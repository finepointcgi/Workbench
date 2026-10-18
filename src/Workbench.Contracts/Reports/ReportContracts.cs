using Workbench.Contracts.Billing;
using Workbench.Contracts.Identity;

namespace Workbench.Contracts.Reports;

// Reporting: the standard government-contractor reports, a report writer over curated data sources, saved reports and
// scheduled email delivery. Every report runs with the caller's permissions: standard reports read through the
// permission-checked services, and the report writer's data sources apply the same row and field rules (cost rates
// only for Finance/Admin, employees see only their own time, contract values only for Finance and the contract's PMs).

// ---- Results (shared by standard reports and the report writer) ----

public enum ReportColumnType { Text, Number, Money, Hours, Rate, Percent, Date, Boolean }

public enum ReportRowKind { Detail, Subtotal, Total }

public sealed record ReportColumnDto(string Key, string Label, ReportColumnType Type);

/// <summary>
/// One row. <see cref="Values"/> line up with the result's columns and are invariant strings: numbers <c>1234.5</c>
/// (rates and percents as fractions, 0.32 = 32%), dates <c>yyyy-MM-dd</c>, booleans <c>true</c>/<c>false</c>, null for blank.
/// <see cref="DrillKey"/>, when set, can be passed to the report's drill-down to list the source rows behind this one;
/// <see cref="Link"/> is an app-relative page for the row (an invoice, a timesheet, a project's financials).
/// </summary>
public sealed record ReportRowDto(ReportRowKind Kind, int Level, IReadOnlyList<string?> Values, string? DrillKey = null, string? Link = null);

public sealed record ReportResultDto(
    string Title,
    string? Subtitle,
    IReadOnlyList<ReportColumnDto> Columns,
    IReadOnlyList<ReportRowDto> Rows,
    IReadOnlyList<string> Notes,
    DateTime GeneratedAtUtc,

    /// <summary>True when the data source had more rows than a report may hold; narrow the filters.</summary>
    bool Truncated = false);

public enum ReportFileFormat { Xlsx, Csv }

public sealed record ReportFileDto(string FileName, string ContentType, byte[] Content);

/// <summary>A date range resolved when the report runs, so saved and scheduled reports stay current.</summary>
public enum ReportDateRange
{
    Custom,
    Today,
    Yesterday,
    ThisWeek,
    LastWeek,
    ThisMonth,
    LastMonth,
    Last30Days,
    Last90Days,
    YearToDate,
    ThisFiscalYear,
    LastFiscalYear,
    All,
}

// ---- Standard reports ----

public static class StandardReportKeys
{
    public const string ProjectStatus = "project-status";
    public const string LaborDistribution = "labor-distribution";
    public const string IndirectRateVariance = "indirect-rate-variance";
    public const string FundingStatus = "funding-status";
    public const string TimesheetCompliance = "timesheet-compliance";
    public const string ArAging = "ar-aging";
    public const string ApAging = "ap-aging";

    public static readonly IReadOnlyList<string> All =
        [ProjectStatus, LaborDistribution, IndirectRateVariance, FundingStatus, TimesheetCompliance, ArAging, ApAging];
}

public enum ReportParameterKind { DateRange, AsOf, Project, Contract, Employee, FiscalYear, RateBasis, PeriodGrouping, IncludeUnapproved }

/// <summary>How the labor distribution splits time.</summary>
public enum ReportPeriodGrouping { FiscalPeriod, Month, Week, None }

/// <summary>
/// Inputs to a standard report; each report reads the ones its catalog entry lists. <see cref="Range"/> (unless
/// <see cref="ReportDateRange.Custom"/>) wins over <see cref="From"/>/<see cref="To"/>.
/// </summary>
public sealed record ReportParameters(
    DateOnly? From = null,
    DateOnly? To = null,
    ReportDateRange Range = ReportDateRange.Custom,
    DateOnly? AsOf = null,
    Guid? ProjectId = null,
    Guid? ContractId = null,
    Guid? EmployeeId = null,
    int? FiscalYear = null,
    RateBasis? Basis = null,
    ReportPeriodGrouping Grouping = ReportPeriodGrouping.FiscalPeriod,
    bool IncludeUnapproved = false);

