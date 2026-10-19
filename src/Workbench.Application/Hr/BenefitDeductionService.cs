using Microsoft.EntityFrameworkCore;
using Workbench.Application.Payroll;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Hr;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Hr;

namespace Workbench.Application.Hr;

/// <summary>
/// Benefit deductions for one pay period: every election in force on the period's last day for the pay group's
/// employees, per period (<see cref="BenefitRules.PerPeriod"/>), exported through the pay group's provider adapter.
/// Lines that can't be exported (no provider employee id) are listed with the issue and left out of the file.
/// </summary>
public sealed class BenefitDeductionService(
    IWorkbenchDbContextFactory dbFactory,
    PayrollProviders providers,
    AuditRecorder audit,
    TimeProvider clock,
    SecurityEventLog? securityLog = null) : IBenefitDeductionService
{
    public async Task<IReadOnlyList<DeductionPeriodDto>> ListPeriodsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var groups = await db.Set<PayGroup>().AsNoTracking().Where(g => g.IsActive).ToListAsync(cancellationToken);
        var exports = (await db.Set<D.BenefitDeductionExport>().AsNoTracking().Select(x => new { x.PayPeriodId, x.ExportedAtUtc }).ToListAsync(cancellationToken))
            .GroupBy(x => x.PayPeriodId).ToDictionary(g => g.Key, g => g.Max(x => x.ExportedAtUtc));
        var result = new List<DeductionPeriodDto>();
        foreach (var group in groups.OrderBy(g => g.Code, StringComparer.Ordinal))
        {
            var periods = await db.Set<PayPeriod>().AsNoTracking().Where(p => p.PayGroupId == group.Id).ToListAsync(cancellationToken);
            var past = periods.Where(p => p.Start <= today).OrderByDescending(p => p.Start).Take(4);
            var ahead = periods.Where(p => p.Start > today).OrderBy(p => p.Start).Take(2);
            result.AddRange(past.Concat(ahead).OrderByDescending(p => p.Start)
                .Select(p => new DeductionPeriodDto(group.Id, group.Code, p.Id, p.Start, p.End, p.CheckDate, exports.TryGetValue(p.Id, out var at) ? at : null)));
        }

        return result;
    }

    public async Task<DeductionPreviewDto> PreviewAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await BuildAsync(db, payGroupId, payPeriodId, cancellationToken)).Preview;
    }

    public async Task<DeductionFileDto> ExportAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (preview, group, period) = await BuildAsync(db, payGroupId, payPeriodId, cancellationToken);
        var lines = preview.Lines.Where(l => l.Issue is null).ToList();
        if (lines.Count == 0)
        {
            throw new ArgumentException("No one in this pay group has a benefit deduction to export for this period.");
        }

        var provider = providers.Get(group.ProviderKey);
        var file = provider.ExportDeductions(new PayrollDeductionExport(group.CompanyCode, group.Code, period.Start, period.End, period.CheckDate,
            lines.Select(l => new PayrollDeductionLine(l.ProviderEmployeeId!, l.EmployeeName, l.DeductionCode, l.EmployeePercent is null ? l.EmployeeAmount : null,
                l.EmployeePercent, l.EmployerAmount, l.IsPreTax)).ToList()));
        var export = new D.BenefitDeductionExport
        {
            PayGroupId = group.Id,
            PayPeriodId = period.Id,
            FileName = file.FileName,
            Content = file.Content,
            LineCount = lines.Count,
            EmployeeTotal = preview.EmployeeTotal,
            EmployerTotal = preview.EmployerTotal,
            ExportedBy = audit.Actor,
            ExportedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Set<D.BenefitDeductionExport>().Add(export);
        audit.Record(db, "BenefitDeductionExport", export.Id, "Exported", null, new { Group = group.Code, period.Start, period.End, export.LineCount });
        await db.SaveChangesAsync(cancellationToken);
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(HrSecurityEvents.DeductionsExported, $"pay group {group.Code}", detail: $"{lines.Count} deduction(s) for {period.Start:yyyy-MM-dd} – {period.End:yyyy-MM-dd}", cancellationToken: cancellationToken);
        }

        return new DeductionFileDto(file.FileName, file.ContentType, file.Content);
    }

    private static async Task<(DeductionPreviewDto Preview, PayGroup Group, PayPeriod Period)> BuildAsync(IWorkbenchDbContext db, Guid payGroupId, Guid payPeriodId, CancellationToken ct)
    {
        var group = await db.Set<PayGroup>().AsNoTracking().FirstOrDefaultAsync(g => g.Id == payGroupId, ct) ?? throw WorkbenchNotFoundException.For("Pay group", payGroupId);
        var period = await db.Set<PayPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == payPeriodId && p.PayGroupId == payGroupId, ct)
            ?? throw WorkbenchNotFoundException.For("Pay period", payPeriodId);
        var profiles = await db.Set<EmployeePayProfile>().AsNoTracking().Where(p => p.PayGroupId == group.Id).ToListAsync(ct);
        var ids = profiles.Select(p => p.EmployeeId).ToList();
        var employees = await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var on = period.End;
        var elections = await db.Set<D.BenefitElection>().AsNoTracking()
            .Where(e => ids.Contains(e.EmployeeId) && !e.IsWaived && e.CoverageStart <= on && (e.CoverageEnd == null || e.CoverageEnd >= on))
            .ToListAsync(ct);
        var planIds = elections.Select(e => e.PlanId).Distinct().ToList();
        var plans = await db.Set<D.BenefitPlan>().AsNoTracking().Include(p => p.Tiers).Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var lines = new List<DeductionLineDto>();
        foreach (var election in elections)
        {
            var profile = profiles.First(p => p.EmployeeId == election.EmployeeId);
            var employee = employees[election.EmployeeId];
            var plan = plans[election.PlanId];
            var tier = plan.Tiers.FirstOrDefault(t => t.Id == election.TierId);
            decimal employeeAmount, employerAmount;
            decimal? percent = null;
            switch (plan.Type)
            {
                case D.BenefitPlanType.Retirement401k:
                    // A percent of each check's pay: Paylocity calculates the amount and the match.
                    percent = election.ContributionAmount;
                    employeeAmount = 0m;
                    employerAmount = 0m;
                    break;
                case D.BenefitPlanType.Fsa or D.BenefitPlanType.Hsa:
                    employeeAmount = Math.Round((election.ContributionAmount ?? 0m) / BenefitRules.PeriodsPerYear(group.Frequency), 2, MidpointRounding.AwayFromZero);
                    employerAmount = BenefitRules.PerPeriod(plan.EmployerMonthlyContribution ?? 0m, group.Frequency);
                    break;
                default:
                    employeeAmount = BenefitRules.PerPeriod(tier?.EmployeeMonthlyCost ?? 0m, group.Frequency);
                    employerAmount = BenefitRules.PerPeriod(tier?.EmployerMonthlyCost ?? 0m, group.Frequency);
                    break;
            }

            var issue = string.IsNullOrWhiteSpace(profile.ProviderEmployeeId) ? "No payroll provider employee id" : null;
            lines.Add(new DeductionLineDto(employee.Id, employee.DisplayName, profile.ProviderEmployeeId, plan.Code, plan.DeductionCode,
                employeeAmount, percent, employerAmount, plan.IsPreTax, issue));
        }

        var last = await db.Set<D.BenefitDeductionExport>().AsNoTracking().Where(x => x.PayGroupId == group.Id && x.PayPeriodId == period.Id)
            .OrderByDescending(x => x.ExportedAtUtc).Select(x => (DateTime?)x.ExportedAtUtc).FirstOrDefaultAsync(ct);
        var preview = new DeductionPreviewDto(group.Id, group.Code, period.Id, period.Start, period.End, group.ProviderKey,
            lines.OrderBy(l => l.EmployeeName).ThenBy(l => l.PlanCode, StringComparer.Ordinal).ToList(), last);
        return (preview, group, period);
    }
}

