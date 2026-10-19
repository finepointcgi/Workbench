using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Security;
using Workbench.Contracts.Hr;
using Workbench.Domain.Accounting;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Hr;

namespace Workbench.Application.Hr;

/// <summary>HR reports. Role gates are in the permission decorators; EEO-1 and VETS-4212 exports are security events.</summary>
public sealed class HrReportService(IWorkbenchDbContextFactory dbFactory, SecurityEventLog? securityLog = null) : IHrReportService
{
    public async Task<HeadcountReportDto> GetHeadcountAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var people = await PeopleAsync(db, cancellationToken);
        var active = people.Where(p => p.ActiveOn(asOf)).ToList();
        HeadcountRowDto Row(string group, IEnumerable<Person> list)
        {
            var l = list.ToList();
            return new HeadcountRowDto(group, l.Count, l.Count(p => p.Type == EmploymentType.FullTime), l.Count(p => p.Type == EmploymentType.PartTime),
                l.Count(p => p.Type is not (EmploymentType.FullTime or EmploymentType.PartTime)));
        }

        return new HeadcountReportDto(asOf, active.Count,
            active.GroupBy(p => p.OrgUnit ?? "No org unit").OrderBy(g => g.Key).Select(g => Row(g.Key, g)).ToList(),
            active.GroupBy(p => p.Type).OrderBy(g => g.Key).Select(g => Row(HrLabels.Label((Contracts.Payroll.EmploymentTypeKind)(int)g.Key), g)).ToList());
    }

    /// <summary>Turnover = separations ÷ average of the start and end headcount.</summary>
    public async Task<TurnoverReportDto> GetTurnoverAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from || to.DayNumber - from.DayNumber > 3660)
        {
            throw new ArgumentException("Choose a range of at most ten years, ending after it starts.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var people = await PeopleAsync(db, cancellationToken);
        var start = people.Count(p => p.ActiveOn(from));
        var end = people.Count(p => p.ActiveOn(to));
        var separations = people.Where(p => p.Terminated is { } t && t >= from && t <= to).OrderBy(p => p.Terminated).ToList();
        var average = (start + end) / 2m;
        return new TurnoverReportDto(from, to, start, end, people.Count(p => p.Hired >= from && p.Hired <= to), separations.Count,
            separations.Count(p => p.Reason is D.TerminationReason.Voluntary or D.TerminationReason.Retirement),
            separations.Count(p => p.Reason == D.TerminationReason.Involuntary),
            average, average == 0 ? 0 : Math.Round(separations.Count / average, 4),
            separations.Select(p => new SeparationDto(p.Id, p.Name, p.Terminated!.Value, p.Reason is { } r ? (TerminationReason)(int)r : null, p.OrgUnit)).ToList());
    }

    public async Task<Eeo1ReportDto> GetEeo1Async(DateOnly snapshotDate, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var people = (await PeopleAsync(db, cancellationToken)).Where(p => p.ActiveOn(snapshotDate)).ToList();
        var cells = people.Where(p => p.JobCategory is not null)
            .GroupBy(p => (p.JobCategory!.Value, p.Race, p.Gender))
            .Select(g => new Eeo1CellDto((EeoJobCategory)(int)g.Key.Item1, (EeoRace)(int)g.Key.Race, (EeoGender)(int)g.Key.Gender, g.Count()))
            .OrderBy(c => c.JobCategory).ThenBy(c => c.Race).ThenBy(c => c.Gender)
            .ToList();
        return new Eeo1ReportDto(snapshotDate, people.Count, people.Count(p => p.Race == D.EeoRace.NotDisclosed || p.Gender == D.EeoGender.NotDisclosed),
            people.Count(p => p.JobCategory is null), cells);
    }

    public async Task<HrExportFile> ExportEeo1Async(DateOnly snapshotDate, CancellationToken cancellationToken = default)
    {
        var report = await GetEeo1Async(snapshotDate, cancellationToken);
        var csv = new HrCsv("Job category", "Race/ethnicity", "Sex", "Employees");
        foreach (var c in report.Cells)
        {
            csv.Row(HrLabels.Label(c.JobCategory), HrLabels.Label(c.Race), c.Gender.ToString(), c.Count.ToString(CultureInfo.InvariantCulture));
        }

        await RecordAsync(HrSecurityEvents.Eeo1Exported, $"EEO-1 snapshot {snapshotDate:yyyy-MM-dd}", cancellationToken);
        return new HrExportFile($"eeo1-{snapshotDate:yyyyMMdd}.csv", "text/csv", csv.ToString());
    }

    /// <summary>The VETS-4212 12-month period ends on <paramref name="periodEnd"/>: employees on that day, hires in the year before.</summary>
    public async Task<Vets4212ReportDto> GetVets4212Async(DateOnly periodEnd, CancellationToken cancellationToken = default)
    {
        var periodStart = periodEnd.AddYears(-1).AddDays(1);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var people = await PeopleAsync(db, cancellationToken);
        var onEnd = people.Where(p => p.ActiveOn(periodEnd)).ToList();
        var hires = people.Where(p => p.Hired >= periodStart && p.Hired <= periodEnd).ToList();
        var rows = Enum.GetValues<D.EeoJobCategory>().Select(c => new Vets4212RowDto((EeoJobCategory)(int)c,
                onEnd.Count(p => p.JobCategory == c && p.ProtectedVeteran), onEnd.Count(p => p.JobCategory == c),
                hires.Count(p => p.JobCategory == c && p.ProtectedVeteran), hires.Count(p => p.JobCategory == c)))
            .ToList();

        // Max/min employees during the period: checked on each month end.
        var monthly = Enumerable.Range(0, 12).Select(i => new DateOnly(periodStart.Year, periodStart.Month, 1).AddMonths(i + 1).AddDays(-1))
            .Select(d => people.Count(p => p.ActiveOn(d))).ToList();
        return new Vets4212ReportDto(periodStart, periodEnd, rows, monthly.Max(), monthly.Min());
    }

    public async Task<HrExportFile> ExportVets4212Async(DateOnly periodEnd, CancellationToken cancellationToken = default)
    {
        var report = await GetVets4212Async(periodEnd, cancellationToken);
        var csv = new HrCsv("Job category", "Protected veteran employees", "Total employees", "Protected veteran new hires", "Total new hires");
        foreach (var r in report.Rows)
        {
            csv.Row(HrLabels.Label(r.JobCategory), N(r.ProtectedVeteranEmployees), N(r.TotalEmployees), N(r.ProtectedVeteranHires), N(r.TotalHires));
        }

        csv.Row("Maximum employees in the period", N(report.MaxEmployees), "", "", "");
        csv.Row("Minimum employees in the period", N(report.MinEmployees), "", "", "");
        await RecordAsync(HrSecurityEvents.Vets4212Exported, $"VETS-4212 period ending {periodEnd:yyyy-MM-dd}", cancellationToken);
        return new HrExportFile($"vets4212-{periodEnd:yyyyMMdd}.csv", "text/csv", csv.ToString());
    }

    public async Task<BenefitSummaryDto> GetBenefitSummaryAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var people = (await PeopleAsync(db, cancellationToken)).Where(p => p.ActiveOn(asOf)).ToList();
        var ids = people.Select(p => p.Id).ToHashSet();
        var elections = (await db.Set<D.BenefitElection>().AsNoTracking()
                .Where(e => e.CoverageStart <= asOf && (e.CoverageEnd == null || e.CoverageEnd >= asOf)).ToListAsync(cancellationToken))
            .Where(e => ids.Contains(e.EmployeeId)).ToList();
        var plans = await db.Set<D.BenefitPlan>().AsNoTracking().Include(p => p.Tiers).ToDictionaryAsync(p => p.Id, cancellationToken);
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => $"{a.Number} {a.Name}", cancellationToken);
        var pay = new Dictionary<Guid, decimal?>();
        foreach (var id in elections.Where(e => e.PlanType == D.BenefitPlanType.Retirement401k).Select(e => e.EmployeeId).Distinct())
        {
            pay[id] = await BenefitService.AnnualPayAsync(db, id, asOf, cancellationToken);
        }

        var rows = elections.Where(e => !e.IsWaived)
            .GroupBy(e => (e.PlanId, e.Coverage))
            .Select(g =>
            {
                var plan = plans[g.Key.PlanId];
                var costs = g.Select(e => BenefitRules.MonthlyCost(plan, plan.Tiers.FirstOrDefault(t => t.Id == e.TierId), e, pay.GetValueOrDefault(e.EmployeeId))).ToList();
                return new BenefitSummaryRowDto(plan.Code, plan.Name, (BenefitPlanType)(int)plan.Type,
                    g.Key.Coverage is { } c ? BenefitLabels.Label((CoverageLevel)(int)c) : null, g.Count(), costs.Sum(x => x.Employee), costs.Sum(x => x.Employer),
                    plan.ExpenseAccountId is { } a ? accounts.GetValueOrDefault(a) : null);
            })
            .OrderBy(r => r.Type).ThenBy(r => r.PlanCode, StringComparer.Ordinal).ThenBy(r => r.Coverage)
            .ToList();
        return new BenefitSummaryDto(asOf, people.Count, elections.Where(e => !e.IsWaived).Select(e => e.EmployeeId).Distinct().Count(),
            elections.Count(e => e.IsWaived), rows);
    }

    public async Task<HrExportFile> ExportBenefitSummaryAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        var report = await GetBenefitSummaryAsync(asOf, cancellationToken);
        var csv = new HrCsv("Plan", "Name", "Type", "Coverage", "Enrolled", "Employee monthly", "Employer monthly", "GL account");
        foreach (var r in report.Rows)
        {
            csv.Row(r.PlanCode, r.PlanName, BenefitLabels.Label(r.Type), r.Coverage ?? "", N(r.Enrolled), M(r.EmployeeMonthly), M(r.EmployerMonthly), r.ExpenseAccount ?? "");
        }

        await RecordAsync(HrSecurityEvents.BenefitSummaryExported, $"Benefits as of {asOf:yyyy-MM-dd}", cancellationToken);
        return new HrExportFile($"benefits-{asOf:yyyyMMdd}.csv", "text/csv", csv.ToString());
    }

    private async Task RecordAsync(string type, string target, CancellationToken ct)
    {
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(type, target, detail: "HR report exported", cancellationToken: ct);
        }
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string M(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private sealed record Person(
        Guid Id, string Name, DateOnly Hired, DateOnly? Terminated, D.TerminationReason? Reason, string? OrgUnit, EmploymentType Type,
        D.EeoJobCategory? JobCategory, D.EeoRace Race, D.EeoGender Gender, bool ProtectedVeteran)
    {
        public bool ActiveOn(DateOnly date) => Hired <= date && (Terminated is null || Terminated >= date);
    }

    /// <summary>Everyone, with the EEO job category from the record or else the position's.</summary>
    private static async Task<List<Person>> PeopleAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var employees = await db.Set<Employee>().AsNoTracking().ToListAsync(ct);
        var units = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name, ct);
        var profiles = await db.Set<D.EmployeeHrProfile>().AsNoTracking().ToDictionaryAsync(p => p.EmployeeId, ct);
        var pay = await db.Set<EmployeePayProfile>().AsNoTracking().ToDictionaryAsync(p => p.EmployeeId, p => p.EmploymentType, ct);
        var eeo = await db.Set<D.EmployeeEeoRecord>().AsNoTracking().ToDictionaryAsync(r => r.EmployeeId, ct);
        var positions = await db.Set<D.Position>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.EeoJobCategory, ct);
        return employees.Select(e =>
        {
            var profile = profiles.GetValueOrDefault(e.Id);
            var record = eeo.GetValueOrDefault(e.Id);
            var category = record?.JobCategory ?? (profile?.PositionId is { } pid ? positions.GetValueOrDefault(pid) : null);
            return new Person(e.Id, e.DisplayName, e.HireDate, e.TerminationDate, profile?.TerminationReason, e.OrgUnitId is { } u ? units.GetValueOrDefault(u) : null,
                profile?.EmploymentType ?? (pay.TryGetValue(e.Id, out var t) ? t : EmploymentType.FullTime), category,
                record?.Race ?? D.EeoRace.NotDisclosed, record?.Gender ?? D.EeoGender.NotDisclosed, record?.IsProtectedVeteran ?? false);
        }).ToList();
    }
}

/// <summary>A small CSV writer; cells starting with = + - @ are prefixed with ' so spreadsheets don't run them.</summary>
internal sealed class HrCsv
{
    private readonly StringBuilder _sb = new();

    public HrCsv(params string[] header) => Row(header);

    public void Row(params string[] cells) =>
        _sb.AppendJoin(',', cells.Select(Cell)).Append("\r\n");

    public override string ToString() => _sb.ToString();

    private static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
