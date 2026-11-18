namespace Workbench.Contracts.Modules;

/// <summary>
/// <b>A row a module adds to one of the product's catalogs</b> (a custom list, a built-in form, a document numbering type, an automation
/// entity, an integration event, an approval document type, a "+ New" action, a report source, a security event name, a home widget).
/// Each catalog has a <see cref="Catalog"/> name (<see cref="ModuleCatalogs"/>) and every row a key that is unique inside it.
/// A module lists its rows in <see cref="ModuleContribution.Rows"/>; the catalog reads the built-in rows and then the module's
/// (<see cref="RowCatalog{T}"/>). The row types are the ones the in-repo rows already use, so a moved module writes the same code.
/// </summary>
public interface IModuleRow
{
    /// <summary>The catalog this row belongs to (a <see cref="ModuleCatalogs"/> value).</summary>
    string Catalog { get; }

    /// <summary>The row's key, unique inside <see cref="Catalog"/> (the list key, entity type, document type, event key, and so on).</summary>
    string RowKey { get; }
}

/// <summary>The catalogs a module can add rows to.</summary>
public static class ModuleCatalogs
{
    /// <summary>Custom lists (<c>CustomizableLists</c>): <see cref="ModuleCustomList"/>.</summary>
    public const string CustomLists = "custom-list";

    /// <summary>Chartable list pages: <c>IListSource</c> (Workbench.Ui).</summary>
    public const string ListSources = "list-source";

    /// <summary>Built-in forms and, with them, their layouts: <c>BuiltInForms.FormDefinition</c>.</summary>
    public const string Forms = "form";

    /// <summary>Numbered documents: <c>DocumentNumberType</c>.</summary>
    public const string DocumentNumbers = "document-number";

    /// <summary>Records automations can watch: <c>AutomationEntity</c>.</summary>
    public const string AutomationEntities = "automation-entity";

    /// <summary>Events sent to webhooks and connectors: <c>IntegrationEventType</c>.</summary>
    public const string IntegrationEvents = "integration-event";

    /// <summary>Documents the approval engine routes: <see cref="ModuleApprovalDocumentType"/>.</summary>
    public const string ApprovalDocumentTypes = "approval-type";

    /// <summary>Event names in the security log: <see cref="ModuleSecurityEventType"/>.</summary>
    public const string SecurityEvents = "security-event";

    /// <summary>The "+ New" menu: <c>NewAction</c>.</summary>
    public const string NewActions = "new-action";

    /// <summary>Report writer sources: <c>ReportSource</c>.</summary>
    public const string ReportSources = "report-source";

    /// <summary>Home page and dashboard widgets: <see cref="ModuleHomeWidget"/>.</summary>
    public const string HomeWidgets = "home-widget";

    /// <summary>Status sets administrators may relabel (Setup › Lists): <c>StatusSetDefinition</c> (Workbench.Application).</summary>
    public const string StatusSets = "status-set";
}

/// <summary>A list page whose saved views a module owns (the key of a <c>ListViewDefinition</c>).</summary>
public sealed record ModuleCustomList(string Key) : IModuleRow
{
    public string Catalog => ModuleCatalogs.CustomLists;

    public string RowKey => Key;
}

/// <summary>
/// A document the module sends through the approval engine. The module also registers an <c>IApprovalDocumentHandler</c> for
/// <see cref="Key"/> in <c>ConfigureServices</c> (that is how the inbox approves and rejects it) and builds its own <c>ApprovalSubject</c>
/// when it submits one. <see cref="AmountIsHours"/> is true for documents whose amount is hours, not money. <see cref="DefaultWorkflow"/> is the
/// workflow the type starts with (administrators change it in Setup › Approval workflows); without one it starts with a single step for the administrators.
/// </summary>
public sealed record ModuleApprovalDocumentType(string Key, string Label, bool AmountIsHours = false, Approvals.ApprovalWorkflowDefinition? DefaultWorkflow = null) : IModuleRow
{
    public string Catalog => ModuleCatalogs.ApprovalDocumentTypes;

    public string RowKey => Key;
}

/// <summary>An event name the module writes to the security log (lower-case, dot-separated, area first). <see cref="Description"/> says when it is written.</summary>
public sealed record ModuleSecurityEventType(string Key, string Description) : IModuleRow
{
    public string Catalog => ModuleCatalogs.SecurityEvents;

    public string RowKey => Key;
}

/// <summary>
/// A home page and dashboard widget the module provides. The widget picker lists it, and the module's <c>IHomeWidgetProvider</c> (registered
/// in <c>ConfigureServices</c>) with the same <see cref="Key"/> computes what it shows, with the viewer's own permissions.
/// </summary>
public sealed record ModuleHomeWidget(string Key, string Title, string Description) : IModuleRow
{
    public string Catalog => ModuleCatalogs.HomeWidgets;

    public string RowKey => Key;
}

/// <summary>
/// A built-in catalog and the module rows that extend it: the built-in rows first, then the rows of the modules loaded (the host has
/// already refused a module whose row key clashes, see <see cref="ModuleSet.Validate"/>). The merged list is cached per module set,
/// so it is rebuilt only when the module set changes.
/// </summary>
public sealed class RowCatalog<T>(IReadOnlyList<T> builtIn) where T : class
{
    private sealed record Merged(ModuleSet Set, IReadOnlyList<T> Rows);

    private Merged? _merged;

    /// <summary>The rows the product ships.</summary>
    public IReadOnlyList<T> BuiltIn { get; } = builtIn;

    /// <summary>The built-in rows plus those of the loaded modules.</summary>
    public IReadOnlyList<T> All
    {
        get
        {
            var set = ModuleRegistry.Current;
            var merged = Volatile.Read(ref _merged);
            if (merged is not null && ReferenceEquals(merged.Set, set))
            {
                return merged.Rows;
            }

            var extra = set.RowsOf<T>();
            var rows = extra.Count == 0 ? BuiltIn : [.. BuiltIn, .. extra];
            Volatile.Write(ref _merged, new Merged(set, rows));
            return rows;
        }
    }
}
