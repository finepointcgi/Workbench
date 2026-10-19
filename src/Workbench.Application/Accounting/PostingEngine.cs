using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Financials;
using Workbench.Application.Odcs;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Accounting;

/// <summary>What one posting call did.</summary>
public sealed record PostingOutcome(int Posted, int Reversed, int AlreadyPosted, IReadOnlyList<string> Problems)
{
    public static readonly PostingOutcome Nothing = new(0, 0, 0, []);

    public static PostingOutcome Problem(string problem) => new(0, 0, 0, [problem]);

    public PostingOutcome Add(PostingOutcome other) =>
        new(Posted + other.Posted, Reversed + other.Reversed, AlreadyPosted + other.AlreadyPosted, [.. Problems, .. other.Problems]);
}

/// <summary>
/// Turns approved timesheets and ODCs into journal entries and reverses them when their source is reopened.
/// <para>
/// Idempotent per source revision: an entry carries its source id and revision (a timesheet's <c>Revision</c>, an ODC's
/// reopen count), posting skips a source that already has a posted entry for its current revision, and a unique index
/// on (source, id, revision, period) stops a concurrent retry from double-posting.
/// </para>
/// <para>
/// This is the unchecked engine: the approval decorators call it for whoever approves (a supervisor needn't be in
/// Finance), while <see cref="IPostingService"/> is the Finance-only face for people.
/// </para>
/// </summary>
public sealed class PostingEngine(IWorkbenchDbContextFactory dbFactory, GeneralLedger ledger, ILogger<PostingEngine> logger)
{
    // ---- Posting one source ----

    /// <summary>Posts an approved timesheet's labor; reverses entries for an older revision first.</summary>
    public async Task<PostingOutcome> PostTimesheetAsync(Guid timesheetId, bool redirectClosedPeriods = false, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var sheet = await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == timesheetId, ct)
                ?? throw WorkbenchNotFoundException.For("Timesheet", timesheetId);
            if (sheet.Status != TimesheetStatus.Approved)
            {
                return PostingOutcome.Problem($"Timesheet for the week of {sheet.PeriodStart:yyyy-MM-dd} isn't approved, so it can't be posted.");
            }