/// <summary>
/// Splits the employer benefit cost on a payroll register across each plan's GL expense account (weighted by the
/// employer cost of the elections in force on the check date), so the payroll journal posts it where the fringe pool
/// reads it. Employees without elections, and plans without an account, stay on the payroll default account.
/// </summary>
public static class BenefitFringe
{
    public static async Task<IReadOnlyList<(Guid? AccountId, decimal Amount)>> SplitAsync(
        IWorkbenchDbContext db, IReadOnlyCollection<(Guid? EmployeeId, decimal EmployerBenefits)> employees, DateOnly on, CancellationToken ct)
    {
        var ids = employees.Where(e => e.EmployeeId is not null && e.EmployerBenefits != 0).Select(e => e.EmployeeId!.Value).Distinct().ToList();
        var weights = new Dictionary<Guid, List<(Guid? Account, decimal Weight)>>();
        if (ids.Count > 0)
        {
            var elections = await db.Set<D.BenefitElection>().AsNoTracking()
                .Where(e => ids.Contains(e.EmployeeId) && !e.IsWaived && e.CoverageStart <= on && (e.CoverageEnd == null || e.CoverageEnd >= on)).ToListAsync(ct);
            var planIds = elections.Select(e => e.PlanId).Distinct().ToList();
            var plans = await db.Set<D.BenefitPlan>().AsNoTracking().Include(p => p.Tiers).Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            foreach (var group in elections.GroupBy(e => e.EmployeeId))
            {
                var pay = await BenefitService.AnnualPayAsync(db, group.Key, on, ct);
                weights[group.Key] = group.Select(e =>
                {
                    var plan = plans[e.PlanId];
                    return (plan.ExpenseAccountId, BenefitRules.MonthlyCost(plan, plan.Tiers.FirstOrDefault(t => t.Id == e.TierId), e, pay).Employer);
                }).Where(w => w.Item2 > 0).ToList();
            }
        }

        var totals = new Dictionary<Guid, decimal>();
        var unmapped = 0m;
        foreach (var (employeeId, amount) in employees)
        {
            var mine = employeeId is { } id ? weights.GetValueOrDefault(id) : null;
            var total = mine?.Sum(w => w.Weight) ?? 0m;
            if (mine is null || total <= 0)
            {
                unmapped += amount;
                continue;
            }

            var allocated = 0m;
            foreach (var (account, weight) in mine)
            {
                var share = Math.Round(amount * weight / total, 2, MidpointRounding.AwayFromZero);
                allocated += share;
                if (account is { } a)
                {
                    totals[a] = totals.GetValueOrDefault(a) + share;
                }
                else
                {
                    unmapped += share;
                }
            }

            unmapped += amount - allocated; // rounding remainder
        }

        var result = totals.Where(t => t.Value != 0).OrderBy(t => t.Key).Select(t => ((Guid?)t.Key, t.Value)).ToList();
        if (unmapped != 0)
        {
            result.Add((null, unmapped));
        }

        return result;
    }
}
