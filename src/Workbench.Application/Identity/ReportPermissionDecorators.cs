using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Security;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Security;

namespace Workbench.Application.Identity;

// Reports: every call needs a signed-in user. Row and field rules (cost rates, other people's time, contract values)
// are applied inside the reports themselves, which read through the permission-checked services or ReportAccess, so a
// report never shows more than the caller could see on the underlying pages. Exports are recorded as security events.

public static class ReportPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchReportPermissions(this IServiceCollection services)
    {
        services.Decorate<IStandardReportService, PermissionCheckingStandardReportService>();
        services.Decorate<IReportWriterService, PermissionCheckingReportWriterService>();
        services.Decorate<ISavedReportService, PermissionCheckingSavedReportService>();
        return services;
    }
}

public sealed class PermissionCheckingStandardReportService(IStandardReportService inner, AccessControl access, SecurityEventLog log) : IStandardReportService
{
    public Task<IReadOnlyList<StandardReportDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListAsync(cancellationToken);
    }

    public Task<ReportResultDto> RunAsync(string reportKey, ReportParameters parameters, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.RunAsync(reportKey, parameters, cancellationToken);
    }

    public Task<ReportResultDto> DrillAsync(string reportKey, ReportParameters parameters, string drillKey, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.DrillAsync(reportKey, parameters, drillKey, cancellationToken);
    }

    public async Task<ReportFileDto> ExportAsync(string reportKey, ReportParameters parameters, ReportFileFormat format, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var file = await inner.ExportAsync(reportKey, parameters, format, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportReport, $"standard:{reportKey}", detail: format.ToString(), cancellationToken: cancellationToken);
        return file;
    }
}

public sealed class PermissionCheckingReportWriterService(IReportWriterService inner, AccessControl access, SecurityEventLog log) : IReportWriterService
{
    public Task<IReadOnlyList<ReportDataSourceDto>> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListSourcesAsync(cancellationToken);
    }

    public Task<ReportResultDto> RunAsync(ReportDefinition definition, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.RunAsync(definition, cancellationToken);
    }

    public async Task<ReportFileDto> ExportAsync(ReportDefinition definition, ReportFileFormat format, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var file = await inner.ExportAsync(definition, format, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportReport, $"writer:{definition.Source}", detail: format.ToString(), cancellationToken: cancellationToken);
        return file;
    }
}

public sealed class PermissionCheckingSavedReportService(ISavedReportService inner, AccessControl access, SecurityEventLog log) : ISavedReportService
{
    private T Signed<T>(Func<T> call)
    {
        access.RequireSignedIn();
        return call();
    }

    public Task<IReadOnlyList<SavedReportDto>> ListAsync(CancellationToken cancellationToken = default) => Signed(() => inner.ListAsync(cancellationToken));

    public Task<SavedReportDto> GetAsync(Guid reportId, CancellationToken cancellationToken = default) => Signed(() => inner.GetAsync(reportId, cancellationToken));

    public Task<SavedReportDto> CreateAsync(SaveReportRequest request, CancellationToken cancellationToken = default) => Signed(() => inner.CreateAsync(request, cancellationToken));

    public Task<SavedReportDto> UpdateAsync(Guid reportId, SaveReportRequest request, CancellationToken cancellationToken = default) => Signed(() => inner.UpdateAsync(reportId, request, cancellationToken));

    public Task DeleteAsync(Guid reportId, CancellationToken cancellationToken = default) => Signed(() => inner.DeleteAsync(reportId, cancellationToken));

    public Task<ReportResultDto> RunAsync(Guid reportId, CancellationToken cancellationToken = default) => Signed(() => inner.RunAsync(reportId, cancellationToken));

    public async Task<ReportFileDto> ExportAsync(Guid reportId, ReportFileFormat format, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var file = await inner.ExportAsync(reportId, format, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportReport, $"saved:{reportId}", detail: format.ToString(), cancellationToken: cancellationToken);
        return file;
    }

    public Task<ReportScheduleDto> AddScheduleAsync(Guid reportId, SaveReportScheduleRequest request, CancellationToken cancellationToken = default) => Signed(() => inner.AddScheduleAsync(reportId, request, cancellationToken));

    public Task<ReportScheduleDto> UpdateScheduleAsync(Guid scheduleId, SaveReportScheduleRequest request, CancellationToken cancellationToken = default) => Signed(() => inner.UpdateScheduleAsync(scheduleId, request, cancellationToken));

    public Task DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default) => Signed(() => inner.DeleteScheduleAsync(scheduleId, cancellationToken));

    public Task<ReportScheduleDto> SendNowAsync(Guid scheduleId, CancellationToken cancellationToken = default) => Signed(() => inner.SendNowAsync(scheduleId, cancellationToken));
}
