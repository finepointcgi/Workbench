using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Accounting;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Planning;
using Workbench.Domain.Accounting;
using Workbench.Domain.Planning;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Planning;

/// <summary>
/// Recurring journals (Costpoint's recurring journal entries): templates, their schedule, and generating the due
/// entries into the ledger. Each occurrence is its own unit of work: the entry (Source = Recurring, source id = the
/// template, revision = the occurrence) and its run row save together, and a unique run per occurrence stops a manual
/// run and the background job from both creating it.
/// </summary>
public sealed class RecurringJournalService(
    IWorkbenchDbContextFactory dbFactory,
    GeneralLedger ledger,
    AuditRecorder audit,
    TimeProvider clock) : IRecurringJournalService
{
    public const string AuditEntityType = nameof(RecurringJournalTemplate);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<IReadOnlyList<RecurringTemplateDto>> ListTemplatesAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var templates = await db.Set<RecurringJournalTemplate>().AsNoTracking().Include(t => t.Lines)
            .Where(t => includeInactive || t.IsActive).ToListAsync(cancellationToken);
        var runs = (await db.Set<RecurringJournalRun>().AsNoTracking().ToListAsync(cancellationToken)).ToLookup(r => r.TemplateId);
        var lookups = await LedgerLookups.LoadAsync(db, cancellationToken);
        return templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => Map(t, runs[t.Id].ToList(), lookups)).ToList();
    }

    public async Task<RecurringTemplateDto> GetTemplateAsync(Guid templateId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await MapAsync(db, await LoadAsync(db, templateId, false, cancellationToken), cancellationToken);
    }

    public async Task<RecurringTemplateDto> CreateTemplateAsync(SaveRecurringTemplateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var template = new RecurringJournalTemplate { Name = "", Description = "", CreatedBy = audit.Actor, CreatedAtUtc = UtcNow };
        await ApplyAsync(db, template, request, cancellationToken);
        db.Set<RecurringJournalTemplate>().Add(template);
        audit.Record(db, AuditEntityType, template.Id, "Created", after: Snapshot(template));
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(db, template, cancellationToken);
    }

    public async Task<RecurringTemplateDto> UpdateTemplateAsync(Guid templateId, SaveRecurringTemplateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var template = await LoadAsync(db, templateId, true, cancellationToken);
        var before = Snapshot(template);
        db.Set<RecurringJournalTemplateLine>().RemoveRange(template.Lines);
        template.Lines.Clear();
        await ApplyAsync(db, template, request, cancellationToken);
        db.Set<RecurringJournalTemplateLine>().AddRange(template.Lines);
        template.UpdatedBy = audit.Actor;
        template.UpdatedAtUtc = UtcNow;
        audit.Record(db, AuditEntityType, template.Id, "Updated", before, Snapshot(template));
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(db, template, cancellationToken);
    }

    public async Task<RecurringTemplateDto> SetActiveAsync(Guid templateId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var template = await LoadAsync(db, templateId, true, cancellationToken);
        if (template.IsActive != isActive)
        {
            template.IsActive = isActive;
            template.UpdatedBy = audit.Actor;
            template.UpdatedAtUtc = UtcNow;
            audit.Record(db, AuditEntityType, template.Id, isActive ? "Resumed" : "Paused");
            await db.SaveChangesAsync(cancellationToken);
        }

        return await MapAsync(db, template, cancellationToken);
    }

    public async Task<IReadOnlyList<RecurringRunDto>> GetScheduleAsync(Guid templateId, int upcoming = 6, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var template = await LoadAsync(db, templateId, false, cancellationToken);
        var runs = await db.Set<RecurringJournalRun>().AsNoTracking().Where(r => r.TemplateId == templateId).ToDictionaryAsync(r => r.Occurrence, cancellationToken);
        var lookups = await RunLookups.LoadAsync(db, runs.Values, cancellationToken);
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(cancellationToken);
        var result = new List<RecurringRunDto>();
        var future = 0;
        var amount = template.Amounts().Sum(a => a.Debit);
        foreach (var (number, date) in template.Schedule(DateOnly.MaxValue))
        {
            if (date > Today && ++future > Math.Clamp(upcoming, 0, 60))
            {
                break;
            }

            if (result.Count >= 600)
            {
                break;
            }

            result.Add(runs.TryGetValue(number, out var run)
                ? lookups.Map(run, template.Name)
                : new RecurringRunDto(null, template.Id, template.Name, number, date, periods.FirstOrDefault(p => p.Start <= date && date <= p.End)?.Name,
                    null, null, null, null, null, amount, null, null, null));
        }

        return result;
    }

    public async Task<IReadOnlyList<RecurringRunDto>> ListHistoryAsync(Guid? templateId = null, int take = 200, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var runs = await db.Set<RecurringJournalRun>().AsNoTracking().Where(r => templateId == null || r.TemplateId == templateId)
            .OrderByDescending(r => r.GeneratedAtUtc).Take(Math.Clamp(take, 1, 2000)).ToListAsync(cancellationToken);
        var names = await db.Set<RecurringJournalTemplate>().AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        var lookups = await RunLookups.LoadAsync(db, runs, cancellationToken);
        return runs.OrderByDescending(r => r.ScheduledDate).ThenBy(r => names.GetValueOrDefault(r.TemplateId), StringComparer.OrdinalIgnoreCase)
            .Select(r => lookups.Map(r, names.GetValueOrDefault(r.TemplateId) ?? "")).ToList();
    }

    public async Task<RecurringGenerationResultDto> GenerateDueAsync(GenerateRecurringRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<RecurringJournalTemplate> templates;
        List<FiscalPeriod> periods;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            periods = (await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(cancellationToken)).OrderBy(p => p.Start).ToList();
            var query = db.Set<RecurringJournalTemplate>().AsNoTracking().Include(t => t.Lines).AsQueryable();
            if (request.TemplateId is { } tid)
            {
                var one = await query.FirstOrDefaultAsync(t => t.Id == tid, cancellationToken) ?? throw WorkbenchNotFoundException.For("Recurring journal", tid);
                if (!one.IsActive)
                {
                    throw new ArgumentException($"{one.Name} is paused; resume it to generate its entries.");
                }

                templates = [one];
            }
            else
            {
                templates = await query.Where(t => t.IsActive).ToListAsync(cancellationToken);
            }
        }

        DateOnly from = DateOnly.MinValue, through = Today;
        if (request.FiscalPeriodId is { } pid)
        {
            var period = periods.FirstOrDefault(p => p.Id == pid) ?? throw WorkbenchNotFoundException.For("Fiscal period", pid);
            (from, through) = (period.Start, period.End);
        }

        var tally = new Tally();
        foreach (var template in templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (number, date) in template.Schedule(through).Where(o => o.Date >= from).ToList())
            {
                await GenerateOccurrenceAsync(template, number, date, periods, tally, cancellationToken);
            }

            if (template.AutoReverse)
            {
                await ReversePendingAsync(template, periods, tally, cancellationToken);
            }
        }

        if (templates.Count == 0)
        {
            tally.Messages.Add("No active recurring journals.");
        }
        else if (tally.Touched.Count == 0 && tally.AlreadyDone == 0)
        {
            tally.Messages.Add(request.FiscalPeriodId is null ? "Nothing is due up to today." : "Nothing is scheduled in this period.");
        }

        await using var read = await dbFactory.CreateAsync(cancellationToken);
        var touched = await read.Set<RecurringJournalRun>().AsNoTracking().Where(r => tally.Touched.Contains(r.Id)).ToListAsync(cancellationToken);
        var lookups = await RunLookups.LoadAsync(read, touched, cancellationToken);
        var names = templates.ToDictionary(t => t.Id, t => t.Name);
        return new RecurringGenerationResultDto(tally.Drafted, tally.Posted, tally.Reversed, tally.Skipped, tally.AlreadyDone,
            touched.OrderBy(r => r.ScheduledDate).ThenBy(r => names.GetValueOrDefault(r.TemplateId)).Select(r => lookups.Map(r, names.GetValueOrDefault(r.TemplateId) ?? "")).ToList(),
            tally.Messages);
    }

    private sealed class Tally
    {
        public int Drafted, Posted, Reversed, Skipped, AlreadyDone;
        public HashSet<Guid> Touched { get; } = [];
        public List<string> Messages { get; } = [];
    }

    private async Task GenerateOccurrenceAsync(RecurringJournalTemplate template, int number, DateOnly date, IReadOnlyList<FiscalPeriod> periods, Tally tally, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var run = await db.Set<RecurringJournalRun>().FirstOrDefaultAsync(r => r.TemplateId == template.Id && r.Occurrence == number, ct);
        if (run is { JournalEntryId: { } existing } && await db.Set<JournalEntry>().AnyAsync(e => e.Id == existing, ct))
        {
            tally.AlreadyDone++;
            return;
        }

        run ??= db.Set<RecurringJournalRun>().Add(new RecurringJournalRun { TemplateId = template.Id, Occurrence = number, GeneratedBy = audit.Actor }).Entity;
        run.ScheduledDate = date;
        run.GeneratedBy = audit.Actor;
        run.GeneratedAtUtc = UtcNow;
        run.JournalEntryId = null;
        run.ReversalEntryId = null;
        var period = periods.FirstOrDefault(p => p.Start <= date && date <= p.End);
        run.FiscalPeriodId = period?.Id;
        var amounts = template.Amounts();
        run.Amount = amounts.Sum(a => a.Debit);

        string? skip = period is null ? $"{template.Name}: no fiscal period covers {date:yyyy-MM-dd}. Add the fiscal year in Setup › Fiscal calendar."
            : period.Status != PeriodStatus.Open ? $"{template.Name}: period {period.Name} is closed, so the {date:yyyy-MM-dd} entry wasn't created. Reopen the period to generate it, or enter it by hand in an open period."
            : null;
        JournalEntry? entry = null;
        if (skip is null)
        {
            entry = new JournalEntry
            {
                Date = date,
                FiscalPeriodId = period!.Id,
                Source = JournalSource.Recurring,
                SourceId = template.Id,
                SourceRevision = number,
                Description = Clip($"{template.Description} ({date:MMM yyyy})", 500),
                CreatedBy = audit.Actor,
                CreatedAtUtc = UtcNow,
            };
            foreach (var (line, debit, credit) in amounts)
            {
                entry.Lines.Add(new JournalLine
                {
                    JournalEntryId = entry.Id, LineNumber = entry.Lines.Count + 1, AccountId = line.AccountId, OrgUnitId = line.OrgUnitId,
                    ProjectId = line.ProjectId, WbsElementId = line.WbsElementId, Debit = debit, Credit = credit, Memo = line.Memo,
                });
            }

            try
            {
                entry.EnsurePostable();
                await GeneralLedger.ValidateLinesAsync(db, entry, ct);
            }
            catch (ArgumentException ex)
            {
                skip = $"{template.Name}: the {date:yyyy-MM-dd} entry can't be created. {ex.Message} Fix the template and generate again.";
                entry = null;
            }
        }

        if (skip is not null)
        {
            run.Status = RecurringRunStatus.Skipped;
            run.Message = Clip(skip, 1000);
            tally.Skipped++;
            tally.Messages.Add(skip);
        }
        else
        {
            db.Set<JournalEntry>().Add(entry!);
            run.JournalEntryId = entry!.Id;
            run.Message = null;
            if (template.PostAutomatically)
            {
                await ledger.PostAsync(db, entry, ct);
                run.Status = RecurringRunStatus.Posted;
                tally.Posted++;
                if (template.AutoReverse)
                {
                    await TryReverseAsync(db, template, run, entry, periods, tally, ct);
                }
            }
            else
            {
                run.Status = RecurringRunStatus.Draft;
                audit.Record(db, GeneralLedger.AuditEntityType, entry.Id, "Created", after: new { entry.Date, entry.Description, Lines = entry.Lines.Count, Recurring = template.Name });
                tally.Drafted++;
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
            tally.Touched.Add(run.Id);
        }
        catch (DbUpdateException)
        {
            // Another run (the background job, or someone else) created this occurrence at the same moment.
            tally.AlreadyDone++;
            if (skip is null)
            {
                tally.Drafted -= template.PostAutomatically ? 0 : 1;
                tally.Posted -= template.PostAutomatically ? 1 : 0;
            }
        }
    }

    /// <summary>Reverses posted entries of an auto-reversing template that aren't reversed yet (a draft posted later, or a next period that wasn't open).</summary>
    private async Task ReversePendingAsync(RecurringJournalTemplate template, IReadOnlyList<FiscalPeriod> periods, Tally tally, CancellationToken ct)
    {
        await using var probe = await dbFactory.CreateAsync(ct);
        var pending = await probe.Set<RecurringJournalRun>().AsNoTracking()
            .Where(r => r.TemplateId == template.Id && r.JournalEntryId != null && r.ReversalEntryId == null).Select(r => r.Id).ToListAsync(ct);
        foreach (var runId in pending)
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var run = await db.Set<RecurringJournalRun>().FirstAsync(r => r.Id == runId, ct);
            var entry = await db.Set<JournalEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == run.JournalEntryId, ct);
            if (entry is null || entry.Status == JournalStatus.Draft)
            {
                continue;
            }

            if (entry.Status == JournalStatus.Reversed)
            {
                // Reversed by hand: link it.
                run.ReversalEntryId = entry.ReversedById;
                run.Status = RecurringRunStatus.Posted;
                run.Message = null;
            }
            else
            {
                run.Status = RecurringRunStatus.Posted;
                if (!await TryReverseAsync(db, template, run, entry, periods, tally, ct))
                {
                    await db.SaveChangesAsync(ct);
                    continue;
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
                tally.Touched.Add(run.Id);
            }
            catch (DbUpdateException)
            {
                tally.AlreadyDone++;
            }
        }
    }

    /// <summary>Posts the reversal on the first day of the period after the entry's, when that period exists and is open.</summary>
    private async Task<bool> TryReverseAsync(IWorkbenchDbContext db, RecurringJournalTemplate template, RecurringJournalRun run, JournalEntry entry,
        IReadOnlyList<FiscalPeriod> periods, Tally tally, CancellationToken ct)
    {
        var current = periods.FirstOrDefault(p => p.Id == entry.FiscalPeriodId);
        var next = current is null ? null : periods.FirstOrDefault(p => p.Start > current.End);
        if (next is null || next.Status != PeriodStatus.Open)
        {
            var note = next is null
                ? $"{template.Name}: {entry.Number} will be reversed once the next fiscal period exists."
                : $"{template.Name}: {entry.Number} will be reversed when period {next.Name} is open.";
            if (run.Message != note)
            {
                run.Message = note;
                tally.Messages.Add(note);
            }

            return false;
        }

        var reversal = await ledger.ReverseAsync(db, entry, next.Start, $"Automatic reversal ({template.Name})", ct);
        run.ReversalEntryId = reversal.Id;
        run.Message = null;
        tally.Reversed++;
        return true;
    }

    public async Task<RecurringSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var s = await db.Set<RecurringJournalSettings>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return new RecurringSettingsDto(s?.AutoGenerate ?? false, s?.LastAutoRunUtc, s?.LastAutoRunSummary);
    }

    public async Task<RecurringSettingsDto> SaveSettingsAsync(SaveRecurringSettingsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var s = await db.Set<RecurringJournalSettings>().FirstOrDefaultAsync(cancellationToken);
        if (s is null)
        {
            s = new RecurringJournalSettings();
            db.Set<RecurringJournalSettings>().Add(s);
        }

        if (s.AutoGenerate != request.AutoGenerate)
        {
            audit.Record(db, "RecurringJournalSettings", s.Id, "Updated", new { s.AutoGenerate }, new { request.AutoGenerate });
        }

        s.AutoGenerate = request.AutoGenerate;
        s.UpdatedBy = audit.Actor;
        s.UpdatedAtUtc = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new RecurringSettingsDto(s.AutoGenerate, s.LastAutoRunUtc, s.LastAutoRunSummary);
    }

    /// <summary>The background job: generates everything due up to today when the setting is on, and records the outcome.</summary>
    internal async Task<RecurringGenerationResultDto?> RunScheduledAsync(CancellationToken ct)
    {
        if (!(await GetSettingsAsync(ct)).AutoGenerate)
        {
            return null;
        }

        var result = await GenerateDueAsync(new GenerateRecurringRequest(), ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var s = await db.Set<RecurringJournalSettings>().FirstOrDefaultAsync(ct);
        if (s is not null)
        {
            s.LastAutoRunUtc = UtcNow;
            s.LastAutoRunSummary = Clip(Summary(result), 1000);
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    public static string Summary(RecurringGenerationResultDto r) =>
        $"{r.Posted} posted, {r.Drafted} drafted, {r.Reversed} reversed, {r.Skipped} skipped" + (r.Skipped > 0 ? $": {string.Join(" ", r.Messages.Take(3))}" : ".");

    // ---- Validation ----

    private static async Task ApplyAsync(IWorkbenchDbContext db, RecurringJournalTemplate t, SaveRecurringTemplateRequest r, CancellationToken ct)
    {
        t.Name = PlanningMapping.Clean(r.Name, 200, "Name", required: true);
        t.Description = PlanningMapping.Clean(r.Description, 450, "Description", required: true);
        if (await db.Set<RecurringJournalTemplate>().AnyAsync(x => x.Name == t.Name && x.Id != t.Id, ct))
        {
            throw new ArgumentException($"A recurring journal named \"{t.Name}\" already exists.");
        }

        if (r.EndDate is { } end && end < r.StartDate)
        {
            throw new ArgumentException("The end date is before the start date.");
        }

        if (r.Occurrences is < 1 or > 600)
        {
            throw new ArgumentException("The number of occurrences is 1 to 600.");
        }

        // Auto-reversing drafts are allowed: they're reversed by the next generation after someone posts them.
        t.Frequency = r.Frequency.ToDomain();
        t.PostingDay = r.PostingDay.ToDomain();
        t.StartDate = r.StartDate;
        t.EndDate = r.EndDate;
        t.Occurrences = r.Occurrences;
        t.AmountMode = r.AmountMode.ToDomain();
        t.PostAutomatically = r.PostAutomatically;
        t.AutoReverse = r.AutoReverse;

        var lines = r.Lines ?? [];
        if (lines.Count < 2)
        {
            throw new ArgumentException("A recurring journal needs at least two lines.");
        }

        if (lines.Count > 100)
        {
            throw new ArgumentException("A recurring journal can have at most 100 lines.");
        }

        if (!lines.Any(l => l.Side == LineSide.Debit) || !lines.Any(l => l.Side == LineSide.Credit))
        {
            throw new ArgumentException("A recurring journal needs at least one debit line and one credit line.");
        }

        if (t.AmountMode == RecurringAmountMode.Percentage)
        {
            PlanningMapping.RequireCents(r.BaseAmount, "Base amount");
            if (r.BaseAmount <= 0)
            {
                throw new ArgumentException("Enter the base amount the percentages split.");
            }

            foreach (var side in new[] { LineSide.Debit, LineSide.Credit })
            {
                var sum = lines.Where(l => l.Side == side).Sum(l => l.Percent);
                if (sum != 100m)
                {
                    throw new ArgumentException($"The {side.ToString().ToLowerInvariant()} percentages add up to {sum:0.####}%; they must add up to 100%.");
                }
            }
        }

        t.BaseAmount = t.AmountMode == RecurringAmountMode.Percentage ? r.BaseAmount : 0;
        var number = 0;
        foreach (var input in lines)
        {
            number++;
            if (t.AmountMode == RecurringAmountMode.Fixed)
            {
                PlanningMapping.RequireCents(input.Amount, $"Line {number}");
                if (input.Amount <= 0)
                {
                    throw new ArgumentException($"Line {number}: enter an amount over zero.");
                }
            }
            else if (input.Percent <= 0 || input.Percent > 100 || decimal.Round(input.Percent, 4) != input.Percent)
            {
                throw new ArgumentException($"Line {number}: the percentage is over 0 and at most 100 (up to four decimals).");
            }

            t.Lines.Add(new RecurringJournalTemplateLine
            {
                TemplateId = t.Id, LineNumber = number, AccountId = input.AccountId, OrgUnitId = input.OrgUnitId, ProjectId = input.ProjectId,
                WbsElementId = input.WbsElementId, Side = input.Side.ToDomain(),
                Amount = t.AmountMode == RecurringAmountMode.Fixed ? input.Amount : 0,
                Percent = t.AmountMode == RecurringAmountMode.Percentage ? input.Percent : 0,
                Memo = string.IsNullOrWhiteSpace(input.Memo) ? null : PlanningMapping.Clean(input.Memo, 500, $"Line {number} memo"),
            });
        }

        // The same checks the ledger makes when posting: balance, active detail accounts and consistent segments.
        var probe = new JournalEntry { Description = t.Description, CreatedBy = "", Date = t.StartDate };
        foreach (var (line, debit, credit) in t.Amounts())
        {
            probe.Lines.Add(new JournalLine { LineNumber = line.LineNumber, AccountId = line.AccountId, OrgUnitId = line.OrgUnitId, ProjectId = line.ProjectId, WbsElementId = line.WbsElementId, Debit = debit, Credit = credit });
        }

        probe.EnsurePostable();
        await GeneralLedger.ValidateLinesAsync(db, probe, ct);
        foreach (var line in t.Lines)
        {
            line.ProjectId = probe.Lines.First(l => l.LineNumber == line.LineNumber).ProjectId;
        }
    }

    // ---- Mapping ----

    private static async Task<RecurringJournalTemplate> LoadAsync(IWorkbenchDbContext db, Guid id, bool track, CancellationToken ct)
    {
        var q = db.Set<RecurringJournalTemplate>().Include(t => t.Lines).AsQueryable();
        return await (track ? q : q.AsNoTracking()).FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Recurring journal", id);
    }

    private async Task<RecurringTemplateDto> MapAsync(IWorkbenchDbContext db, RecurringJournalTemplate t, CancellationToken ct)
    {
        var runs = await db.Set<RecurringJournalRun>().AsNoTracking().Where(r => r.TemplateId == t.Id).ToListAsync(ct);
        return Map(t, runs, await LedgerLookups.LoadAsync(db, ct));
    }

    private static RecurringTemplateDto Map(RecurringJournalTemplate t, IReadOnlyList<RecurringJournalRun> runs, LedgerLookups lookups)
    {
        var done = runs.Where(r => r.JournalEntryId != null).Select(r => r.Occurrence).ToHashSet();
        DateOnly? next = t.IsActive ? t.Schedule(DateOnly.MaxValue).Take(1200).Where(o => !done.Contains(o.Number)).Select(o => (DateOnly?)o.Date).FirstOrDefault() : null;
        return new RecurringTemplateDto(t.Id, t.Name, t.Description, t.Frequency.ToContract(), t.PostingDay.ToContract(), t.StartDate, t.EndDate, t.Occurrences,
            t.AmountMode.ToContract(), t.BaseAmount, t.PostAutomatically, t.AutoReverse, t.IsActive, t.Amounts().Sum(a => a.Debit), done.Count, next,
            t.Lines.OrderBy(l => l.LineNumber).Select(l =>
            {
                lookups.Accounts.TryGetValue(l.AccountId, out var account);
                return new RecurringLineDto(l.LineNumber, l.AccountId, account is null ? "?" : $"{account.Number} {account.Name}",
                    l.OrgUnitId, l.OrgUnitId is { } o ? lookups.OrgUnits.GetValueOrDefault(o) : null,
                    l.ProjectId, l.ProjectId is { } p ? lookups.Projects.GetValueOrDefault(p) : null,
                    l.WbsElementId, l.WbsElementId is { } w && lookups.Wbs.TryGetValue(w, out var wbs) ? wbs.Code : null,
                    l.Side.ToContract(), l.Amount, l.Percent, l.Memo);
            }).ToList(),
            t.CreatedBy, t.CreatedAtUtc);
    }

    private static object Snapshot(RecurringJournalTemplate t) => new
    {
        t.Name, t.Description, Frequency = t.Frequency.ToString(), PostingDay = t.PostingDay.ToString(), t.StartDate, t.EndDate, t.Occurrences,
        AmountMode = t.AmountMode.ToString(), t.BaseAmount, t.PostAutomatically, t.AutoReverse, t.IsActive,
        Lines = t.Lines.OrderBy(l => l.LineNumber).Select(l => new { l.AccountId, l.OrgUnitId, l.ProjectId, l.WbsElementId, Side = l.Side.ToString(), l.Amount, l.Percent }).ToList(),
    };

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>Entry numbers and period names for a set of runs.</summary>
    private sealed class RunLookups
    {
        private Dictionary<Guid, string> _entries = [];
        private Dictionary<Guid, string> _periods = [];

        public static async Task<RunLookups> LoadAsync(IWorkbenchDbContext db, IEnumerable<RecurringJournalRun> runs, CancellationToken ct)
        {
            var list = runs.ToList();
            var ids = list.SelectMany(r => new[] { r.JournalEntryId, r.ReversalEntryId }).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
            return new RunLookups
            {
                _entries = ids.Count == 0 ? [] : await db.Set<JournalEntry>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct),
                _periods = await db.Set<FiscalPeriod>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct),
            };
        }

        public RecurringRunDto Map(RecurringJournalRun r, string templateName)
        {
            // A draft deleted from the journal leaves the run without an entry: it's generated again next time.
            var entryGone = r.JournalEntryId is { } eid && !_entries.ContainsKey(eid);
            return new RecurringRunDto(r.Id, r.TemplateId, templateName, r.Occurrence, r.ScheduledDate,
                r.FiscalPeriodId is { } p ? _periods.GetValueOrDefault(p) : null,
                entryGone ? null : r.Status.ToContract(),
                entryGone ? null : r.JournalEntryId, r.JournalEntryId is { } e ? _entries.GetValueOrDefault(e) : null,
                r.ReversalEntryId, r.ReversalEntryId is { } rv ? _entries.GetValueOrDefault(rv) : null,
                r.Amount, entryGone ? "The draft was deleted; it's created again the next time entries are generated." : r.Message, r.GeneratedBy, r.GeneratedAtUtc);
        }
    }
}

/// <summary>
/// Every six hours, per tenant, as Workbench System: generates the recurring journal entries due up to today, when the
/// organization turned that on (Finance › Accounting › Recurring journals). Through the service, not its permission decorator.
/// </summary>
public sealed class RecurringJournalBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, TimeProvider clock, ILogger<RecurringJournalBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = SystemExecution.CreateScope(scopes);
                    var result = await scope.ServiceProvider.GetRequiredService<RecurringJournalService>().RunScheduledAsync(ct);
                    if (result is not null && result.Posted + result.Drafted + result.Reversed + result.Skipped > 0)
                    {
                        logger.LogInformation("Recurring journals: {Summary}", RecurringJournalService.Summary(result));
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Generating recurring journal entries failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
