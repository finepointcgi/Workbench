using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Application.Financials;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Contracts;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ContractDetail;

/// <summary>Funding thresholds. 0.75 is the Limitation of Funds notice point (FAR 52.232-22 asks for notice at 75%).</summary>
public sealed class FundingNoticeOptions
{
    public const string Section = "FundingNotices";

    /// <summary>Incurred plus committed cost, as a fraction of funded value, that triggers the first notice.</summary>
    public decimal WarningThreshold { get; set; } = 0.75m;

    /// <summary>A second, critical notice.</summary>
    public decimal CriticalThreshold { get; set; } = 0.90m;
}

/// <summary>Cost per CLIN for one contract, and the funding status built from it.</summary>
public sealed class ClinFundingCalculator(
    IWorkbenchDbContextFactory dbFactory, IServiceProvider services, TimeProvider clock, IOptions<FundingNoticeOptions> options,
    ILogger<ClinFundingCalculator> logger)
{
    public const int BurnWindowDays = 90;

    public FundingNoticeOptions Options => options.Value;

    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<ContractFundingStatusDto> ComputeAsync(Guid contractId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw WorkbenchNotFoundException.For("Contract", contractId);
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contractId).ToListAsync(ct);
        var asOf = Today;
        var costs = await LoadCostsAsync(db, contract, clins, asOf, ct);

        var rows = new List<ClinFundingStatusDto>();
        foreach (var clin in ClinRules.InTreeOrder(clins))
        {
            var slins = clins.Where(c => ClinRules.ParentOf(c.Number) == clin.Number).ToList();
            var family = slins.Count > 0 ? slins : [clin];
            var cost = family.Select(c => costs.ByClin.GetValueOrDefault(c.Id)).Aggregate(new ClinCost(), (a, b) => a + b);
            rows.Add(Status(clin, cost, asOf, Options));
        }

        return new ContractFundingStatusDto(contract.Id, contract.ContractNumber, asOf, costs.IsBurdened,
            Options.WarningThreshold, Options.CriticalThreshold, rows, []);
    }

    public sealed record ClinCost(decimal Incurred = 0, decimal Committed = 0, decimal RecentIncurred = 0)
    {
        public static ClinCost operator +(ClinCost a, ClinCost? b) =>
            b is null ? a : new(a.Incurred + b.Incurred, a.Committed + b.Committed, a.RecentIncurred + b.RecentIncurred);
    }

    /// <summary>Funded, incurred, remaining, percent and the projected exhaustion date at the recent burn rate.</summary>
    public static ClinFundingStatusDto Status(Clin clin, ClinCost cost, DateOnly asOf, FundingNoticeOptions options)
    {
        var remaining = clin.FundedValue - cost.Incurred;
        decimal? Pct(decimal v) => clin.FundedValue > 0 ? Math.Round(v / clin.FundedValue, 4, MidpointRounding.AwayFromZero) : null;
        var exposure = cost.Incurred + cost.Committed;

        // Burn over the last 90 days, or since the CLIN started when that's more recent.
        var days = Math.Clamp(asOf.DayNumber - clin.PeriodStart.DayNumber + 1, 1, BurnWindowDays);
        var burn = Math.Round(cost.RecentIncurred / days, 2, MidpointRounding.AwayFromZero);
        DateOnly? exhaustion = remaining > 0 && burn > 0
            ? asOf.AddDays((int)Math.Min(Math.Ceiling(remaining / burn), 365 * 50))
            : remaining <= 0 && clin.FundedValue > 0 ? asOf : null;

        var level = !clin.IsActive() ? FundingLevel.NotActive
            : (clin.FundedValue > 0 && remaining <= 0) || (clin.FundedValue == 0 && cost.Incurred > 0) ? FundingLevel.Exhausted
            : clin.FundedValue > 0 && exposure / clin.FundedValue >= options.CriticalThreshold ? FundingLevel.Critical
            : clin.FundedValue > 0 && exposure / clin.FundedValue >= options.WarningThreshold ? FundingLevel.Warning
            : FundingLevel.Ok;

        return new ClinFundingStatusDto(clin.Id, clin.Number, clin.Description, clin.IsOption, clin.IsExercised, clin.PeriodStart, clin.PeriodEnd,
            clin.CeilingValue, clin.FundedValue, cost.Incurred, cost.Committed, remaining, Pct(cost.Incurred), Pct(exposure), burn, exhaustion, level);
    }

    private sealed record CostResult(Dictionary<Guid, ClinCost> ByClin, bool IsBurdened);

    /// <summary>
    /// Incurred: approved time at cost plus approved, allowable ODCs on WBS elements mapped to each CLIN (fully burdened
    /// through <see cref="IBurdenedCostService"/> when indirect rates are registered). Committed: submitted time and ODCs
    /// that aren't approved yet, at direct cost, plus open purchase order amounts.
    /// </summary>
    private async Task<CostResult> LoadCostsAsync(IWorkbenchDbContext db, Contract contract, IReadOnlyList<Clin> clins, DateOnly asOf, CancellationToken ct)
    {
        var clinIds = clins.Select(c => c.Id).ToHashSet();
        var projectIds = await db.Set<Project>().AsNoTracking().Where(p => p.ContractId == contract.Id).Select(p => p.Id).ToListAsync(ct);
        var mapping = await db.Set<WbsElement>().AsNoTracking()
            .Where(w => projectIds.Contains(w.ProjectId) && w.ClinId != null)
            .ToDictionaryAsync(w => w.Id, w => w.ClinId!.Value, ct);
        mapping = mapping.Where(m => clinIds.Contains(m.Value)).ToDictionary();
        var windowStart = asOf.AddDays(-(BurnWindowDays - 1));

        var result = new Dictionary<Guid, ClinCost>();
        void Add(Guid wbsId, DateOnly date, decimal amount, bool committed)
        {
            if (!mapping.TryGetValue(wbsId, out var clinId) || date > asOf)
            {
                return;
            }

            var cost = committed ? new ClinCost(Committed: amount) : new ClinCost(amount, 0, date >= windowStart ? amount : 0);
            result[clinId] = (result.GetValueOrDefault(clinId) ?? new ClinCost()) + cost;
        }

        var (labor, odcs) = await FinancialsLoader.LoadActualsAsync(db, projectIds, contract, [], ct);
        var burdened = await LoadBurdenedAsync(projectIds, mapping, contract.PeriodStart, windowStart, asOf, ct);
        if (burdened is null)
        {
            foreach (var l in labor)
            {
                Add(l.WbsElementId, l.WorkDate, l.Cost, committed: false);
            }

            foreach (var o in odcs)
            {
                Add(o.WbsElementId, o.IncurredOn, o.Amount, committed: false);
            }
        }
        else
        {
            foreach (var (clinId, cost) in burdened)
            {
                result[clinId] = (result.GetValueOrDefault(clinId) ?? new ClinCost()) + cost;
            }
        }

        var wbsIds = mapping.Keys.ToList();
        var pendingLabor = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.Status == TimesheetStatus.Submitted && wbsIds.Contains(e.WbsElementId)
                select new { Actual = new LaborActual(e.WbsElementId, t.EmployeeId, e.LaborCategoryId, e.WorkDate, e.Hours, e.Id), e.TimesheetId })
            .ToListAsync(ct);
        var book = await TotalTime.LaborCostBook.LoadAsync(db, pendingLabor.Select(l => l.TimesheetId), ct);
        var employeeIds = pendingLabor.Select(l => l.Actual.EmployeeId).Distinct().ToList();
        var rates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => employeeIds.Contains(r.EmployeeId)).ToListAsync(ct))
        {
            rates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        foreach (var l in LaborCosting.Cost(pendingLabor.Select(l => l.Actual), book, rates, null, out _))
        {
            Add(l.WbsElementId, l.WorkDate, l.Cost, committed: true);
        }

        var pendingOdcs = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => wbsIds.Contains(o.WbsElementId) && o.Status == OdcStatus.Submitted && !o.IsUnallowable)
            .Select(o => new { o.WbsElementId, o.IncurredOn, o.Amount })
            .ToListAsync(ct);
        foreach (var o in pendingOdcs)
        {
            Add(o.WbsElementId, o.IncurredOn, o.Amount, committed: true);
        }

        // Open purchase order amounts (procure-to-pay) are committed cost too, until their bills post.
        foreach (var (wbsId, orderDate, amount) in await Purchasing.ProcurementSupport.CommitmentsByWbsAsync(db, wbsIds, ct))
        {
            Add(wbsId, orderDate, amount, committed: true);
        }

        return new CostResult(result, burdened is not null);
    }

    /// <summary>Burdened incurred cost per CLIN, or null when no indirect-rate implementation is registered or it can't answer.</summary>
    private async Task<Dictionary<Guid, ClinCost>?> LoadBurdenedAsync(
        IReadOnlyList<Guid> projectIds, Dictionary<Guid, Guid> mapping, DateOnly from, DateOnly windowStart, DateOnly asOf, CancellationToken ct)
    {
        if (services.GetService<IBurdenedCostService>() is not { } burdened || projectIds.Count == 0)
        {
            return null;
        }

        try
        {
            var result = new Dictionary<Guid, ClinCost>();
            var anyRates = false;
            foreach (var projectId in projectIds)
            {
                var total = await burdened.GetProjectCostAsync(projectId, from < windowStart ? from : windowStart, asOf, RateBasis.Provisional, ct);
                var recent = await burdened.GetProjectCostAsync(projectId, windowStart, asOf, RateBasis.Provisional, ct);
                anyRates |= total.Rates.Any(r => r.Fringe != 0 || r.Overhead != 0 || r.GeneralAndAdministrative != 0);
                foreach (var (line, isRecent) in total.Lines.Select(l => (l, false)).Concat(recent.Lines.Select(l => (l, true))))
                {
                    if (!mapping.TryGetValue(line.WbsElementId, out var clinId))
                    {
                        continue;
                    }

                    var cost = isRecent ? new ClinCost(RecentIncurred: line.TotalCost) : new ClinCost(Incurred: line.TotalCost);
                    result[clinId] = (result.GetValueOrDefault(clinId) ?? new ClinCost()) + cost;
                }
            }

            // With no indirect rates set up the service only echoes direct cost; report that as direct, not burdened.
            return anyRates ? result : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException or WorkbenchNotFoundException)
        {
            // No rates for the year, or the caller (a background check) can't read them: fall back to direct cost.
            logger.LogInformation(ex, "Burdened cost unavailable; CLIN funding uses direct cost");
            return null;
        }
    }
}
