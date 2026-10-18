namespace Workbench.Contracts.Time;

// Mirrors Workbench.Domain.ProjectAccounting.TimesheetCadence by name.
public enum TimesheetCadenceKind { Weekly, SemiMonthly, Biweekly, Monthly }

/// <summary>A timesheet period setting and when it applies. <see cref="EffectiveFrom"/> is null for the setting in force from the start.</summary>
public sealed record TimesheetPeriodSettingDto(
    TimesheetCadenceKind Cadence,
    DayOfWeek WeekStartDay,
    DateOnly? BiweeklyAnchor,
    DateOnly? EffectiveFrom,
    string? ChangedBy,
    DateTime? ChangedAtUtc,
    string? Reason,
    string Description);

/// <param name="Current">The setting today.</param>
/// <param name="Pending">A saved change that hasn't taken effect yet (it starts at the next period boundary).</param>
/// <param name="CurrentPeriodStart">Today's timesheet period.</param>
/// <param name="NextBoundary">Where a change saved now takes effect: the day after today's period ends.</param>
/// <param name="History">Every setting that took effect or is pending, newest first.</param>
/// <param name="CanEdit">Only Admins change organization settings.</param>
public sealed record OrganizationSettingsDto(
    TimesheetPeriodSettingDto Current,
    TimesheetPeriodSettingDto? Pending,
    DateOnly CurrentPeriodStart,
    DateOnly CurrentPeriodEnd,
    DateOnly NextBoundary,
    IReadOnlyList<TimesheetPeriodSettingDto> History,
    bool CanEdit);

/// <param name="WeekStartDay">Weekly: the first day of each period (default Monday).</param>
/// <param name="BiweeklyAnchor">Biweekly: the first day of any period; periods repeat every 14 days from it.</param>
/// <param name="Reason">Why it changes. Required: the change is audited.</param>
public sealed record ChangeTimesheetCadenceRequest(TimesheetCadenceKind Cadence, DayOfWeek? WeekStartDay, DateOnly? BiweeklyAnchor, string Reason);

/// <summary>What a change would do: the day it takes effect and the first periods under it (the first may be short).</summary>
public sealed record TimesheetCadencePreviewDto(DateOnly EffectiveFrom, IReadOnlyList<TimesheetPeriodRangeDto> FirstPeriods, string Description);

public sealed record TimesheetPeriodRangeDto(DateOnly Start, DateOnly End);

/// <summary>
/// Organization settings kept in the tenant database (<c>/setup/organization</c>). Reading needs Finance or Admin;
/// changing needs Admin. The timesheet cadence changes from the next period boundary, never mid-period.
/// </summary>
public interface IOrganizationSettingsService
{
    Task<OrganizationSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    Task<TimesheetCadencePreviewDto> PreviewTimesheetCadenceAsync(ChangeTimesheetCadenceRequest request, CancellationToken cancellationToken = default);

    Task<OrganizationSettingsDto> ChangeTimesheetCadenceAsync(ChangeTimesheetCadenceRequest request, CancellationToken cancellationToken = default);
}