/// <summary>
/// A catalog entry. <see cref="Available"/> is false when the signed-in user can't run it (the server refuses too);
/// <see cref="PageLink"/> is the existing page with the same data, when there is one.
/// </summary>
public sealed record StandardReportDto(
    string Key,
    string Name,
    string Description,
    string Category,
    IReadOnlyList<ReportParameterKind> Parameters,
    bool Available,
    string? UnavailableReason,
    string? PageLink);

public interface IStandardReportService
{
    Task<IReadOnlyList<StandardReportDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a standard report. Unknown keys are not found; reports the user can't run are forbidden.</summary>
    Task<ReportResultDto> RunAsync(string reportKey, ReportParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>The source rows behind a row's <see cref="ReportRowDto.DrillKey"/> (time entries, ODCs, funding actions, ...).</summary>
    Task<ReportResultDto> DrillAsync(string reportKey, ReportParameters parameters, string drillKey, CancellationToken cancellationToken = default);

    Task<ReportFileDto> ExportAsync(string reportKey, ReportParameters parameters, ReportFileFormat format, CancellationToken cancellationToken = default);
}

// ---- Report writer ----

public static class ReportSourceKeys
{
    public const string GlLines = "gl-lines";
    public const string TimeEntries = "time-entries";
    public const string Odcs = "odcs";
    public const string Invoices = "invoices";
    public const string ApBills = "ap-bills";
    public const string ProjectsWbs = "projects-wbs";
    public const string Employees = "employees";
}

/// <summary>A field of a data source. <see cref="Restricted"/> fields are listed only to users who may see them.</summary>
public sealed record ReportFieldDto(string Key, string Label, ReportColumnType Type, bool Restricted = false, string? Description = null);

/// <summary>A data source the user may report on, with only the fields they may see. <see cref="DateField"/> is what the date range filters.</summary>
public sealed record ReportDataSourceDto(string Key, string Name, string Description, string? DateField, IReadOnlyList<ReportFieldDto> Fields);

public enum ReportFilterOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    IsBlank,
    IsNotBlank,

    /// <summary>Any of the comma-separated values.</summary>
    OneOf,
}

public enum ReportAggregate { None, Sum, Count, Average, Min, Max }

public sealed record ReportColumnSpec(string Field, string? Label = null, ReportAggregate Aggregate = ReportAggregate.None);

/// <summary>Values are invariant strings, as in results (dates <c>yyyy-MM-dd</c>, numbers <c>1234.5</c>).</summary>
public sealed record ReportFilterSpec(string Field, ReportFilterOperator Operator, string? Value = null);

public sealed record ReportSortSpec(string Field, bool Descending = false);

/// <summary>
/// A report-writer report: columns, filters, up to three grouping levels (each with a subtotal row) and sort, over one
/// data source. With <see cref="ShowDetails"/> false only subtotals and the total are listed.
/// </summary>
public sealed record ReportDefinition(
    string Source,
    IReadOnlyList<ReportColumnSpec> Columns,
    IReadOnlyList<ReportFilterSpec>? Filters = null,
    IReadOnlyList<string>? GroupBy = null,
    IReadOnlyList<ReportSortSpec>? Sort = null,
    ReportDateRange Range = ReportDateRange.All,
    DateOnly? From = null,
    DateOnly? To = null,
    bool ShowDetails = true,
    string? Title = null);

public interface IReportWriterService
{
    /// <summary>The data sources the user may use, each with the fields they may see.</summary>
    Task<IReadOnlyList<ReportDataSourceDto>> ListSourcesAsync(CancellationToken cancellationToken = default);

    Task<ReportResultDto> RunAsync(ReportDefinition definition, CancellationToken cancellationToken = default);

    Task<ReportFileDto> ExportAsync(ReportDefinition definition, ReportFileFormat format, CancellationToken cancellationToken = default);
}

// ---- Saved reports and schedules ----

