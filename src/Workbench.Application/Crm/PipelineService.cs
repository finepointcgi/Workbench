using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>Pipeline board, forecast, win/loss and B&amp;P spending. Permissions: <c>PermissionCheckingPipelineService</c>.</summary>
public sealed class PipelineService(IWorkbenchDbContextFactory dbFactory, CrmAccess access, AuditRecorder audit, TimeProvider clock) : IPipelineService
{
    public async Task<PipelineBoardDto> GetBoardAsync(bool includeOutcomes = false, CancellationToken cancellationToken = default)
    {
        var roles = await access.RolesAsync(cancellationToken);
        var me = access.Email;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await GateDefaults.EnsureAsync(db, cancellationToken);
        var query = db.Set<D.Opportunity>().AsNoTracking();
        if (!roles.CanViewPipeline())
        {
            query = query.Where(o => o.CaptureManagerEmail == me || o.ProposalManagerEmail == me);
        }

        if (!includeOutcomes)
        {
            query = query.Where(o => CrmMapping.OpenStages.Contains(o.Stage));
        }

        var opportunities = await query.ToListAsync(cancellationToken);
        var accountIds = opportunities.Where(o => o.AgencyAccountId != null).Select(o => o.AgencyAccountId!.Value).Distinct().ToList();
        var accounts = await db.Set<D.CrmAccount>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Acronym ?? a.Name, cancellationToken);
        var emails = opportunities.Select(o => o.CaptureManagerEmail).OfType<string>().Distinct().ToList();
        var names = await db.Set<AppUser>().AsNoTracking().Where(u => emails.Contains(u.Email)).ToDictionaryAsync(u => u.Email, u => u.DisplayName, cancellationToken);
        var pending = (await db.Set<D.OpportunityGateReview>().AsNoTracking().Where(r => r.Status == D.GateReviewStatus.Pending).Select(r => r.OpportunityId).ToListAsync(cancellationToken)).ToHashSet();
        var gates = await db.Set<D.GateDefinition>().AsNoTracking().Where(g => g.IsActive).ToListAsync(cancellationToken);

