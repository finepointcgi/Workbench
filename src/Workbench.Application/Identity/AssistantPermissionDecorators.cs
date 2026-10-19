using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Assistant;

namespace Workbench.Application.Identity;

public static class AssistantPermissionRegistration
{
    /// <summary>
    /// Assistant: anyone signed in may ask (each tool then checks the user's own access); settings, usage and the audit
    /// log are Admin; narratives Finance, Admin or the project's PM; timesheet flags filter to the people the user reviews.
    /// </summary>
    public static IServiceCollection AddWorkbenchAssistantPermissions(this IServiceCollection services)
    {
        services.Decorate<IAssistantService, PermissionCheckingAssistantService>();
        services.Decorate<IAssistantAdminService, PermissionCheckingAssistantAdminService>();
        services.Decorate<IProjectNarrativeService, PermissionCheckingProjectNarrativeService>();
        services.Decorate<ITimesheetFlagService, PermissionCheckingTimesheetFlagService>();
        services.Decorate<ITimesheetSubmissionService, PermissionCheckingTimesheetSubmissionService>();
        return services;
    }
}

internal sealed class PermissionCheckingAssistantService(IAssistantService inner, AccessControl access) : IAssistantService
{
    public Task<AssistantStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetStatusAsync(cancellationToken);
    }

    public IAsyncEnumerable<AssistantEvent> AskAsync(AskAssistantRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.AskAsync(request, cancellationToken);
    }
}

internal sealed class PermissionCheckingAssistantAdminService(IAssistantAdminService inner, AccessControl access) : IAssistantAdminService
{
    public async Task<AssistantSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<AssistantSettingsDto> SaveSettingsAsync(SaveAssistantSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return await inner.SaveSettingsAsync(request, cancellationToken);
    }

    public async Task<AssistantUsageDto> GetUsageAsync(int? year = null, int? month = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return await inner.GetUsageAsync(year, month, cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantInteractionDto>> ListAuditAsync(AssistantAuditQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireAdminAsync(cancellationToken);
        return await inner.ListAuditAsync(query, cancellationToken);
    }
}

internal sealed class PermissionCheckingProjectNarrativeService(IProjectNarrativeService inner, ProjectAccess projects) : IProjectNarrativeService
{
    public async Task<IReadOnlyList<NarrativePeriodDto>> ListPeriodsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "read the project's narratives", cancellationToken);
        return await inner.ListPeriodsAsync(projectId, cancellationToken);
    }

    public async Task<ProjectNarrativeDto?> GetAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "read the project's narratives", cancellationToken);
        return await inner.GetAsync(projectId, fiscalPeriodId, cancellationToken);
    }

    public async Task<ProjectNarrativeDto> DraftAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "draft the project's narrative", cancellationToken);
        return await inner.DraftAsync(projectId, fiscalPeriodId, cancellationToken);
    }

    public async Task<ProjectNarrativeDto> SaveAsync(Guid projectId, SaveProjectNarrativeRequest request, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "save the project's narrative", cancellationToken);
        return await inner.SaveAsync(projectId, request, cancellationToken);
    }
}

/// <summary>The service itself limits every read to the people the user reviews (and never their own timesheet).</summary>
internal sealed class PermissionCheckingTimesheetFlagService(ITimesheetFlagService inner, AccessControl access) : ITimesheetFlagService
{
    public Task<IReadOnlyList<TimesheetFlagsDto>> ListForTimesheetsAsync(IReadOnlyCollection<Guid> timesheetIds, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListForTimesheetsAsync(timesheetIds, cancellationToken);
    }

    public Task<TimesheetFlagsDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetAsync(timesheetId, cancellationToken);
    }

    public Task<IReadOnlyList<TimesheetFlagsDto>> ReportAsync(TimesheetFlagReportQuery query, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ReportAsync(query, cancellationToken);
    }

    public Task<TimesheetFlagExplanationDto> ExplainAsync(Guid timesheetId, string ruleKey, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ExplainAsync(timesheetId, ruleKey, cancellationToken);
    }
}

internal sealed class PermissionCheckingTimesheetSubmissionService(ITimesheetSubmissionService inner, AccessControl access) : ITimesheetSubmissionService
{
    public Task<IReadOnlyList<TimesheetSubmissionRowDto>> GetStatusAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetStatusAsync(date, cancellationToken);
    }
}
