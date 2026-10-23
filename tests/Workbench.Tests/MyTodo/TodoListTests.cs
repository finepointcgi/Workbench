using Bunit;
using MudBlazor.Services;
using Workbench.Contracts.MyTodo;
using Workbench.Ui.Home;

namespace Workbench.Tests.MyTodo;

/// <summary>The home to-do list stays short: overdue and today in full, later groups their first few until "Show more".</summary>
public sealed class TodoListTests
{
    private static MyTodoItem Item(string key, TodoUrgency urgency) =>
        new(key, "task", $"Task {key}", "Because", urgency, null, "tasks", "Open");

    [Fact]
    public async Task Later_groups_show_three_until_show_more_and_urgent_groups_show_everything()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var items = Enumerable.Range(1, 5).Select(i => Item($"o{i}", TodoUrgency.Overdue))
            .Concat(Enumerable.Range(1, 6).Select(i => Item($"w{i}", TodoUrgency.ThisWeek)))
            .Concat(Enumerable.Range(1, 4).Select(i => Item($"c{i}", TodoUrgency.Later)))
            .ToList();

        var list = ctx.Render<TodoList>(p => p.Add(l => l.Items, items));

        Assert.Equal(5, list.FindAll("[data-todo^='o']").Count);
        Assert.Equal(3, list.FindAll("[data-todo^='w']").Count);
        Assert.Equal(4, list.FindAll("[data-todo^='c']").Count); // one extra isn't worth a button
        var more = Assert.Single(list.FindAll("[data-testid='todo-more']"));
        Assert.Contains("Show 3 more", more.TextContent);

        more.Click();
        Assert.Equal(6, list.FindAll("[data-todo^='w']").Count);
        Assert.Empty(list.FindAll("[data-testid='todo-more']"));
    }
}
