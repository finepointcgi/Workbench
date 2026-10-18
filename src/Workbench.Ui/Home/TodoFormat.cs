using System.Globalization;
using MudBlazor;
using Workbench.Contracts;
using Workbench.Contracts.MyTodo;

namespace Workbench.Ui.Home;

/// <summary>Labels, icons and colors for the home page's to-do list.</summary>
public static class TodoFormat
{
    public static string Greeting(int hour) => hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening",
    };

    public static string Heading(TodoUrgency urgency) => urgency switch
    {
        TodoUrgency.Overdue => "Overdue",
        TodoUrgency.Today => "Today",
        TodoUrgency.ThisWeek => "This week",
        _ => "Coming up",
    };

    public static Color Color(TodoUrgency urgency) => urgency switch
    {
        TodoUrgency.Overdue => MudBlazor.Color.Error,
        TodoUrgency.Today => MudBlazor.Color.Warning,
        TodoUrgency.ThisWeek => MudBlazor.Color.Info,
        _ => MudBlazor.Color.Default,
    };

    /// <summary>The CSS color for the urgency's left border.</summary>
    public static string Accent(TodoUrgency urgency) => urgency switch
    {
        TodoUrgency.Overdue => "var(--mud-palette-error)",
        TodoUrgency.Today => "var(--mud-palette-warning)",
        TodoUrgency.ThisWeek => "var(--mud-palette-info)",
        _ => "var(--mud-palette-lines-default)",
    };

    public static string Icon(string category) => category switch
    {
        TodoCategories.Timesheet => Icons.Material.Filled.Schedule,
        TodoCategories.Expense => Icons.Material.Filled.ReceiptLong,
        TodoCategories.Leave => Icons.Material.Filled.BeachAccess,
        TodoCategories.Approval => Icons.Material.Filled.FactCheck,
        TodoCategories.Receiving => Icons.Material.Filled.Inventory2,
        TodoCategories.Sheet => Icons.Material.Filled.TaskAlt,
        TodoCategories.Project => Icons.Material.Filled.Insights,
        TodoCategories.Finance => Icons.Material.Filled.AccountBalance,
        _ => Icons.Material.Filled.ChevronRight,
    };

    /// <summary>"Due today", "Due Fri Oct 23", "Was due Oct 9"; null when undated.</summary>
    public static string? Due(MyTodoItem item, DateOnly today) => item.Due switch
    {
        null => null,
        { } d when d == today => "Due today",
        { } d when d < today => $"Was due {d.ToString("MMM d", CultureInfo.InvariantCulture)}",
        { } d when d == today.AddDays(1) => "Due tomorrow",
        { } d => $"Due {d.ToString("ddd MMM d", CultureInfo.InvariantCulture)}",
    };

    public static string Hours(decimal hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Money(decimal? amount) => amount is { } a ? a.ToString("C", CultureInfo.GetCultureInfo("en-US")) : "";

    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException
            or TaskCanceledException or WorkbenchNotFoundException;
}
