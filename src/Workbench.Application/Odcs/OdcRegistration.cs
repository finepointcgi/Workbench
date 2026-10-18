using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Files;
using Workbench.Contracts.Odcs;

namespace Workbench.Application.Odcs;

public static class OdcRegistration
{
    /// <summary>
    /// Registers vendor ODCs, expense reports and approvals. Binds <see cref="ExpenseOptions"/> from config <c>Expenses</c>.
    /// <see cref="IOdcNotifier"/> defaults to a no-op; register a real one (before or after this call) to replace it.
    /// </summary>
    public static IServiceCollection AddWorkbenchOdcs(this IServiceCollection services)
    {
        services.AddOptions<ExpenseOptions>().BindConfiguration(ExpenseOptions.Section);
        services.TryAddScoped<FileStorage>();
        services.TryAddScoped<IOdcNotifier, NullOdcNotifier>();
        services.AddScoped<OdcWorkflow>();
        services.AddScoped<IOdcService, OdcService>();
        services.AddScoped<IExpenseReportService, ExpenseReportService>();
        services.AddScoped<IOdcApprovalService, OdcApprovalService>();
        return services;
    }
}
