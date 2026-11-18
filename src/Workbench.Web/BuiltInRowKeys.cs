using Workbench.Application.Automations;
using Workbench.Application.Customization;
using Workbench.Application.Reports;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Modules;
using Workbench.Contracts.Security;
using Workbench.Ui.Customization;
using Workbench.Ui.Navigation;

namespace Workbench.Web;

/// <summary>
/// The key of every catalog row the product itself has, as <c>catalog/key</c>. The module host checks a module's rows against this list
/// (<c>ModuleHostOptions.BuiltInRowKeys</c>), so a module can't add a custom list, form, numbered document, automation entity, event, approval
/// type, security event, "+ New" item, report source or status set that already exists.
/// </summary>
public static class BuiltInRowKeys
{
    public static IReadOnlyList<string> All { get; } =
    [
        .. CustomizableLists.BuiltIn.Select(k => ModuleSet.RowKey(ModuleCatalogs.CustomLists, k)),
        .. ListSources.BuiltIn.Select(s => ModuleSet.RowKey(s)),
        .. BuiltInForms.BuiltIn.Select(f => ModuleSet.RowKey(f)),
        .. DocumentNumberTypes.BuiltIn.Select(t => ModuleSet.RowKey(t)),
        .. AutomationEntities.BuiltIn.Select(e => ModuleSet.RowKey(e)),
        .. WebhookEventCatalog.BuiltIn.Select(e => ModuleSet.RowKey(e)),
        .. ApprovalDocumentTypes.BuiltIn.Select(k => ModuleSet.RowKey(ModuleCatalogs.ApprovalDocumentTypes, k)),
        .. SecurityEventTypes.BuiltIn.Select(k => ModuleSet.RowKey(ModuleCatalogs.SecurityEvents, k)),
        .. NewCatalog.BuiltIn.Select(a => ModuleSet.RowKey(a)),
        .. ReportSource.BuiltIn.Select(s => ModuleSet.RowKey(s)),
        .. StatusSetCatalog.BuiltIn.Select(s => ModuleSet.RowKey(s)),
    ];
}