            var context = await PostingContext.LoadAsync(db, redirectClosedPeriods, ct);
            try
            {
                return await PostAsync(db, JournalSource.Timesheet, sheet.Id, sheet.Revision,
                    () => PlanTimesheetAsync(db, sheet, context, ct), context, ct);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Lost a race with another posting (same source, or the next entry number): look again.
            }
        }
    }

    /// <summary>Posts an approved ODC or expense line.</summary>
    public async Task<PostingOutcome> PostOdcAsync(Guid odcId, bool redirectClosedPeriods = false, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbFactory.CreateAsync(ct);
            var odc = await db.Set<OdcEntry>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == odcId, ct)
                ?? throw WorkbenchNotFoundException.For("ODC", odcId);
            if (odc.Status != OdcStatus.Approved)
            {
                return PostingOutcome.Problem($"\"{odc.Description}\" isn't approved, so it can't be posted.");
            }

            var context = await PostingContext.LoadAsync(db, redirectClosedPeriods, ct);
            var revision = (await OdcRevisionsAsync(db, [odc], ct))[odc.Id];
            try
            {
                return await PostAsync(db, JournalSource.Odc, odc.Id, revision, () => Task.FromResult(PlanOdc(odc, revision, context)), context, ct);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
            }
        }
    }

    /// <summary>Reverses every posted entry of a source (after it was reopened). A no-op when nothing is posted.</summary>
    public async Task<PostingOutcome> ReverseSourceAsync(JournalSource source, Guid sourceId, string reason, bool redirectClosedPeriods = false, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var active = await ActiveEntriesAsync(db, source, sourceId, ct);
        if (active.Count == 0)
        {
            return PostingOutcome.Nothing;
        }

        var context = await PostingContext.LoadAsync(db, redirectClosedPeriods, ct);
        foreach (var entry in active)
        {
            await ledger.ReverseAsync(db, entry, context.ReversalDate(entry), reason, ct);
        }

        await db.SaveChangesAsync(ct);
        return new PostingOutcome(0, active.Count, 0, []);
    }

    private async Task<PostingOutcome> PostAsync(
        IWorkbenchDbContext db, JournalSource source, Guid sourceId, int revision, Func<Task<Plan>> plan, PostingContext context, CancellationToken ct)
    {
        var active = await ActiveEntriesAsync(db, source, sourceId, ct);
        var current = active.Where(e => e.SourceRevision == revision).ToList();
        if (current.Count > 0)
        {
            return new PostingOutcome(0, 0, current.Count, []);
        }

        var planned = await plan();
        if (planned.Problem is not null)
        {
            return PostingOutcome.Problem(planned.Problem);
        }

        // Entries left over from an earlier revision (a reopen whose reversal didn't happen) are reversed first.
        var reversed = 0;
        foreach (var stale in active)
        {
            await ledger.ReverseAsync(db, stale, context.ReversalDate(stale), $"Superseded by revision {revision}", ct);
            reversed++;
        }

        foreach (var entry in planned.Entries)
        {
            db.Set<JournalEntry>().Add(entry);
            await ledger.PostAsync(db, entry, ct);
        }

        await db.SaveChangesAsync(ct);
        return new PostingOutcome(planned.Entries.Count, reversed, 0, []);
    }

    private static Task<List<JournalEntry>> ActiveEntriesAsync(IWorkbenchDbContext db, JournalSource source, Guid sourceId, CancellationToken ct) =>
        db.Set<JournalEntry>().Include(e => e.Lines)
            .Where(e => e.Source == source && e.SourceId == sourceId && e.Status == JournalStatus.Posted)
            .ToListAsync(ct);

    // ---- Closed-period checks for the approval decorators ----

    /// <summary>Approving or reopening time in a closed period would change that period, so it's refused.</summary>
    public async Task EnsureTimesheetPeriodsOpenAsync(Guid timesheetId, string action, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var dates = await db.Set<TimeEntry>().AsNoTracking().Where(e => e.TimesheetId == timesheetId && e.Hours != 0)
            .Select(e => e.WorkDate).Distinct().ToListAsync(ct);
        await EnsureOpenAsync(db, dates, $"{action} this timesheet: it has hours", ct);
    }

    public async Task EnsureOdcPeriodsOpenAsync(IReadOnlyCollection<Guid> odcIds, string action, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var dates = await db.Set<OdcEntry>().AsNoTracking().Where(o => odcIds.Contains(o.Id)).Select(o => o.IncurredOn).Distinct().ToListAsync(ct);
        await EnsureOpenAsync(db, dates, $"{action}: it has costs", ct);
    }

    private static async Task EnsureOpenAsync(IWorkbenchDbContext db, IReadOnlyCollection<DateOnly> dates, string what, CancellationToken ct)
    {
        if (dates.Count == 0)
        {
            return;
        }

        var (min, max) = (dates.Min(), dates.Max());
        var closed = (await db.Set<FiscalPeriod>().AsNoTracking()
                .Where(p => p.Status == PeriodStatus.Closed && p.Start <= max && p.End >= min).ToListAsync(ct))
            .Where(p => dates.Any(d => p.Start <= d && d <= p.End))
            .OrderBy(p => p.Start)
            .ToList();
        if (closed.Count > 0)
        {
            throw new ArgumentException($"You can't {what} in closed fiscal period {string.Join(", ", closed.Select(p => p.Name))}.");
        }
    }

    // ---- Decorator helpers: posting never undoes an approval; failures stay visible as unposted items ----

    public async Task TryPostAsync(JournalSource source, Guid sourceId, CancellationToken ct = default)
    {
        try
        {
            var outcome = source == JournalSource.Timesheet ? await PostTimesheetAsync(sourceId, false, ct) : await PostOdcAsync(sourceId, false, ct);
            foreach (var problem in outcome.Problems)
            {
                logger.LogWarning("Couldn't post {Source} {SourceId} to the general ledger: {Problem}", source, sourceId, problem);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Posting {Source} {SourceId} to the general ledger failed; it stays in the unposted list", source, sourceId);
        }
    }

    public async Task TryReverseAsync(JournalSource source, Guid sourceId, string reason, CancellationToken ct = default)
    {
        try
        {
            await ReverseSourceAsync(source, sourceId, reason, false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reversing {Source} {SourceId} in the general ledger failed; it stays in the unposted list", source, sourceId);
        }
    }

    // ---- Unposted items and the backlog ----

    public async Task<IReadOnlyList<UnpostedItemDto>> ListUnpostedAsync(Guid? fiscalPeriodId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var context = await PostingContext.LoadAsync(db, redirectClosedPeriods: true, ct);
        var period = fiscalPeriodId is { } pid ? context.Periods.FirstOrDefault(p => p.Id == pid) ?? throw WorkbenchNotFoundException.For("Fiscal period", pid) : null;
        bool InPeriod(DateOnly d) => period is null || (period.Start <= d && d <= period.End);

        // Only what the period involves: approved timesheets with hours in it, approved ODCs dated in it, and the posted
        // labor and ODC entries of those sources or dated in the period. Without a period (the backlog), everything.
        var (start, end) = period is null ? (DateOnly.MinValue, DateOnly.MaxValue) : (period.Start, period.End);
        var sheets = await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries)
            .Where(t => t.Status == TimesheetStatus.Approved && (period == null || t.Entries.Any(e => e.Hours != 0 && e.WorkDate >= start && e.WorkDate <= end)))
            .ToListAsync(ct);
        var odcs = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => o.Status == OdcStatus.Approved && (period == null || (o.IncurredOn >= start && o.IncurredOn <= end)))
            .ToListAsync(ct);
        var activeQuery = db.Set<JournalEntry>().AsNoTracking()
            .Where(e => (e.Source == JournalSource.Timesheet || e.Source == JournalSource.Odc) && e.Status == JournalStatus.Posted);
        if (period is not null)
        {
            var sourceIds = sheets.Select(t => t.Id).Concat(odcs.Select(o => o.Id)).ToList();
            activeQuery = activeQuery.Where(e => e.FiscalPeriodId == period.Id || (e.SourceId != null && sourceIds.Contains(e.SourceId.Value)));
        }

        var active = await activeQuery.ToListAsync(ct);
        var posted = active.Select(e => (e.Source, e.SourceId, e.SourceRevision)).ToHashSet();
        var result = new List<UnpostedItemDto>();

        var sheetRevisions = sheets.ToDictionary(s => s.Id, s => s.Revision);
        foreach (var sheet in sheets.Where(s => s.Entries.Any(e => e.Hours != 0 && InPeriod(e.WorkDate))))
        {
            if (posted.Contains((JournalSource.Timesheet, sheet.Id, sheet.Revision)))
            {
                continue;
            }

            var plan = await PlanTimesheetAsync(db, sheet, context, ct);
            if (plan.Problem is null && plan.Entries.Count == 0)
            {
                continue; // Nothing costs anything (zero hours).
            }

            result.Add(new UnpostedItemDto(Contracts.Accounting.JournalEntrySource.Timesheet, sheet.Id,
                $"Labor: {context.EmployeeName(sheet.EmployeeId)}, week of {sheet.PeriodStart:yyyy-MM-dd}", sheet.PeriodEnd, plan.Amount, false, plan.Problem));
        }

        var odcRevisions = await OdcRevisionsAsync(db, odcs, ct);
        foreach (var odc in odcs.Where(o => o.Amount != 0 && InPeriod(o.IncurredOn)))
        {
            if (posted.Contains((JournalSource.Odc, odc.Id, odcRevisions[odc.Id])))
            {
                continue;
            }

            var plan = PlanOdc(odc, odcRevisions[odc.Id], context);
            result.Add(new UnpostedItemDto(Contracts.Accounting.JournalEntrySource.Odc, odc.Id, OdcDescription(odc), odc.IncurredOn, odc.Amount, false, plan.Problem));
        }

        // Posted entries whose source was reopened (or re-approved as a new revision) still need their reversal. Sources
        // posted into the period but not loaded above (their hours moved, or reopened) are looked up by id.
        var inPeriod = active.Where(e => period is null || e.FiscalPeriodId == period.Id).ToList();
        var otherSheets = inPeriod.Where(e => e.Source == JournalSource.Timesheet && !sheetRevisions.ContainsKey(e.SourceId!.Value)).Select(e => e.SourceId!.Value).Distinct().ToList();
        if (otherSheets.Count > 0)
        {
            foreach (var t in await db.Set<Timesheet>().AsNoTracking().Where(t => t.Status == TimesheetStatus.Approved && otherSheets.Contains(t.Id))
                         .Select(t => new { t.Id, t.Revision }).ToListAsync(ct))
            {
                sheetRevisions[t.Id] = t.Revision;
            }
        }

        var otherOdcIds = inPeriod.Where(e => e.Source == JournalSource.Odc && !odcRevisions.ContainsKey(e.SourceId!.Value)).Select(e => e.SourceId!.Value).Distinct().ToList();
        if (otherOdcIds.Count > 0)
        {
            var otherOdcs = await db.Set<OdcEntry>().AsNoTracking().Where(o => o.Status == OdcStatus.Approved && otherOdcIds.Contains(o.Id)).ToListAsync(ct);
            foreach (var (id, revision) in await OdcRevisionsAsync(db, otherOdcs, ct))
            {
                odcRevisions[id] = revision;
            }
        }

        var stale = new List<(JournalEntry Entry, int? Current)>();
        foreach (var entry in inPeriod)
        {
            var current = entry.Source == JournalSource.Timesheet
                ? sheetRevisions.TryGetValue(entry.SourceId!.Value, out var r) ? r : (int?)null
                : odcRevisions.TryGetValue(entry.SourceId!.Value, out var o) ? o : null;
            if (current != entry.SourceRevision)
            {
                stale.Add((entry, current));
            }
        }

        if (stale.Count > 0)
        {
            var staleIds = stale.Select(s => s.Entry.Id).ToList();
            var debits = await db.Set<JournalLine>().AsNoTracking().Where(l => staleIds.Contains(l.JournalEntryId))
                .GroupBy(l => l.JournalEntryId).Select(g => new { g.Key, Debit = g.Sum(l => l.Debit) }).ToDictionaryAsync(x => x.Key, x => x.Debit, ct);
            foreach (var (entry, current) in stale)
            {
                result.Add(new UnpostedItemDto(entry.Source.ToContract(), entry.SourceId!.Value, $"{entry.Number} {entry.Description}", entry.Date,
                    debits.GetValueOrDefault(entry.Id), true, current is null ? "The source was reopened; the entry needs a reversal." : "The source was approved again; the old revision needs a reversal."));
            }
        }

        return result.OrderBy(i => i.Date).ThenBy(i => i.Description).ToList();
    }

    /// <summary>Posts every approved item without an entry and reverses stale entries. Closed periods redirect to the next open one.</summary>
    public async Task<PostingOutcome> PostBacklogAsync(CancellationToken ct = default)
    {
        var items = await ListUnpostedAsync(null, ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var approvedSheets = (await db.Set<Timesheet>().AsNoTracking().Where(t => t.Status == TimesheetStatus.Approved).Select(t => t.Id).ToListAsync(ct)).ToHashSet();
        var approvedOdcs = (await db.Set<OdcEntry>().AsNoTracking().Where(o => o.Status == OdcStatus.Approved).Select(o => o.Id).ToListAsync(ct)).ToHashSet();

        var outcome = PostingOutcome.Nothing;
        foreach (var (source, id) in items.Select(i => (i.Source.ToDomain(), i.SourceId)).Distinct())
        {
            try
            {
                var approved = source == JournalSource.Timesheet ? approvedSheets.Contains(id) : approvedOdcs.Contains(id);
                outcome = outcome.Add(!approved
                    ? await ReverseSourceAsync(source, id, "Source reopened (posted from the backlog)", true, ct)
                    : source == JournalSource.Timesheet
                        ? await PostTimesheetAsync(id, true, ct)
                        : await PostOdcAsync(id, true, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbUpdateException)
            {
                outcome = outcome.Add(PostingOutcome.Problem($"{source} {id}: {ex.Message}"));
            }
        }

        return outcome;
    }

    // ---- Planning entries ----

    private sealed record Plan(List<JournalEntry> Entries, string? Problem, decimal Amount)
    {
        public static Plan Failed(string problem) => new([], problem, 0);
    }

    private async Task<Plan> PlanTimesheetAsync(IWorkbenchDbContext db, Timesheet sheet, PostingContext context, CancellationToken ct)
    {
        var entries = sheet.Entries.Where(e => e.Hours != 0).ToList();
        if (entries.Count == 0)
        {
            return new Plan([], null, 0);
        }

        var employee = context.Employees.GetValueOrDefault(sheet.EmployeeId);
        var rates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => r.EmployeeId == sheet.EmployeeId).ToListAsync(ct))
        {
            rates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        var name = employee?.DisplayName ?? "employee";
        var costed = new List<(FiscalPeriod Period, bool Redirected, Guid Account, WbsElement Wbs, decimal Hours, decimal Cost)>();
        foreach (var e in entries)
        {
            if (!context.Wbs.TryGetValue(e.WbsElementId, out var wbs))
            {
                return Plan.Failed($"Time on {e.WorkDate:yyyy-MM-dd} is charged to a WBS element that no longer exists.");
            }

            if (context.LaborAccount(wbs) is not { } account)
            {
                return Plan.Failed(context.MissingLaborRule(wbs));
            }

            // Labor cost is computed, never typed: hours × the cost rate effective on the work date, per entry to the cent
            // (the same rounding the project financials use, so the two reconcile).
            if (rates.Find(sheet.EmployeeId, e.WorkDate) is not { } rate)
            {
                return Plan.Failed($"{name} has no cost rate on {e.WorkDate:yyyy-MM-dd}; add one under Setup › People.");
            }

            if (!context.TryResolve(e.WorkDate, out var period, out var redirected, out var problem))
            {
                return Plan.Failed(problem!);
            }

            costed.Add((period!, redirected, account, wbs, e.Hours, Money.Round(e.Hours * rate)));
        }

        var accrued = context.Rule(PostingRuleKind.AccruedPayroll, "");
        if (accrued is null)
        {
            return Plan.Failed(PostingContext.MissingRule("Accrued payroll"));
        }

        var result = new List<JournalEntry>();
        foreach (var group in costed.GroupBy(c => c.Period.Id))
        {
            var period = group.First().Period;
            var redirected = group.Any(c => c.Redirected);
            var entry = new JournalEntry
            {
                // The employee's home company accrues the labor; time charged to another company's project crosses companies.
                CompanyId = employee?.CompanyId ?? Guid.Empty,
                Date = redirected ? period.Start : Min(sheet.PeriodEnd, period.End),
                FiscalPeriodId = period.Id,
                Source = JournalSource.Timesheet,
                SourceId = sheet.Id,
                SourceRevision = sheet.Revision,
                Description = $"Labor: {name}, week of {sheet.PeriodStart:yyyy-MM-dd}"
                    + (sheet.Revision > 0 ? $" (revision {sheet.Revision})" : "")
                    + (redirected ? " - late posting from a closed period" : ""),
                CreatedBy = ledger.Actor,
                CreatedAtUtc = ledger.UtcNow,
            };
            foreach (var line in group.GroupBy(c => (c.Account, c.Wbs.Id)).OrderBy(g => g.First().Wbs.Code))
            {
                var cost = line.Sum(c => c.Cost);
                if (cost == 0)
                {
                    continue;
                }

                var wbs = line.First().Wbs;
                entry.Lines.Add(new JournalLine
                {
                    JournalEntryId = entry.Id,
                    LineNumber = entry.Lines.Count + 1,
                    AccountId = line.Key.Account,
                    OrgUnitId = employee?.OrgUnitId,
                    ProjectId = wbs.ProjectId,
                    WbsElementId = wbs.Id,
                    Debit = cost > 0 ? cost : 0,
                    Credit = cost < 0 ? -cost : 0,
                    Memo = $"{line.Sum(c => c.Hours):0.##} h {wbs.Code}",
                });
            }

            var total = entry.Lines.Sum(l => l.Debit - l.Credit);
            if (total == 0)
            {
                continue;
            }

            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = entry.Lines.Count + 1,
                AccountId = accrued.Value,
                OrgUnitId = employee?.OrgUnitId,
                Debit = total < 0 ? -total : 0,
                Credit = total > 0 ? total : 0,
                Memo = $"Accrued payroll: {name}",
            });
            result.Add(entry);
        }

        return new Plan(result, null, costed.Sum(c => c.Cost));
    }

    private Plan PlanOdc(OdcEntry odc, int revision, PostingContext context)
    {
        var amount = Money.Round(odc.Amount);
        if (amount == 0)
        {
            return new Plan([], null, 0);
        }

        var type = odc.Type.ToString();
        var debit = odc.IsUnallowable
            ? context.Rule(PostingRuleKind.UnallowableOdc, type) ?? context.Rule(PostingRuleKind.UnallowableOdc, "")
            : context.Rule(PostingRuleKind.Odc, type) ?? context.Rule(PostingRuleKind.Odc, "");
        if (debit is null)
        {
            return Plan.Failed(PostingContext.MissingRule(odc.IsUnallowable ? $"Unallowable {type} ODCs" : $"{type} ODCs"));
        }

        var isExpense = IsEmployeeExpense(odc);
        var credit = context.Rule(isExpense ? PostingRuleKind.AccruedReimbursement : PostingRuleKind.AccountsPayable, "");
        if (credit is null)
        {
            return Plan.Failed(PostingContext.MissingRule(isExpense ? "Accrued employee reimbursements" : "Accounts payable"));
        }

        if (!context.TryResolve(odc.IncurredOn, out var period, out var redirected, out var problem))
        {
            return Plan.Failed(problem!);
        }

        var orgUnit = odc.EmployeeId is { } eid ? context.Employees.GetValueOrDefault(eid)?.OrgUnitId : null;
        var entry = new JournalEntry
        {
            // An expense is owed by the employee's home company; a vendor ODC by the project's company (filled in on posting).
            CompanyId = isExpense && odc.EmployeeId is { } owner && context.Employees.TryGetValue(owner, out var spender) ? spender.CompanyId : Guid.Empty,
            Date = redirected ? period!.Start : odc.IncurredOn,
            FiscalPeriodId = period!.Id,
            Source = JournalSource.Odc,
            SourceId = odc.Id,
            SourceRevision = revision,
            Description = OdcDescription(odc) + (redirected ? " - late posting from a closed period" : ""),
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };
        entry.Lines.Add(new JournalLine
        {
            JournalEntryId = entry.Id,
            LineNumber = 1,
            AccountId = debit.Value,
            OrgUnitId = orgUnit,
            ProjectId = odc.ProjectId,
            WbsElementId = odc.WbsElementId,
            Debit = amount > 0 ? amount : 0,
            Credit = amount < 0 ? -amount : 0,
            Memo = odc.IsUnallowable ? $"Unallowable: {odc.UnallowableReason}" : odc.Description,
        });
        entry.Lines.Add(new JournalLine
        {
            JournalEntryId = entry.Id,
            LineNumber = 2,
            AccountId = credit.Value,
            OrgUnitId = orgUnit,
            Debit = amount < 0 ? -amount : 0,
            Credit = amount > 0 ? amount : 0,
            Memo = isExpense ? $"Reimbursement: {context.EmployeeName(odc.EmployeeId)}" : $"{odc.Vendor} {odc.InvoiceNumber}".Trim(),
        });
        return new Plan([entry], null, amount);
    }

    /// <summary>Expense report lines (and employee-incurred costs without a vendor) are owed to the employee; the rest to vendors.</summary>
    public static bool IsEmployeeExpense(OdcEntry odc) => odc.ExpenseReportId is not null || (odc.EmployeeId is not null && string.IsNullOrWhiteSpace(odc.Vendor));

    private static string OdcDescription(OdcEntry odc) =>
        (IsEmployeeExpense(odc) ? $"Expense: {odc.Description}" : $"ODC: {odc.Vendor} {odc.InvoiceNumber} {odc.Description}".Replace("  ", " "))
        + (odc.IsUnallowable ? " (unallowable)" : "");

    /// <summary>An ODC's revision for posting: how many times it (or its expense report) was reopened.</summary>
    private static async Task<Dictionary<Guid, int>> OdcRevisionsAsync(IWorkbenchDbContext db, IReadOnlyCollection<OdcEntry> odcs, CancellationToken ct)
    {
        var ids = odcs.Select(o => o.Id).Concat(odcs.Where(o => o.ExpenseReportId is not null).Select(o => o.ExpenseReportId!.Value)).Distinct().ToList();
        var reopens = await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.Action == "Reopened" && (a.EntityType == OdcWorkflow.OdcEntityType || a.EntityType == OdcWorkflow.ReportEntityType) && ids.Contains(a.EntityId))
            .GroupBy(a => a.EntityId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return odcs.ToDictionary(o => o.Id,
            o => reopens.GetValueOrDefault(o.Id) + (o.ExpenseReportId is { } r ? reopens.GetValueOrDefault(r) : 0));
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}

