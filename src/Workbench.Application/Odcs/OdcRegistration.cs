using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Files;
using Workbench.Contracts.Odcs;

namespace Workbench.Application.Odcs;

public static class OdcRegistration
{
    /// <summary>
    /// Registers vendor ODCs, expense reports and approvals. Binds <see cref="ExpenseOptions"/> from config <c>Expenses</c>.
    /// Notifications go out through <see cref="PublishingOdcNotifier"/> (registered by <c>AddWorkbenchAutomation</c>'s publisher).
    /// </summary>
    public static IServiceCollection AddWorkbenchOdcs(this IServiceCollection services)
    {
        services.AddOptions<ExpenseOptions>().BindConfiguration(ExpenseOptions.Section);
        services.TryAddScoped<FileStorage>();
        services.AddScoped<IOdcNotifier, PublishingOdcNotifier>();
        services.AddScoped<OdcWorkflow>();
        services.AddScoped<IOdcService, OdcService>();
        services.AddScoped<IExpenseReportService, ExpenseReportService>();
        services.AddScoped<IOdcApprovalService, OdcApprovalService>();
        return services;
    }
}
