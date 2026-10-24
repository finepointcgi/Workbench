using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Forms;
using Workbench.Contracts.Forms;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Sheets;

public static class SheetExtrasRegistration
{
    /// <summary>
    /// Wave E: linked rows and lookups, cross-sheet references and formula help, templates and copies, cross-sheet
    /// reports, and forms. The services check access themselves (as the current user), so they need no decorator.
    /// </summary>
    public static IServiceCollection AddWorkbenchSheetExtras(this IServiceCollection services)
    {
        services.AddScoped<SheetCalculator>();
        services.AddScoped<ISheetLinkService, SheetLinkService>();
        services.AddScoped<ISheetFormulaService, SheetFormulaService>();
        services.AddScoped<ISheetTemplateService, SheetTemplateService>();
        services.AddScoped<ISheetReportService, SheetReportService>();
        services.AddScoped<ISheetFormatRuleService, SheetFormatRuleService>(); // Conditional formatting; checks access itself.
        services.AddScoped<Contracts.Scheduling.IPlanFloatService, Scheduling.PlanFloatService>(); // Float columns; reads through the checked services.
        services.AddScoped<FormSubmissionGrant>();
        services.AddScoped<FormService>();
        services.AddScoped<IFormService>(sp => sp.GetRequiredService<FormService>());
        services.AddScoped<IPublicFormService>(sp => sp.GetRequiredService<FormService>());
        return services;
    }
}
