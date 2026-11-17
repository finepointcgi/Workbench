using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.AccountingExport;
using Workbench.Application.Companies;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;
using Workbench.Contracts.Security;
using Workbench.Domain.AccountingExport;

namespace Workbench.Application.AccountingLive;

/// <summary>
/// Sends exports straight to QuickBooks Online or Xero. It follows the same rules as the file export, on the same tables:
/// <list type="bullet">
/// <item>Candidates, mappings and the "already exported" marks are the file export's (<see cref="ExportDataLoader"/>, <see cref="MappingResolver"/>,
/// <see cref="AccountingExportedRecord"/>), so a record exported as a file is not pushed, and a pushed one is not exported as a file, unless "again" with a note.</item>
/// <item>The provider's own id for every created record is saved (<see cref="AccountingProviderLink"/>). A record that has one is never created again:
/// a re-run reports it as skipped. Creating is also idempotent at the provider, through a key built from the record ids and attempt numbers.</item>
/// <item>A run saves a result per record. Only records that were created are marked exported; failed ones stay available, and <see cref="RetryFailedAsync"/> sends just those.</item>
/// <item>The run appears in the export history (format <c>qbo-live</c> / <c>xero-live</c>, no file) and in the security log and audit trail.</item>
/// <item>Only one push per target runs at a time (a running run blocks another for 30 minutes), so two people can't both create the same records.</item>
/// </list>
/// </summary>
public sealed class AccountingPushService(
    IWorkbenchDbContextFactory dbFactory,
    IEnumerable<IAccountingPublisher> publishers,
    IEnumerable<IAccountingOAuth> oauths,
    AccountingTokenService tokens,
    IAccountingHttp http,
    IAccountingDelay delay,
    AuditRecorder audit,
    SecurityEventLog securityLog,
    TimeProvider clock,
    CompanyScope? companies = null) : IAccountingPushService
{
    public static readonly TimeSpan RunningLimit = TimeSpan.FromMinutes(30);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private IAccountingPublisher Publisher(AccountingTarget target) => publishers.FirstOrDefault(p => p.Target == target) ?? throw new ArgumentException($"{target} has no live push.");

    private string ProviderName(AccountingTarget target) => oauths.FirstOrDefault(o => o.Target == target)?.Name ?? target.ToString();

    public static string FormatId(AccountingTarget target) => target == AccountingTarget.QuickBooks ? "qbo-live" : "xero-live";

    private sealed record Plan(
        IAccountingPublisher Publisher, AccountingPushRequest Request, ExportDataset? Data, List<ExportCandidate> ToSend, List<(ExportCandidate Candidate, string ProviderId, string? ProviderNumber)> AlreadyLinked,
        int AlreadyExported, IReadOnlyList<AccountingUnmappedDto> Unmapped, IReadOnlyList<AccountingMissingDto> Missing, List<string> Warnings, string? Blocked, AccountingApi? Api);

    private AccountingApi NewApi(AccountingTarget target) => new(target, http, tokens, delay);

    private async Task<Plan> PlanAsync(IWorkbenchDbContext db, AccountingPushRequest request, IReadOnlySet<Guid>? only, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var publisher = Publisher(request.Target);
        if (!publisher.Kinds.Contains(request.Kind))
        {
            throw new ArgumentException($"{ProviderName(request.Target)} can't take {AccountingExportService.KindLabel(request.Kind)} by push. Use the file export.");
        }

        if (request.To < request.From)
        {
            throw new ArgumentException("The period ends before it starts.");
        }

        var note = request.Note?.Trim();
        if (request.Again && (note is null || note.Length < 3))
        {
            throw new ArgumentException("Pushing records again needs a note saying why.");
        }

        var target = request.Target.ToString();
        var kind = request.Kind.ToString();
        var fileRequest = new AccountingFileRequest(FormatId(request.Target), request.Kind, request.From, request.To, request.CompanyId, false, request.Again, note, request.IncludeSubledgerJournals);
        var visible = await (companies?.VisibleCompanyIdsAsync(ct) ?? Task.FromResult<IReadOnlySet<Guid>?>(null));
        var candidates = await ExportDataLoader.CandidatesAsync(db, fileRequest, visible, ct);
        if (only is not null)
        {
            candidates = candidates.Where(c => only.Contains(c.Id)).ToList();
        }

        var ids = candidates.Select(c => c.Id).ToList();
        var exported = (await db.Set<AccountingExportedRecord>().AsNoTracking().Where(r => r.Target == target && r.Kind == kind && ids.Contains(r.RecordId)).Select(r => r.RecordId).ToListAsync(ct)).ToHashSet();
        var links = await db.Set<AccountingProviderLink>().AsNoTracking().Where(l => l.Target == target && l.Kind == kind && ids.Contains(l.RecordId)).ToDictionaryAsync(l => l.RecordId, ct);
        var marked = request.Again ? candidates : candidates.Where(c => !exported.Contains(c.Id)).ToList();
        // A record with a provider id is already in the books whatever else is said: never created again.
        var linked = marked.Where(c => links.ContainsKey(c.Id)).Select(c => (c, links[c.Id].ProviderId, links[c.Id].ProviderNumber)).ToList();
        var toSend = marked.Where(c => !links.ContainsKey(c.Id)).ToList();
        var warnings = new List<string>();
        var leftOut = exported.Count;
        if (!request.Again && leftOut > 0)
        {
            warnings.Add($"{leftOut} record(s) in this period were exported to {ProviderName(request.Target)} before and are left out.");
        }

        if (request.Kind == AccountingExportKind.JournalEntries && !request.IncludeSubledgerJournals)
        {
            warnings.Add("Entries made by invoices, payments and bills are left out; push those documents instead so nothing is counted twice.");
        }

        if (toSend.Count == 0)
        {
            var blocked = candidates.Count == 0 ? "There is nothing to push for this period."
                : linked.Count > 0 ? null
                : $"All {candidates.Count} record(s) in this period were already exported to {ProviderName(request.Target)}. Choose \"Push again\" and give a reason to repeat them.";
            return new Plan(publisher, request, null, toSend, linked, exported.Count, [], [], warnings, blocked, null);
        }

        var mappings = await db.Set<AccountingMapping>().AsNoTracking().Where(m => m.Target == target).ToListAsync(ct);
        var resolver = new MappingResolver(mappings, request.Target);
        var data = await ExportDataLoader.BuildAsync(db, request.Kind, toSend.Select(c => c.Id).ToList(), resolver, ct);
        warnings.AddRange(data.Warnings);
        var unmapped = resolver.Unmapped();
        // Xero invoices take the revenue account, not a product or service, so an unmapped item can't matter there.
        var blockingUnmapped = resolver.Unmapped(blockingOnly: true).Where(u => !(request.Target == AccountingTarget.Xero && u.Kind == AccountingMapKind.Item)).ToList();
        if (blockingUnmapped.Count > 0)
        {
            return new Plan(publisher, request, data, toSend, linked, exported.Count, unmapped, [], warnings, $"{blockingUnmapped.Count} account, customer, vendor or item name{(blockingUnmapped.Count == 1 ? " has" : "s have")} no mapping for {request.Target}. A push needs every name mapped, so nothing is sent under a name the books may not know.", null);
        }

        var api = NewApi(request.Target);
        IReadOnlyList<AccountingMissingDto> missing;
        try
        {
            missing = await publisher.FindMissingAsync(api, new ExportInput(request.Kind, request.From, request.To, data, Now), request.CreateMissingContacts, ct);
        }
        catch (AccountingNotConnectedException ex)
        {
            return new Plan(publisher, request, data, toSend, linked, exported.Count, unmapped, [], warnings, ex.Message, null);
        }
        catch (AccountingApiException ex)
        {
            return new Plan(publisher, request, data, toSend, linked, exported.Count, unmapped, [], warnings, ex.Message, null);
        }

        // Accounts and items must exist in the books; customers and vendors may be created when the user says so; classes only warn.
        var blocking = missing.Where(m => m.Kind is AccountingMapKind.Account or AccountingMapKind.Item || (m.Kind is AccountingMapKind.Customer or AccountingMapKind.Vendor && !m.WillBeCreated)).ToList();
        if (missing.Any(m => m.Kind == AccountingMapKind.Class))
        {
            warnings.Add("Some projects have no matching class or tracking option in the books; those lines are sent without one.");
        }

        var reason = blocking.Count > 0
            ? $"{blocking.Count} name{(blocking.Count == 1 ? " isn't" : "s aren't")} in {ProviderName(request.Target)} yet ({string.Join(", ", blocking.Take(3).Select(b => $"{b.Kind} {b.Name}"))}{(blocking.Count > 3 ? ", ..." : "")}). Create {(blocking.Count == 1 ? "it" : "them")} there, fix the mapping{(blocking.Any(b => b.Kind is AccountingMapKind.Customer or AccountingMapKind.Vendor) ? ", or choose to create missing customers and vendors" : "")}."
            : null;
        return new Plan(publisher, request, data, toSend, linked, exported.Count, unmapped, missing, warnings, reason, api);
    }

    public async Task<AccountingPushPreviewDto> PreviewAsync(AccountingPushRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plan = await PlanAsync(db, request, null, cancellationToken);
        return ToPreview(plan);
    }

    private static AccountingPushPreviewDto ToPreview(Plan p)
    {
        var rows = p.Data is null ? 0 : p.Data.Journals.Sum(j => j.Lines.Count) + p.Data.Invoices.Sum(i => i.Lines.Count) + p.Data.Bills.Sum(b => b.Lines.Count);
        var total = p.Data is null ? 0m : p.Data.Journals.Sum(j => j.Lines.Sum(l => l.Debit)) + p.Data.Invoices.Where(i => !i.IsCreditMemo).Sum(i => i.Total) + p.Data.Bills.Where(b => !b.IsCredit).Sum(b => b.Amount);
        var create = p.Request.CreateMissingContacts;
        return new AccountingPushPreviewDto(p.Request.Target, p.Request.Kind, p.ToSend.Count, p.AlreadyExported, rows, total, p.Unmapped, p.Missing,
            create ? p.Missing.Count(m => m.Kind == AccountingMapKind.Customer) : 0, create ? p.Missing.Count(m => m.Kind == AccountingMapKind.Vendor) : 0,
            p.Warnings, p.Blocked is null && (p.ToSend.Count > 0 || p.AlreadyLinked.Count > 0), p.Blocked);
    }

    public Task<AccountingPushRunDto> PushAsync(AccountingPushRequest request, CancellationToken cancellationToken = default) => RunAsync(request, null, null, cancellationToken);

    public async Task<AccountingPushRunDto> RetryFailedAsync(Guid runId, bool createMissingContacts = false, CancellationToken cancellationToken = default)
    {
        AccountingPushRequest request;
        HashSet<Guid> failed;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var run = await db.Set<AccountingPushRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Push run", runId);
            failed = (await db.Set<AccountingPushItem>().AsNoTracking().Where(i => i.RunId == runId && i.Status == AccountingPushItemStatus.Failed).Select(i => i.RecordId).ToListAsync(cancellationToken)).ToHashSet();
            if (failed.Count == 0)
            {
                throw new ArgumentException("Nothing failed in that run.");
            }

            request = new AccountingPushRequest(Enum.Parse<AccountingTarget>(run.Target), Enum.Parse<AccountingExportKind>(run.Kind), run.PeriodFrom, run.PeriodTo, run.CompanyId, createMissingContacts || run.CreateMissingContacts);
        }

        return await RunAsync(request, failed, runId, cancellationToken);
    }

    private async Task<AccountingPushRunDto> RunAsync(AccountingPushRequest request, IReadOnlySet<Guid>? only, Guid? retryOf, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var target = request.Target.ToString();
        var kind = request.Kind.ToString();
        var now = Now;
        var since = now - RunningLimit;
        if (await db.Set<AccountingPushRun>().AnyAsync(r => r.Target == target && r.Status == "Running" && r.StartedAtUtc > since, ct))
        {
            throw new InvalidOperationException($"A push to {ProviderName(request.Target)} is already running. Wait for it to finish.");
        }

        var plan = await PlanAsync(db, request, only, ct);
        if (plan.Blocked is not null || (plan.ToSend.Count == 0 && plan.AlreadyLinked.Count == 0))
        {
            throw new InvalidOperationException(plan.Blocked ?? "Nothing to push.");
        }

        var runId = Guid.CreateVersion7();
        var note = request.Note?.Trim();
        var run = new AccountingPushRun
        {
            Id = runId, Target = target, Kind = kind, PeriodFrom = request.From, PeriodTo = request.To, CompanyId = request.CompanyId, Status = "Running", Total = plan.ToSend.Count + plan.AlreadyLinked.Count,
            RetryOfRunId = retryOf, CreateMissingContacts = request.CreateMissingContacts, StartedBy = audit.Actor, StartedAtUtc = now,
        };
        db.Set<AccountingPushRun>().Add(run);
        await db.SaveChangesAsync(ct);

        var results = new List<PublishRecordResult>();
        foreach (var (c, providerId, providerNumber) in plan.AlreadyLinked)
        {
            results.Add(new PublishRecordResult(c.Id, c.Number, AccountingPushItemStatus.Skipped, providerId, providerNumber, $"Already in {ProviderName(request.Target)} (id {providerId}); not created again."));
        }

        var contactsCreated = 0;
        string? fatal = null;
        if (plan.ToSend.Count > 0 && plan.Data is not null)
        {
            var ids = plan.ToSend.Select(c => c.Id).ToList();
            var attempts = (await db.Set<AccountingPushItem>().AsNoTracking().Where(i => ids.Contains(i.RecordId) && i.Status == AccountingPushItemStatus.Failed).Select(i => i.RecordId).ToListAsync(ct))
                .GroupBy(i => i).ToDictionary(g => g.Key, g => g.Count());
            try
            {
                var outcome = await plan.Publisher.PublishAsync(plan.Api ?? NewApi(request.Target),
                    new PublishRequest(new ExportInput(request.Kind, request.From, request.To, plan.Data, Now), request.CreateMissingContacts, id => attempts.GetValueOrDefault(id)), ct);
                results.AddRange(outcome.Records);
                contactsCreated = outcome.ContactsCreated;
            }
            catch (Exception ex) when (ex is AccountingApiException or AccountingNotConnectedException)
            {
                fatal = ex.Message;
            }

            // Whatever the provider didn't answer for is failed, so the run always lists every record.
            var answered = results.Select(r => r.RecordId).ToHashSet();
            foreach (var c in plan.ToSend.Where(c => !answered.Contains(c.Id)))
            {
                results.Add(PublishSupport.Failed(c.Id, c.Number, fatal ?? "No answer was recorded for this record. Retry it."));
            }
        }

        return await SaveAsync(db, plan, run, results, contactsCreated, fatal, note, ct);
    }

    private async Task<AccountingPushRunDto> SaveAsync(IWorkbenchDbContext db, Plan plan, AccountingPushRun run, List<PublishRecordResult> results, int contactsCreated, string? fatal, string? note, CancellationToken ct)
    {
        var request = plan.Request;
        var target = run.Target;
        var kind = run.Kind;
        var now = Now;
        var created = results.Count(r => r.Status == AccountingPushItemStatus.Created);
        var skipped = results.Count(r => r.Status == AccountingPushItemStatus.Skipped);
        var failed = results.Count(r => r.Status == AccountingPushItemStatus.Failed);
        var notSent = results.Count(r => r.Status == AccountingPushItemStatus.NotSent);
        var resultIds = results.Select(r => r.RecordId).ToList();
        var attemptsBefore = await db.Set<AccountingPushItem>().AsNoTracking().Where(i => i.Status == AccountingPushItemStatus.Failed && resultIds.Contains(i.RecordId)).Select(i => i.RecordId).ToListAsync(ct);
        var attemptMap = attemptsBefore.GroupBy(i => i).ToDictionary(g => g.Key, g => g.Count());

        foreach (var r in results)
        {
            db.Set<AccountingPushItem>().Add(new AccountingPushItem
            {
                RunId = run.Id, RecordId = r.RecordId, RecordNumber = PublishSupport.Clip(r.Number, 60), Status = r.Status, ProviderId = r.ProviderId, ProviderNumber = PublishSupport.Clip(r.ProviderNumber, 100),
                Message = r.Message, Attempts = attemptMap.GetValueOrDefault(r.RecordId) + (r.Status == AccountingPushItemStatus.Failed ? 1 : 0),
            });
        }

        var done = results.Where(r => r.Status is AccountingPushItemStatus.Created or AccountingPushItemStatus.Skipped && r.ProviderId is not null).ToList();
        var doneIds = done.Select(r => r.RecordId).ToList();
        var existingLinks = await db.Set<AccountingProviderLink>().Where(l => l.Target == target && l.Kind == kind && doneIds.Contains(l.RecordId)).Select(l => l.RecordId).ToListAsync(ct);
        foreach (var r in done.Where(r => r.Status == AccountingPushItemStatus.Created && !existingLinks.Contains(r.RecordId)))
        {
            db.Set<AccountingProviderLink>().Add(new AccountingProviderLink { Target = target, Kind = kind, RecordId = r.RecordId, ProviderId = r.ProviderId!, ProviderNumber = PublishSupport.Clip(r.ProviderNumber, 100), RunId = run.Id, CreatedAtUtc = now });
        }

        var marks = await db.Set<AccountingExportedRecord>().Where(m => m.Target == target && m.Kind == kind && doneIds.Contains(m.RecordId)).ToDictionaryAsync(m => m.RecordId, ct);
        foreach (var r in done)
        {
            if (marks.TryGetValue(r.RecordId, out var mark))
            {
                mark.LastRunId = run.Id;
                mark.ExportCount++;
                mark.LastExportedAtUtc = now;
            }
            else
            {
                db.Set<AccountingExportedRecord>().Add(new AccountingExportedRecord { Target = target, Kind = kind, RecordId = r.RecordId, RecordNumber = PublishSupport.Clip(r.Number, 60), FirstRunId = run.Id, LastRunId = run.Id, ExportCount = 1, FirstExportedAtUtc = now, LastExportedAtUtc = now });
            }
        }

        var status = failed == 0 ? AccountingPushRunStatus.Completed : created + skipped == 0 ? AccountingPushRunStatus.Failed : AccountingPushRunStatus.PartlyFailed;
        var message = fatal ?? $"{created} created, {skipped} already there, {failed} failed{(notSent > 0 ? $", {notSent} not sent" : "")}{(contactsCreated > 0 ? $"; {contactsCreated} contact(s) created" : "")}.";
        run.Status = status;
        run.Created = created;
        run.Skipped = skipped;
        run.Failed = failed;
        run.NotSent = notSent;
        run.ContactsCreated = contactsCreated;
        run.Message = PublishSupport.Clip(message, 1000);
        run.FinishedAtUtc = now;

        var warnings = plan.Warnings.Concat(results.Where(r => r.Status == AccountingPushItemStatus.NotSent).Select(r => $"{r.Number}: {r.Message}")).Distinct().ToList();
        var providerName = ProviderName(request.Target);
        db.Set<AccountingExportRun>().Add(new AccountingExportRun
        {
            Id = run.Id, Target = target, FormatId = FormatId(request.Target), Kind = kind, PeriodFrom = run.PeriodFrom, PeriodTo = run.PeriodTo, CompanyId = run.CompanyId,
            FileName = $"Pushed to {providerName}", ContentType = "application/vnd.workbench.push", RowCount = results.Count, RecordCount = created + skipped, ControlTotal = ControlTotal(plan.Data, done.Select(d => d.RecordId).ToHashSet()),
            Checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", results.OrderBy(r => r.RecordId).Select(r => $"{r.RecordId:N}|{r.Status}|{r.ProviderId}"))))),
            WarningsJson = JsonSerializer.Serialize(warnings, new JsonSerializerOptions(JsonSerializerDefaults.Web)), WarningCount = warnings.Count, UnmappedAllowed = false, UnmappedCount = 0,
            IncludedSubledgerJournals = request.Kind == AccountingExportKind.JournalEntries && request.IncludeSubledgerJournals, IsRepeat = request.Again && plan.AlreadyExported > 0, Note = note,
            CreatedBy = audit.Actor, CreatedAtUtc = now,
        });

        var connection = await db.Set<AccountingConnection>().FirstOrDefaultAsync(c => c.Target == target, ct);
        if (connection is not null)
        {
            connection.LastSyncUtc = now;
            connection.LastSyncStatus = status;
            connection.LastSyncMessage = PublishSupport.Clip(message, 1000);
        }

        audit.Record(db, AccountingExportService.RunEntity, run.Id, "Pushed", after: new { request.Target, Kind = kind, request.From, request.To, status, created, skipped, failed, notSent, contactsCreated, retryOf = run.RetryOfRunId }, reason: note);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException($"{created} record(s) reached {providerName} but couldn't be recorded here ({ex.GetBaseException().Message}). Don't push again before checking {providerName}: a retry sends the same idempotency key, so the provider will not create them twice.", ex);
        }

        await securityLog.RecordAsync(AccountingExportService.SecurityEventExport, $"{kind} {run.PeriodFrom:yyyy-MM-dd}..{run.PeriodTo:yyyy-MM-dd}", failed == 0 ? SecurityOutcomes.Success : SecurityOutcomes.Failure,
            $"push to {providerName}: {message}", cancellationToken: ct);
        return ToDto(run, results.Select(r => new AccountingPushItemDto(r.RecordId, r.Number, r.Status, r.ProviderId, r.ProviderNumber, r.Message, attemptMap.GetValueOrDefault(r.RecordId) + (r.Status == AccountingPushItemStatus.Failed ? 1 : 0))).ToList());
    }

    private static decimal ControlTotal(ExportDataset? data, HashSet<Guid> ids) => data is null ? 0m
        : data.Journals.Where(j => ids.Contains(j.Id)).Sum(j => j.Lines.Sum(l => l.Debit)) + data.Invoices.Where(i => ids.Contains(i.Id)).Sum(i => i.Total) + data.Bills.Where(b => ids.Contains(b.Id)).Sum(b => b.Amount);

    public async Task<IReadOnlyList<AccountingPushRunDto>> ListRunsAsync(AccountingTarget? target = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<AccountingPushRun>().AsNoTracking().AsQueryable();
        if (target is { } t)
        {
            var name = t.ToString();
            q = q.Where(r => r.Target == name);
        }

        var rows = await q.OrderByDescending(r => r.StartedAtUtc).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
        return rows.Select(r => ToDto(r, [])).ToList();
    }

    public async Task<AccountingPushRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<AccountingPushRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Push run", runId);
        var items = await db.Set<AccountingPushItem>().AsNoTracking().Where(i => i.RunId == runId).OrderBy(i => i.Status).ThenBy(i => i.RecordNumber).ToListAsync(cancellationToken);
        return ToDto(run, items.Select(i => new AccountingPushItemDto(i.RecordId, i.RecordNumber, i.Status, i.ProviderId, i.ProviderNumber, i.Message, i.Attempts)).ToList());
    }

    private static AccountingPushRunDto ToDto(AccountingPushRun r, IReadOnlyList<AccountingPushItemDto> items) =>
        new(r.Id, Enum.Parse<AccountingTarget>(r.Target), Enum.Parse<AccountingExportKind>(r.Kind), r.PeriodFrom, r.PeriodTo, r.Status, r.Total, r.Created, r.Skipped, r.Failed, r.NotSent, r.ContactsCreated,
            r.RetryOfRunId, r.StartedBy, r.StartedAtUtc, r.FinishedAtUtc, r.Message, items);
}