/// <summary>Posting rules, pools, WBS, employees and the calendar, loaded once per posting operation.</summary>
internal sealed class PostingContext
{
    private Dictionary<(PostingRuleKind, string), Guid> _rules = [];
    private Dictionary<Guid, IndirectPool> _pools = [];

    public Dictionary<Guid, WbsElement> Wbs { get; private set; } = [];
    public Dictionary<Guid, Employee> Employees { get; private set; } = [];
    public List<FiscalPeriod> Periods { get; private set; } = [];
    public bool RedirectClosedPeriods { get; private init; }

    public static async Task<PostingContext> LoadAsync(IWorkbenchDbContext db, bool redirectClosedPeriods, CancellationToken ct)
    {
        var activeAccounts = (await db.Set<Account>().AsNoTracking().Where(a => a.IsActive).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var rules = await db.Set<PostingRule>().AsNoTracking().ToListAsync(ct);
        return new PostingContext
        {
            RedirectClosedPeriods = redirectClosedPeriods,
            _rules = rules.Where(r => activeAccounts.Contains(r.AccountId)).ToDictionary(r => (r.Kind, r.Key), r => r.AccountId),
            _pools = await db.Set<WbsCostPool>().AsNoTracking().ToDictionaryAsync(p => p.WbsElementId, p => p.Pool, ct),
            Wbs = await db.Set<WbsElement>().AsNoTracking().ToDictionaryAsync(w => w.Id, ct),
            Employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, ct),
            Periods = (await db.Set<FiscalPeriod>().AsNoTracking().ToListAsync(ct)).OrderBy(p => p.Start).ToList(),
        };
    }

