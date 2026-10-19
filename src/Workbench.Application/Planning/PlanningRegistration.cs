using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Contracts.Planning;

namespace Workbench.Application.Planning;

public static class PlanningRegistration
{
    /// <summary>
    /// Recurring journals, corporate GL budgets, budget vs actual and planning cycles (with the "Org unit budget" inbox
    /// handler). Recurring journals post through the general ledger, so this goes after AddWorkbenchAccounting; above
    /// AddWorkbenchIdentity, whose <c>AddWorkbenchPlanningPermissions</c> wraps these services.
    /// </summary>
    public static IServiceCollection AddWorkbenchPlanning(this IServiceCollection services)
    {
        services.AddScoped<RecurringJournalService>();
        services.AddScoped<IRecurringJournalService>(sp => sp.GetRequiredService<RecurringJournalService>());
        services.AddScoped<IGlBudgetService, GlBudgetService>();
        services.AddScoped<IBudgetReportService, BudgetReportService>();
        services.AddScoped<IPlanningCycleService, PlanningCycleService>();
        services.AddScoped<IApprovalDocumentHandler, BudgetSubmissionApprovalHandler>();
        return services;
    }

    /// <summary>For the server host: generates due recurring journal entries every six hours when the organization turned it on.</summary>
    public static IServiceCollection AddWorkbenchPlanningHosting(this IServiceCollection services)
    {
        services.AddHostedService<RecurringJournalBackgroundService>();
        return services;
    }
}
