using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Accounting;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Accounting;

/// <summary>What re-costing one posted timesheet after a rate change did.</summary>
public sealed record LaborAdjustmentOutcome(Guid TimesheetId, Guid? AdjustmentId, decimal Amount, IReadOnlyList<string> EntryNumbers, IReadOnlyList<string> PostedPeriods, bool Redirected, string? Problem)
{
    public bool Adjusted => AdjustmentId is not null;
}

// Back-dated rate changes. Posted labor is never re-posted: the difference between what was posted and what the hours
// cost at the rates now in effect is posted as a labor cost adjustment (source LaborAdjustment) into the work date's
// period, or the first open period after it when that one is closed. The adjustments belong to the timesheet revision,
// so reopening or re-approving the timesheet reverses them with its own entries.
public sealed partial class PostingEngine
{
    public const string LaborAdjustmentAuditType = nameof(LaborCostAdjustment);

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Re-costs an approved, posted timesheet at the cost rates now in effect on each work date and posts the difference.
    /// A timesheet that isn't approved or posted yet needs nothing (it is costed when it posts); no difference posts nothing.
    /// </summary>
    public async Task<LaborAdjustmentOutcome> AdjustTimesheetLaborAsync(Guid timesheetId, DateOnly rateEffectiveFrom, string reason, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await dbFactory.CreateAsync(ct);
            try
            {
                return await AdjustAsync(db, timesheetId, rateEffectiveFrom, reason, ct);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Lost a race for the next entry number: try once more.
            }
        }
    }

    private async Task<LaborAdjustmentOutcome> AdjustAsync(IWorkbenchDbContext db, Guid timesheetId, DateOnly rateEffectiveFrom, string reason, CancellationToken ct)
    {
        LaborAdjustmentOutcome None(string? problem = null) => new(timesheetId, null, 0, [], [], false, problem);
        var sheet = await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == timesheetId, ct);
        if (sheet is null || sheet.Status != TimesheetStatus.Approved)
        {
            return None();
        }

        var posted = (await ActiveEntriesAsync(db, JournalSource.Timesheet, sheet.Id, ct)).Where(e => e.SourceRevision == sheet.Revision).ToList();
        if (posted.Count == 0)
        {
            return None();
        }

        posted.AddRange(await ActiveLaborAdjustmentEntriesAsync(db, sheet.Id, null, ct, onlyRevision: sheet.Revision));

        // Closed periods always redirect here: a rate change is a correction made today, so it lands in an open period.
        var context = await PostingContext.LoadAsync(db, redirectClosedPeriods: true, ct);
        var plan = await PlanTimesheetAsync(db, sheet, context, ct);
        if (plan.Problem is not null)
        {
            return None(plan.Problem);
        }

        // Cost per (target period, labor account, WBS element): what the hours cost now, less what is posted. Posted lines
        // count in their entry's period, or the open period it now redirects to.
        var accrued = context.Rule(PostingRuleKind.AccruedPayroll, "");
        var delta = new Dictionary<(Guid Period, Guid Account, Guid Wbs), decimal>();
        foreach (var entry in plan.Entries)
        {
            foreach (var line in entry.Lines.Where(l => l.WbsElementId is not null))
            {
                var key = (entry.FiscalPeriodId, line.AccountId, line.WbsElementId!.Value);
                delta[key] = delta.GetValueOrDefault(key) + line.Debit - line.Credit;
            }
        }

        var periods = context.Periods.ToDictionary(p => p.Id);
        foreach (var entry in posted)
        {
            var target = TargetPeriod(context, periods, entry.FiscalPeriodId);
            foreach (var line in entry.Lines.Where(l => l.WbsElementId is not null && l.AccountId != accrued))
            {
                var key = (target, line.AccountId, line.WbsElementId!.Value);
                delta[key] = delta.GetValueOrDefault(key) - (line.Debit - line.Credit);
            }
        }

        var changes = delta.Where(d => d.Value != 0).ToList();
        if (changes.Count == 0)
        {
            return None();
        }

        if (accrued is null)
        {
            return None(PostingContext.MissingRule("Accrued payroll"));
        }

        var employee = context.Employees.GetValueOrDefault(sheet.EmployeeId);
        var name = employee?.DisplayName ?? "employee";
        var workPeriods = sheet.Entries.Where(e => e.Hours != 0)
            .Select(e => context.Periods.FirstOrDefault(p => p.Start <= e.WorkDate && e.WorkDate <= p.End))
            .OfType<FiscalPeriod>().DistinctBy(p => p.Id).OrderBy(p => p.Start).ToList();
        var adjustment = new LaborCostAdjustment
        {
            TimesheetId = sheet.Id,
            TimesheetRevision = sheet.Revision,
            EmployeeId = sheet.EmployeeId,
            RateEffectiveFrom = rateEffectiveFrom,
            Amount = changes.Sum(c => c.Value),
            WorkPeriods = Truncate(string.Join(", ", workPeriods.Select(p => p.Name)), 500),
            Reason = Truncate(reason, 500),
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };

        var entries = new List<JournalEntry>();
        var redirectedAny = false;
        foreach (var group in changes.GroupBy(c => c.Key.Period).OrderBy(g => periods[g.Key].Start))
        {
            var period = periods[group.Key];
            var redirected = !workPeriods.Any(p => p.Id == period.Id) || period.Status != PeriodStatus.Open;
            redirectedAny |= redirected;
            var entry = new JournalEntry
            {
                CompanyId = employee?.CompanyId ?? Guid.Empty,
                Date = redirected ? Max(period.Start, Min(sheet.PeriodEnd, period.End)) : Min(sheet.PeriodEnd, period.End),
                FiscalPeriodId = period.Id,
                Source = JournalSource.LaborAdjustment,
                SourceId = adjustment.Id,
                Description = $"Labor cost adjustment: {name}, week of {sheet.PeriodStart:yyyy-MM-dd} (rate change effective {rateEffectiveFrom:yyyy-MM-dd})"
                    + (redirected ? $" - for closed period {string.Join(", ", workPeriods.Where(p => p.Status != PeriodStatus.Open).Select(p => p.Name))}" : ""),
                CreatedBy = ledger.Actor,
                CreatedAtUtc = ledger.UtcNow,
            };
            foreach (var change in group.OrderBy(c => context.Wbs.GetValueOrDefault(c.Key.Wbs)?.Code, StringComparer.Ordinal))
            {
                var wbs = context.Wbs.GetValueOrDefault(change.Key.Wbs);
                entry.Lines.Add(new JournalLine
                {
                    JournalEntryId = entry.Id,
                    LineNumber = entry.Lines.Count + 1,
                    AccountId = change.Key.Account,
                    OrgUnitId = employee?.OrgUnitId,
                    ProjectId = wbs?.ProjectId,
                    WbsElementId = change.Key.Wbs,
                    Debit = change.Value > 0 ? change.Value : 0,
                    Credit = change.Value < 0 ? -change.Value : 0,
                    Memo = $"Rate change {rateEffectiveFrom:yyyy-MM-dd}: {wbs?.Code}",
                });
            }

            var total = group.Sum(c => c.Value);
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
            entries.Add(entry);
        }

        adjustment.Redirected = redirectedAny;
        db.Set<LaborCostAdjustment>().Add(adjustment);
        foreach (var entry in entries)
        {
            db.Set<JournalEntry>().Add(entry);
            await ledger.PostAsync(db, entry, ct);
        }

        var postedPeriods = entries.Select(e => periods[e.FiscalPeriodId].Name).ToList();
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            EntityType = LaborAdjustmentAuditType,
            EntityId = adjustment.Id,
            Action = "Labor cost adjusted",
            AfterJson = JsonSerializer.Serialize(new
            {
                TimesheetId = sheet.Id,
                sheet.Revision,
                sheet.EmployeeId,
                RateEffectiveFrom = rateEffectiveFrom,
                adjustment.Amount,
                WorkPeriods = adjustment.WorkPeriods,
                PostedPeriods = postedPeriods,
                Entries = entries.Select(e => e.Number).ToList(),
                Redirected = redirectedAny,
            }, AuditJson),
            Actor = ledger.Actor,
            OccurredAtUtc = ledger.UtcNow,
            Reason = redirectedAny ? $"{adjustment.Reason} (closed period: posted to the open period)" : adjustment.Reason,
        });
        await db.SaveChangesAsync(ct);
        return new LaborAdjustmentOutcome(sheet.Id, adjustment.Id, adjustment.Amount, entries.Select(e => e.Number).ToList(), postedPeriods, redirectedAny, null);
    }

    /// <summary>The period an amount posted in <paramref name="periodId"/> counts in now: that period while open, else the open period it redirects to.</summary>
    private static Guid TargetPeriod(PostingContext context, IReadOnlyDictionary<Guid, FiscalPeriod> periods, Guid periodId)
    {
        if (!periods.TryGetValue(periodId, out var period) || period.Status == PeriodStatus.Open)
        {
            return periodId;
        }

        return context.TryResolve(period.Start, out var target, out _, out _) ? target!.Id : periodId;
    }

    /// <summary>
    /// Posted rate-change adjustment entries of a timesheet: all of them, or (with <paramref name="exceptRevision"/>) those
    /// of other revisions, or (with <paramref name="onlyRevision"/>) those of one revision.
    /// </summary>
    private static async Task<List<JournalEntry>> ActiveLaborAdjustmentEntriesAsync(IWorkbenchDbContext db, Guid timesheetId, int? exceptRevision, CancellationToken ct, int? onlyRevision = null)
    {
        var ids = await db.Set<LaborCostAdjustment>().AsNoTracking()
            .Where(a => a.TimesheetId == timesheetId
                && (exceptRevision == null || a.TimesheetRevision != exceptRevision)
                && (onlyRevision == null || a.TimesheetRevision == onlyRevision))
            .Select(a => a.Id).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return [];
        }

        return await db.Set<JournalEntry>().Include(e => e.Lines)
            .Where(e => e.Source == JournalSource.LaborAdjustment && e.SourceId != null && ids.Contains(e.SourceId.Value) && e.Status == JournalStatus.Posted)
            .ToListAsync(ct);
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