    public Guid? Rule(PostingRuleKind kind, string key) => _rules.TryGetValue((kind, key), out var id) ? id : null;

    public string EmployeeName(Guid? id) => id is { } e && Employees.TryGetValue(e, out var emp) ? emp.DisplayName : "employee";

    /// <summary>The pool of an indirect/leave WBS element: its own setting, else the nearest ancestor's, else the default for its kind.</summary>
    public (IndirectPool Pool, bool Inherited, bool Explicit) EffectivePool(WbsElement wbs)
    {
        var inherited = false;
        for (WbsElement? at = wbs; at is not null; at = at.ParentId is { } p ? Wbs.GetValueOrDefault(p) : null)
        {
            if (_pools.TryGetValue(at.Id, out var pool))
            {
                return (pool, inherited, true);
            }

            inherited = true;
        }

        return (wbs.Kind == ChargeKind.Leave ? IndirectPool.Fringe : IndirectPool.Overhead, false, false);
    }

    /// <summary>Direct → direct labor; Indirect → the indirect labor account of the element's pool; Leave → fringe.</summary>
    public Guid? LaborAccount(WbsElement wbs) => wbs.Kind switch
    {
        ChargeKind.Direct => Rule(PostingRuleKind.DirectLabor, ""),
        ChargeKind.Indirect => Rule(PostingRuleKind.IndirectLabor, EffectivePool(wbs).Pool.ToString()) ?? Rule(PostingRuleKind.IndirectLabor, ""),
        _ => Rule(PostingRuleKind.LeaveLabor, ""),
    };

