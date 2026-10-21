namespace Workbench.Domain.Customization;

/// <summary>
/// A form layout for one entity type, set for every company or one (<see cref="CompanyId"/>) and everyone or one role
/// (<see cref="RoleKey"/>). <see cref="ScopeKey"/> combines the two so the pair is unique even when either is null.
/// </summary>
public sealed class FormLayout : Entity
{
    public required string EntityType { get; set; }
    public Guid? CompanyId { get; set; }
    public string? RoleKey { get; set; }
    public required string ScopeKey { get; set; }

    /// <summary>The sections and their fields (<c>FormSectionLayout</c> records as JSON).</summary>
    public string SectionsJson { get; set; } = "[]";

    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public static string ScopeKeyFor(Guid? companyId, string? roleKey) => $"{companyId?.ToString("N") ?? "*"}|{roleKey ?? "*"}";
}

public enum ListViewVisibility
{
    Personal,
    Role,
    Everyone,
}

/// <summary>A saved view of a list page: columns, sorts, filters and grouping (<c>ListViewDefinition</c> as JSON).</summary>
public sealed class SavedListView : Entity
{
    public required string ListKey { get; set; }
    public required string Name { get; set; }
    public required string OwnerEmail { get; set; }
    public ListViewVisibility Visibility { get; set; }
    public string? RoleKey { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A person's default view of a list (null view: the standard view).</summary>
public sealed class ListViewPreference : Entity
{
    public required string UserEmail { get; set; }
    public required string ListKey { get; set; }
    public Guid? ViewId { get; set; }
}

/// <summary>Links a role to the dashboard that is its home page.</summary>
public sealed class RoleHomePage : Entity
{
    public required string RoleKey { get; set; }
    public Guid DashboardId { get; set; }
}

/// <summary>
/// A person's changes to a home page: widget order and hidden widgets (JSON id lists). <see cref="DashboardId"/> is
/// <see cref="Guid.Empty"/> for the built-in home page.
/// </summary>
public sealed class HomePagePreference : Entity
{
    public required string UserEmail { get; set; }
    public Guid DashboardId { get; set; }
    public string OrderJson { get; set; } = "[]";
    public string HiddenJson { get; set; } = "[]";
    public DateTime UpdatedAtUtc { get; set; }
}
