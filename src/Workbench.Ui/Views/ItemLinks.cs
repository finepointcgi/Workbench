using Microsoft.AspNetCore.Components;

namespace Workbench.Ui.Views;

/// <summary>
/// Links that open an item's details panel where the sheet is shown: the current page with <c>?item={id}</c>. On a project's
/// Plan tab that keeps the person on the project page instead of moving them to <c>/sheets/{id}</c>.
/// </summary>
public static class ItemLinks
{
    public static string Details(NavigationManager navigation, Guid itemId) =>
        navigation.GetUriWithQueryParameter("item", itemId.ToString());
}
