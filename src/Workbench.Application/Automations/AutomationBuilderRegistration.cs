using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Approvals;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Customization;

namespace Workbench.Application.Automations;

public static class AutomationBuilderRegistration
{
    /// <summary>
    /// Admin › Automation (cross-module automations): the capture hook <see cref="ProjectAccounting.AuditRecorder"/> calls, the
    /// engine, the admin and task services, the record approval handler and the built-in field catalog (TryAdd, so the
    /// custom-fields catalog wins when it's registered). The permission decorator is added by AddWorkbenchIdentity.
    /// </summary>
    public static IServiceCollection AddWorkbenchAutomationBuilder(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AutomationIndex>();
        services.AddSingleton<AutomationSignal>();
        services.AddScoped<AutomationCapture>();
        services.AddScoped<AutomationGuard>();
        services.AddScoped<AutomationExecutor>();
        services.AddScoped<AutomationProcessor>();
        services.AddScoped<AutomationScheduler>();
        services.TryAddScoped<IAutomationPermissions, AdminAutomationPermissions>();
        services.TryAddScoped<IEntityFieldCatalog, BuiltInEntityFieldCatalog>();
        services.AddScoped<AutomationService>(); // Also behind the sheet rules API (RuleService), which checks sheet roles itself.
        services.AddScoped<IAutomationService>(sp => sp.GetRequiredService<AutomationService>());
        services.AddScoped<IAutomationTaskService, AutomationTaskService>();
        services.AddScoped<IMyTodoProvider, AutomationTaskTodoProvider>();
        services.AddScoped<IApprovalDocumentHandler, RecordApprovalHandler>();
        return services;
    }

    /// <summary>For the server host: runs queued automations, schedules and thresholds for every tenant.</summary>
    public static IServiceCollection AddWorkbenchAutomationBuilderHosting(this IServiceCollection services)
    {
        services.AddHostedService<AutomationBackgroundService>();
        return services;
    }
}