        var stages = includeOutcomes ? Enum.GetValues<OpportunityStage>() : OpportunityStages.Open.ToArray();
        var lanes = stages.Select(stage =>
        {
            var cards = opportunities.Where(o => o.Stage.ToContract() == stage)
                .OrderBy(o => o.ProposalDueOn ?? DateOnly.MaxValue).ThenByDescending(o => o.EstimatedValue)
                .Select(o => new PipelineCardDto(o.Id, o.Number, o.Name,
                    o.AgencyAccountId is { } a && accounts.TryGetValue(a, out var name) ? name : o.AgencyName, stage, o.EstimatedValue,
                    PipelineMath.Weighted(o.EstimatedValue, o.Pwin, o.Pgo), o.Pwin, o.Pgo, o.ProposalDueOn,
                    o.CaptureManagerEmail is { } c ? names.GetValueOrDefault(c) ?? c : null, o.Kind.ToContract(), CrmAccess.CanEdit(o, roles, me), pending.Contains(o.Id)))
                .ToList();
            return new PipelineLaneDto(stage, cards, cards.Sum(c => c.EstimatedValue), cards.Sum(c => c.WeightedValue),
                gates.FirstOrDefault(g => g.ToStage.ToContract() == stage)?.Name);
        }).ToList();
        return new PipelineBoardDto(lanes);
    }

    public async Task<ForecastDto> GetForecastAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunities = await db.Set<D.Opportunity>().AsNoTracking()
            .Where(o => o.Stage != D.OpportunityStage.Lost && o.Stage != D.OpportunityStage.NoBid).ToListAsync(cancellationToken);
        var years = await db.Set<FiscalYear>().AsNoTracking().ToListAsync(cancellationToken);
        return PipelineMath.Forecast(
            opportunities.Select(o => new PipelineMath.ForecastInput(o.Stage.ToContract(), o.Kind.ToContract(), o.EstimatedValue, o.Pwin, o.Pgo, o.EstimatedAwardDate, o.AwardedValue, o.DecidedOn)),
            date => QuarterOf(years, date));
    }

    /// <summary>The fiscal quarter from the organization's fiscal calendar when the date is in a defined year, else October–September.</summary>
    internal static (int Year, int Quarter) QuarterOf(IReadOnlyList<FiscalYear> years, DateOnly date) =>
        years.FirstOrDefault(y => y.Start <= date && y.End >= date) is { } year
            ? PipelineMath.FiscalQuarter(date, yearStart: (year.Start, year.Year))
            : PipelineMath.FiscalQuarter(date, years.Count > 0 ? years.OrderByDescending(y => y.Start).First().Start.Month : 10);

    public async Task<WinLossDto> GetWinLossAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var decided = (await db.Set<D.Opportunity>().AsNoTracking().Where(o => CrmMapping.OutcomeStages.Contains(o.Stage)).ToListAsync(cancellationToken))
            .Select(o => (Opportunity: o, On: o.DecidedOn ?? DateOnly.FromDateTime(o.StageChangedAtUtc)))
            .Where(x => (from is null || x.On >= from) && (to is null || x.On <= to))
            .ToList();
        var accountIds = decided.Where(x => x.Opportunity.AgencyAccountId != null).Select(x => x.Opportunity.AgencyAccountId!.Value).Distinct().ToList();
        var accounts = await db.Set<D.CrmAccount>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);
        var years = await db.Set<FiscalYear>().AsNoTracking().ToListAsync(cancellationToken);

        static decimal ValueOf(D.Opportunity o) => o.AwardedValue ?? o.EstimatedValue;
        WinLossByGroupDto Group(string name, IEnumerable<D.Opportunity> list)
        {
            var items = list.ToList();
            return new WinLossByGroupDto(name, items.Count(o => o.Stage == D.OpportunityStage.Won), items.Count(o => o.Stage == D.OpportunityStage.Lost),
                items.Count(o => o.Stage == D.OpportunityStage.NoBid), items.Where(o => o.Stage == D.OpportunityStage.Won).Sum(ValueOf),
                items.Where(o => o.Stage == D.OpportunityStage.Lost).Sum(ValueOf));
        }

        var all = decided.Select(x => x.Opportunity).ToList();
        var won = all.Where(o => o.Stage == D.OpportunityStage.Won).ToList();
        var lost = all.Where(o => o.Stage == D.OpportunityStage.Lost).ToList();
        var wonValue = won.Sum(ValueOf);
        var lostValue = lost.Sum(ValueOf);
        var reasons = all.Where(o => o.OutcomeReason != null).GroupBy(o => o.OutcomeReason!.Value.ToContract())
            .Select(g => new WinLossReasonDto(g.Key, g.Count(o => o.Stage == D.OpportunityStage.Won), g.Count(o => o.Stage == D.OpportunityStage.Lost), g.Count(o => o.Stage == D.OpportunityStage.NoBid)))
            .OrderByDescending(r => r.Won + r.Lost + r.NoBid).ThenBy(r => r.Reason).ToList();
        var byAgency = all.GroupBy(o => o.AgencyAccountId is { } a && accounts.TryGetValue(a, out var n) ? n : o.AgencyName ?? "(no agency)")
            .Select(g => Group(g.Key, g)).OrderByDescending(g => g.WonValue + g.LostValue).ThenBy(g => g.Group).ToList();
        var byYear = decided.GroupBy(x => QuarterOf(years, x.On).Year).OrderBy(g => g.Key).Select(g => Group($"FY{g.Key}", g.Select(x => x.Opportunity))).ToList();
        var byKind = all.GroupBy(o => o.Kind == D.OpportunityKind.New ? "New business" : "Recompete").OrderBy(g => g.Key).Select(g => Group(g.Key, g)).ToList();
        return new WinLossDto(won.Count, lost.Count, all.Count(o => o.Stage == D.OpportunityStage.NoBid), wonValue, lostValue,
            won.Count + lost.Count == 0 ? null : Math.Round(100m * won.Count / (won.Count + lost.Count), 1),
            wonValue + lostValue == 0 ? null : Math.Round(100m * wonValue / (wonValue + lostValue), 1),
            reasons, byAgency, byYear, byKind);
    }

    public async Task<IReadOnlyList<BpSpendDto>> GetBpSpendAsync(Guid? opportunityId = null, CancellationToken cancellationToken = default)
    {
        var roles = await access.RolesAsync(cancellationToken);
        var me = access.Email;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<D.Opportunity>().AsNoTracking().Where(o => o.BpWbsElementId != null);
        if (opportunityId is { } id)
        {
            query = query.Where(o => o.Id == id);
        }

        if (!roles.CanViewPipeline())
        {
            query = query.Where(o => o.CaptureManagerEmail == me || o.ProposalManagerEmail == me);
        }

        var opportunities = await query.ToListAsync(cancellationToken);
        var wbsIds = opportunities.Select(o => o.BpWbsElementId!.Value).ToList();
        var codes = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, cancellationToken);
        var entries = await (from e in db.Set<TimeEntry>().AsNoTracking()
                             join t in db.Set<Timesheet>() on e.TimesheetId equals t.Id
                             where wbsIds.Contains(e.WbsElementId) && t.Status != TimesheetStatus.Rejected
                             select new { e.WbsElementId, e.WorkDate, e.Hours, t.EmployeeId, Approved = t.Status == TimesheetStatus.Approved })
            .ToListAsync(cancellationToken);

        // Labor cost (approved hours at each employee's cost rate on the work date) is for Finance and administrators only.
        Dictionary<Guid, List<EmployeeCostRate>>? rates = null;
        if (roles.IsFinance())
        {
            var employees = entries.Select(e => e.EmployeeId).Distinct().ToList();
            rates = (await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => employees.Contains(r.EmployeeId)).ToListAsync(cancellationToken))
                .GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).ToList());
        }

        decimal RateOn(Guid employee, DateOnly date) =>
            rates!.GetValueOrDefault(employee)?.FirstOrDefault(r => r.EffectiveFrom <= date)?.HourlyCost ?? 0m;

        return opportunities.OrderBy(o => o.Number).Select(o =>
        {
            var mine = entries.Where(e => e.WbsElementId == o.BpWbsElementId).ToList();
            return new BpSpendDto(o.Id, o.Number, o.Name, o.Stage.ToContract(), codes.GetValueOrDefault(o.BpWbsElementId!.Value),
                mine.Where(e => e.Approved).Sum(e => e.Hours), mine.Where(e => !e.Approved).Sum(e => e.Hours),
                rates is null ? null : Math.Round(mine.Where(e => e.Approved).Sum(e => e.Hours * RateOn(e.EmployeeId, e.WorkDate)), 2),
                mine.Select(e => e.EmployeeId).Distinct().Count(), mine.Count == 0 ? null : mine.Min(e => e.WorkDate), mine.Count == 0 ? null : mine.Max(e => e.WorkDate));
        }).ToList();
    }

    public async Task<CrmSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var settings = await db.Set<D.CrmSettings>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var parent = await BpChargeCodes.FindParentAsync(db, cancellationToken);
        return new CrmSettingsDto(settings?.BpParentWbsElementId, parent?.Code, parent?.Name, parent is not null);
    }

    public async Task<CrmSettingsDto> SetBpParentAsync(Guid? wbsElementId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (wbsElementId is { } id)
        {
            var element = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
                ?? throw new ArgumentException("Choose an existing WBS element.");
            if (element.Kind != ChargeKind.Indirect)
            {
                throw new ArgumentException($"{element.Code} isn't indirect. B&P is an indirect cost: choose an indirect element.");
            }
        }

        var settings = await db.Set<D.CrmSettings>().FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            settings = new D.CrmSettings();
            db.Set<D.CrmSettings>().Add(settings);
        }

        var before = settings.BpParentWbsElementId;
        settings.BpParentWbsElementId = wbsElementId;
        settings.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        settings.UpdatedBy = access.Email;
        audit.Record(db, "CrmSettings", settings.Id, "Updated", new { BpParentWbsElementId = before }, new { BpParentWbsElementId = wbsElementId });
        await db.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BpParentOptionDto>> ListBpParentOptionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await (from w in db.Set<WbsElement>().AsNoTracking()
                          join p in db.Set<Project>() on w.ProjectId equals p.Id
                          where w.Kind == ChargeKind.Indirect && p.Status == ProjectStatus.Active
                          select new BpParentOptionDto(w.Id, w.Code, w.Name, p.Code)).ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.Code, StringComparer.Ordinal).ToList();
    }
}
