using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Security;
using Workbench.Contracts.Time;

namespace Workbench.Application.Security;

// Record sensitive exports and period close in the security event log (A.8.15). Registered before the permission
// decorators, which wrap them, so only calls that were allowed (and succeeded) are recorded here; refusals are
// recorded as access.denied.

public sealed class SecurityLoggingTimeReportService(ITimeReportService inner, SecurityEventLog log) : ITimeReportService
{
    public Task<IReadOnlyList<HoursByChargeCodeRow>> GetHoursByChargeCodeAsync(DateOnly from, DateOnly to, bool approvedOnly = true, CancellationToken cancellationToken = default) =>
        inner.GetHoursByChargeCodeAsync(from, to, approvedOnly, cancellationToken);

    public Task<IReadOnlyList<DailyEntryComplianceRow>> GetDailyEntryComplianceAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        inner.GetDailyEntryComplianceAsync(from, to, cancellationToken);

    public async Task<string> ExportLaborDistributionCsvAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var csv = await inner.ExportLaborDistributionCsvAsync(from, to, cancellationToken);
        var rows = Math.Max(0, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1);
        await log.RecordAsync(SecurityEventTypes.ExportLaborCsv, $"labor-distribution {from:yyyy-MM-dd}..{to:yyyy-MM-dd}", detail: $"{rows} rows", cancellationToken: cancellationToken);
        return csv;
    }
}

public sealed class SecurityLoggingFinancialExportService(IFinancialExportService inner, SecurityEventLog log) : IFinancialExportService
{
    public async Task<FinancialExportFile> ExportWbsCsvAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default)
    {
        var file = await inner.ExportWbsCsvAsync(projectId, budgetVersionId, forecastVersionId, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportFinancialCsv, file.FileName, detail: $"WBS financials, project {projectId}", cancellationToken: cancellationToken);
        return file;
    }

    public async Task<FinancialExportFile> ExportTimePhasedCsvAsync(Guid projectId, Guid? budgetVersionId = null, CancellationToken cancellationToken = default)
    {
        var file = await inner.ExportTimePhasedCsvAsync(projectId, budgetVersionId, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportFinancialCsv, file.FileName, detail: $"Time-phased burn, project {projectId}", cancellationToken: cancellationToken);
        return file;
    }
}

public sealed class SecurityLoggingFiscalCalendarService(IFiscalCalendarService inner, SecurityEventLog log) : IFiscalCalendarService
{
    public Task<IReadOnlyList<FiscalPeriodDto>> ListPeriodsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default) =>
        inner.ListPeriodsAsync(fiscalYear, cancellationToken);

    public Task<FiscalPeriodDto?> GetPeriodForDateAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        inner.GetPeriodForDateAsync(date, cancellationToken);

    public Task<IReadOnlyList<FiscalPeriodDto>> CreateFiscalYearAsync(int fiscalYear, DateOnly start, CancellationToken cancellationToken = default) =>
        inner.CreateFiscalYearAsync(fiscalYear, start, cancellationToken);

    public async Task<FiscalPeriodDto> SetPeriodStatusAsync(Guid periodId, FiscalPeriodState status, string reason, CancellationToken cancellationToken = default)
    {
        var period = await inner.SetPeriodStatusAsync(periodId, status, reason, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.PeriodStatusChanged, $"FY{period.FiscalYear} {period.Name}", detail: $"{(status == FiscalPeriodState.Closed ? "Closed" : "Reopened")}: {reason}", cancellationToken: cancellationToken);
        return period;
    }
}
