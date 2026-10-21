using Workbench.Application.Time;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.IndirectRates;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Time;
using static Workbench.Contracts.Reports.ReportParameterKind;

namespace Workbench.Application.Reports;

/// <summary>
/// The standard government-contractor reports. Each reads through the permission-checked services (project financials,
/// burdened cost, funding, indirect rates, AR/AP reports, time reports) or applies the same rules through
/// <see cref="ReportAccess"/>, so a report never shows more than the pages it summarizes.
/// </summary>
public sealed partial class StandardReportService(
    IWorkbenchDbContextFactory dbFactory,
    ReportAccess access,
    TimekeepingClock clock,
    IProjectService projectService,
    IContractService contractService,
    IFiscalCalendarService calendar,
    IProjectFinancialsService financials,
    IBurdenedCostService burdenedCost,
    IBurdenedFinancialsService burdenedFinancials,
    IFundingService funding,
    IIndirectRateService indirectRates,
    ITimeReportService timeReports,
    IArReportService arReports,
    IApReportService apReports) : IStandardReportService
{
    private sealed record Entry(string Key, string Name, string Description, string Category, ReportParameterKind[] Parameters, string? PageLink, Func<ReportAccessContext, string?> Unavailable);

    private static readonly Entry[] Catalog =
    [
        new(StandardReportKeys.ProjectStatus, "Project status report",
            "Budget, period and inception-to-date actuals (direct and burdened), ETC/EAC, variance, funded value and billed to date by project and WBS.",
            "Projects", [DateRange, Project, ReportParameterKind.RateBasis], null,
            a => a.ManagesAnyProject ? null : "Project status is for Finance, administrators and project managers (for their projects)."),
        new(StandardReportKeys.LaborDistribution, "Labor distribution",
            "Hours by employee × project × period. Labor cost is shown to Finance and administrators.",
            "Labor", [DateRange, Project, Employee, PeriodGrouping, IncludeUnapproved], "time/team", _ => null),
        new(StandardReportKeys.IndirectRateVariance, "Indirect rate variance",
            "Actual (from the GL) vs provisional vs target rates by pool and period, with under/over-applied burden.",
            "Indirect rates", [FiscalYear], "accounting/indirect-rates",
            a => a.IsFinance ? null : "Indirect rates are for Finance and administrators."),
        new(StandardReportKeys.FundingStatus, "Contract funding and ceiling status",
            "Ceiling, funded value, incurred and committed cost, remaining funding and projected exhaustion by contract and CLIN.",
            "Contracts", [Contract], "setup/contracts",
            a => a.IsFinance || a.FinancialContracts is not { Count: 0 } ? null : "Contract funding is for Finance, administrators and the managers of a contract's projects."),
        new(StandardReportKeys.TimesheetCompliance, "Timesheet compliance",
            "Missing and late timesheets, days entered late and entries revised after submission (DCAA).",
            "Labor", [DateRange, Employee], "time/team", _ => null),
        new(StandardReportKeys.ArAging, "AR aging summary",
            "Open receivables by customer and days past due.", "Billing", [AsOf], "billing/aging",
            a => a.IsFinance ? null : "Receivables are for Finance and administrators."),
        new(StandardReportKeys.ApAging, "AP aging summary",
            "Open payables by vendor and days past due.", "Purchasing", [AsOf], "purchasing/reports",
            a => a.IsFinance ? null : "Payables are for Finance and administrators."),
    ];

    public async Task<IReadOnlyList<StandardReportDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var context = await access.GetAsync(cancellationToken);
        return Catalog.Select(e =>
        {
            var reason = e.Unavailable(context);
            return new StandardReportDto(e.Key, e.Name, e.Description, e.Category, e.Parameters, reason is null, reason, e.PageLink);
        }).ToList();
    }

    public async Task<ReportResultDto> RunAsync(string reportKey, ReportParameters parameters, CancellationToken cancellationToken = default)
    {
        var (entry, context) = await AuthorizeAsync(reportKey, cancellationToken);
        RequireVisibleProject(context, parameters);
        parameters ??= new ReportParameters();
        var builder = new ReportBuilder(entry.Name);
        switch (entry.Key)
        {
            case StandardReportKeys.ProjectStatus: await ProjectStatusAsync(builder, context, parameters, cancellationToken); break;
            case StandardReportKeys.LaborDistribution: await LaborDistributionAsync(builder, context, parameters, cancellationToken); break;
            case StandardReportKeys.IndirectRateVariance: await IndirectRateVarianceAsync(builder, parameters, cancellationToken); break;
            case StandardReportKeys.FundingStatus: await FundingStatusAsync(builder, context, parameters, cancellationToken); break;
            case StandardReportKeys.TimesheetCompliance: await TimesheetComplianceAsync(builder, context, parameters, cancellationToken); break;
            case StandardReportKeys.ArAging: await ArAgingAsync(builder, parameters, cancellationToken); break;
            case StandardReportKeys.ApAging: await ApAgingAsync(builder, parameters, cancellationToken); break;
        }

        return builder.Build(clock.UtcNow);
    }

    public async Task<ReportResultDto> DrillAsync(string reportKey, ReportParameters parameters, string drillKey, CancellationToken cancellationToken = default)
    {
        var (entry, context) = await AuthorizeAsync(reportKey, cancellationToken);
        RequireVisibleProject(context, parameters);
        parameters ??= new ReportParameters();
        var key = DrillKey.Parse(drillKey);
        if (key.Id("project") is { } drilled && !context.CanSeeProject(drilled))
        {
            throw WorkbenchNotFoundException.For("Project", drilled);
        }

        var builder = new ReportBuilder($"{entry.Name}: detail");
        switch (entry.Key)
        {
            case StandardReportKeys.ProjectStatus: await ProjectStatusDrillAsync(builder, context, parameters, key, cancellationToken); break;
            case StandardReportKeys.LaborDistribution: await LaborDrillAsync(builder, context, parameters, key, cancellationToken); break;
            case StandardReportKeys.IndirectRateVariance: await IndirectRateDrillAsync(builder, key, cancellationToken); break;
            case StandardReportKeys.FundingStatus: await FundingDrillAsync(builder, context, key, cancellationToken); break;
            case StandardReportKeys.TimesheetCompliance: await ComplianceDrillAsync(builder, context, key, cancellationToken); break;
            case StandardReportKeys.ArAging: await ArDrillAsync(builder, parameters, key, cancellationToken); break;
            case StandardReportKeys.ApAging: await ApDrillAsync(builder, parameters, key, cancellationToken); break;
        }

        return builder.Build(clock.UtcNow);
    }

    public async Task<ReportFileDto> ExportAsync(string reportKey, ReportParameters parameters, ReportFileFormat format, CancellationToken cancellationToken = default) =>
        ReportFiles.Render(await RunAsync(reportKey, parameters, cancellationToken), format);

    private async Task<(Entry Entry, ReportAccessContext Context)> AuthorizeAsync(string reportKey, CancellationToken ct)
    {
        var entry = Catalog.FirstOrDefault(e => e.Key == reportKey) ?? throw new WorkbenchNotFoundException($"There is no standard report '{reportKey}'.");
        var context = await access.GetAsync(ct);
        if (entry.Unavailable(context) is { } reason)
        {
            throw new WorkbenchForbiddenException(reason);
        }

        return (entry, context);
    }

    /// <summary>A project parameter must be a project the user can see: a hidden one is not found.</summary>
    private static void RequireVisibleProject(ReportAccessContext context, ReportParameters? parameters)
    {
        if (parameters?.ProjectId is { } projectId && !context.CanSeeProject(projectId))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }
    }

    /// <summary>The report's date range: the parameters' range or dates, else <paramref name="fallback"/>; never past today when <paramref name="capAtToday"/>.</summary>
    private async Task<(DateOnly From, DateOnly To)> RangeAsync(ReportParameters p, ReportDateRange fallback, bool capAtToday, CancellationToken ct)
    {
        var today = clock.Today;
        var range = p.Range == ReportDateRange.Custom && p.From is null && p.To is null ? fallback : p.Range;
        await using var db = await dbFactory.CreateAsync(ct);
        var (from, to) = await ReportDates.ResolveAsync(db, range, p.From, p.To, today, ct);
        var end = to ?? today;
        if (capAtToday && end > today)
        {
            end = today;
        }

        var start = from ?? new DateOnly(2000, 1, 1);
        if (end < start)
        {
            end = start;
        }

        return (start, end);
    }

    /// <summary>Drill keys are <c>name:value|name:value</c>; values are ids, dates or enum names.</summary>
    internal sealed class DrillKey
    {
        private readonly Dictionary<string, string> _parts;

        private DrillKey(Dictionary<string, string> parts) => _parts = parts;

        public static string Of(params (string Name, object Value)[] parts) =>
            string.Join('|', parts.Select(p => $"{p.Name}:{(p.Value is Enum ? p.Value.ToString() : ReportValues.Format(p.Value))}"));

        public static DrillKey Parse(string? key)
        {
            var parts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in (key ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = part.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0)
                {
                    throw new ArgumentException("That drill-down key isn't valid.");
                }

                parts[part[..colon]] = part[(colon + 1)..];
            }

            return parts.Count == 0 ? throw new ArgumentException("A drill-down key is required.") : new DrillKey(parts);
        }

        public Guid? Id(string name) => _parts.TryGetValue(name, out var v) && Guid.TryParse(v, out var g) ? g : null;

        public Guid RequireId(string name) => Id(name) ?? throw new ArgumentException("That drill-down key isn't valid.");

        public DateOnly? Date(string name) => _parts.TryGetValue(name, out var v) ? ReportValues.Date(v) : null;

        public string? Text(string name) => _parts.GetValueOrDefault(name);
    }
}