    public string MissingLaborRule(WbsElement wbs) => MissingRule(wbs.Kind switch
    {
        ChargeKind.Direct => "Direct labor",
        ChargeKind.Indirect => $"Indirect labor ({AccountingMapping.Label(EffectivePool(wbs).Pool.ToContract())} pool)",
        _ => "Leave labor",
    });

    public static string MissingRule(string what) => $"No active account is mapped for {what}; set it under Accounting › Posting rules.";

    /// <summary>
    /// The period an item dated <paramref name="date"/> posts into: its own when open; when closed, the next open period
    /// if <see cref="RedirectClosedPeriods"/> (the backlog), else a problem.
    /// </summary>
    public bool TryResolve(DateOnly date, out FiscalPeriod? period, out bool redirected, out string? problem)
    {
        redirected = false;
        problem = null;
        period = Periods.FirstOrDefault(p => p.Start <= date && date <= p.End);
        if (period is null)
        {
            problem = $"No fiscal period covers {date:yyyy-MM-dd}.";
            return false;
        }

        if (period.Status == PeriodStatus.Open)
        {
            return true;
        }

        if (!RedirectClosedPeriods)
        {
            problem = $"Fiscal period {period.Name} is closed.";
            period = null;
            return false;
        }

        var closed = period;
        period = Periods.FirstOrDefault(p => p.Status == PeriodStatus.Open && p.Start > closed.End);
        if (period is null)
        {
            problem = $"Fiscal period {closed.Name} is closed and there's no later open period to post into.";
            return false;
        }

        redirected = true;
        return true;
    }

    /// <summary>A reversal is dated like the original when its period is open, else (backlog only) in the next open period.</summary>
    public DateOnly ReversalDate(JournalEntry original)
    {
        if (TryResolve(original.Date, out var period, out var redirected, out var problem))
        {
            return redirected ? period!.Start : original.Date;
        }

        throw new ArgumentException($"Can't reverse {original.Number}: {problem}");
    }
}
