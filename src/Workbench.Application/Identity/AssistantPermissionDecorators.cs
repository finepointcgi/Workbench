using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Identity;

namespace Workbench.Application.Identity;

public static class AssistantPermissionRegistration
{
    /// <summary>
    /// Assistant: anyone signed in may ask (each tool then checks the user's own access); settings, usage and the audit
    /// log are Admin; timesheet flags filter to the people the user reviews.
    /// </summary>
    public static IServiceCollection AddWorkbenchAssistantPermissions(this IServiceCollection services)
    {
        services.Decorate<IAssistantService, PermissionCheckingAssistantService>();
        services.Decorate<IAssistantAdminService, PermissionCheckingAssistantAdminService>();
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
        await access.RequireAsync(Permission.AdminAssistant, "administer the assistant", cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<AssistantSettingsDto> SaveSettingsAsync(SaveAssistantSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminAssistant, "administer the assistant", cancellationToken);
        return await inner.SaveSettingsAsync(request, cancellationToken);
    }

    public async Task<AssistantUsageDto> GetUsageAsync(int? year = null, int? month = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminAssistant, "administer the assistant", cancellationToken);
        return await inner.GetUsageAsync(year, month, cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantInteractionDto>> ListAuditAsync(AssistantAuditQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminAssistant, "administer the assistant", cancellationToken);
        return await inner.ListAuditAsync(query, cancellationToken);
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