public enum ReportVisibility { Private, Shared }

public enum ReportFrequency { Daily, Weekly, Monthly }

public enum ReportDeliveryMode
{
    /// <summary>The schedule's owner gets the file attached.</summary>
    Attachment,

    /// <summary>Everyone gets a link; the report runs with each reader's permissions when opened.</summary>
    Link,
}

public sealed record ReportScheduleDto(
    Guid Id,
    Guid SavedReportId,
    string OwnerEmail,
    bool IsMine,
    ReportFrequency Frequency,
    DayOfWeek? DayOfWeek,
    int? DayOfMonth,
    int HourUtc,
    ReportDeliveryMode Delivery,
    ReportFileFormat Format,
    IReadOnlyList<string> AdditionalRecipients,
    bool Enabled,
    DateTime NextRunUtc,
    DateTime? LastRunUtc,
    string? LastStatus);

/// <summary>
/// A saved report. <see cref="SharedWith"/> lists the organization roles it's shared with (<see cref="OrgRoles.Employee"/> =
/// everyone). Exactly one of <see cref="Parameters"/> (with <see cref="StandardKey"/>) and <see cref="Definition"/> is set.
/// </summary>
public sealed record SavedReportDto(
    Guid Id,
    string Name,
    string? Description,
    string OwnerEmail,
    bool IsMine,
    bool CanEdit,
    ReportVisibility Visibility,
    IReadOnlyList<OrgRoles> SharedWith,
    string? StandardKey,
    ReportParameters? Parameters,
    ReportDefinition? Definition,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ReportScheduleDto> Schedules);

public sealed record SaveReportRequest(
    string Name,
    string? Description,
    ReportVisibility Visibility,
    IReadOnlyList<OrgRoles>? SharedWith,
    string? StandardKey,
    ReportParameters? Parameters,
    ReportDefinition? Definition);

/// <summary><see cref="HourUtc"/> 0–23; weekly needs <see cref="DayOfWeek"/>, monthly <see cref="DayOfMonth"/> 1–28.</summary>
public sealed record SaveReportScheduleRequest(
    ReportFrequency Frequency,
    DayOfWeek? DayOfWeek,
    int? DayOfMonth,
    int HourUtc,
    ReportDeliveryMode Delivery,
    ReportFileFormat Format,
    IReadOnlyList<string>? AdditionalRecipients = null,
    bool Enabled = true);

public interface ISavedReportService
{
    /// <summary>The user's own reports and those shared with one of their roles (administrators see all), by name.</summary>
    Task<IReadOnlyList<SavedReportDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<SavedReportDto> GetAsync(Guid reportId, CancellationToken cancellationToken = default);

    Task<SavedReportDto> CreateAsync(SaveReportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Only the owner (or an administrator) changes a report.</summary>
    Task<SavedReportDto> UpdateAsync(Guid reportId, SaveReportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes the report and its schedules. Owner or administrator.</summary>
    Task DeleteAsync(Guid reportId, CancellationToken cancellationToken = default);

    /// <summary>Runs the report with the caller's permissions.</summary>
    Task<ReportResultDto> RunAsync(Guid reportId, CancellationToken cancellationToken = default);

    Task<ReportFileDto> ExportAsync(Guid reportId, ReportFileFormat format, CancellationToken cancellationToken = default);

    /// <summary>Schedules the report for the caller (who it will run as). Anyone who can see the report may schedule it.</summary>
    Task<ReportScheduleDto> AddScheduleAsync(Guid reportId, SaveReportScheduleRequest request, CancellationToken cancellationToken = default);

    /// <summary>The schedule's owner (or an administrator) changes it.</summary>
    Task<ReportScheduleDto> UpdateScheduleAsync(Guid scheduleId, SaveReportScheduleRequest request, CancellationToken cancellationToken = default);

    Task DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default);

    /// <summary>Sends the scheduled email now (as the caller, who must own the schedule) without moving the next run.</summary>
    Task<ReportScheduleDto> SendNowAsync(Guid scheduleId, CancellationToken cancellationToken = default);
}
