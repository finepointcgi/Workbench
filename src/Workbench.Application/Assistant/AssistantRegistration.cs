using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Assistant;

namespace Workbench.Application.Assistant;

public static class AssistantRegistration
{
    /// <summary>
    /// The AI assistant (off until an administrator turns it on), PM narratives and timesheet flags. Only the fake
    /// provider is available from here; <c>AddWorkbenchInfrastructure</c> replaces the client factory with one that also
    /// reaches the Anthropic API and Amazon Bedrock.
    /// </summary>
    public static IServiceCollection AddWorkbenchAssistant(this IServiceCollection services)
    {
        services.AddOptions<AssistantOptions>().BindConfiguration(AssistantOptions.Section);
        services.TryAddSingleton<IAssistantModelClientFactory, FakeOnlyModelClientFactory>();
        services.AddScoped<AssistantGate>();
        services.AddScoped<AssistantRunner>();
        services.AddScoped<AssistantToolbox>();
        services.AddScoped<IAssistantService, AssistantService>();
        services.AddScoped<AssistantWriteTools>();
        services.AddScoped<IAssistantToolHost, AssistantToolHost>(); // The same tools for outside AI clients over MCP (/api/v1/mcp).
        services.AddScoped<IAssistantAdminService, AssistantAdminService>();
        services.AddScoped<IProjectNarrativeService, ProjectNarrativeService>();
        services.AddScoped<ITimesheetFlagService, TimesheetFlagService>();
        services.AddScoped<ITimesheetSubmissionService, TimesheetSubmissionService>();
        return services;
    }
}
