using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Ui.Gantt;
using Workbench.Ui.Grid;
using Workbench.Ui.Views;

namespace Workbench.Tests.Accessibility;

/// <summary>
/// Keyboard ways to do what the mouse does by dragging (WCAG 2.1.1): Gantt dependencies from the item details panel and
/// grid column widths from the column menu. Also input purposes (1.3.5).
/// </summary>
public sealed class KeyboardPathTests
{
    private static readonly Guid SheetId = Guid.NewGuid();
    private static readonly ItemDto Design = Item("Design");
    private static readonly ItemDto Build = Item("Build");
    private static readonly ItemDto Test = Item("Test");

    [Fact]
    public async Task Depends_on_lists_removes_and_adds_links()
    {
        var dependencies = new FakeDependencyService();
        dependencies.Links.Add(new DependencyDto(Guid.NewGuid(), Design.Id, Build.Id, DependencyKind.FinishToStart, 0));
        await using var ctx = CreateContext(dependencies, out var popovers);

        var cut = ctx.Render<ItemDependencies>(p => p.Add(c => c.SheetId, SheetId).Add(c => c.Item, Build).Add(c => c.Items, [Design, Build, Test]));

        cut.WaitForAssertion(() => Assert.Contains("Design", cut.Find("[data-testid='depends-on'] ul").TextContent));
        A11yAssert.Check(cut);

        await cut.Find("button[aria-label='Remove dependency on Design']").ClickAsync(new());
        Assert.Empty(dependencies.Links);
        cut.WaitForAssertion(() => Assert.Contains("Nothing.", cut.Markup));

        // Choose "Test" in the select (keyboard users open it with Enter or Alt+Down), then Add.
        cut.Find(".mud-select .mud-input-control").MouseDown();
        cut.WaitForAssertion(() => Assert.NotEmpty(popovers.FindComponents<MudSelectItem<Guid?>>()));
        popovers.FindComponents<MudSelectItem<Guid?>>().Single(i => i.Instance.Value == Test.Id).Find("[role=option]").Click();
        await cut.Find("[data-testid='add-dependency-button']").ClickAsync(new());

        var added = Assert.Single(dependencies.Links);
        Assert.Equal((Test.Id, Build.Id), (added.PredecessorId, added.SuccessorId));
        A11yAssert.Check(cut);
    }

    [Theory]
    [InlineData(WorkspaceRole.Viewer)]
    [InlineData(WorkspaceRole.Commenter)]
    public async Task Depends_on_is_read_only_without_edit_access(WorkspaceRole role)
    {
        var dependencies = new FakeDependencyService();
        dependencies.Links.Add(new DependencyDto(Guid.NewGuid(), Design.Id, Build.Id, DependencyKind.StartToStart, 2));
        await using var ctx = CreateContext(dependencies);

        var cut = ctx.Render<ItemDependencies>(p => p
            .Add(c => c.SheetId, SheetId).Add(c => c.Item, Build).Add(c => c.Items, [Design, Build, Test])
            .AddCascadingValue(new SheetAccess(role)));

        cut.WaitForAssertion(() => Assert.Contains("Design (start to start) +2 working days", cut.Find("li").TextContent));
        Assert.Empty(cut.FindAll("button"));
        Assert.Empty(cut.FindAll("[data-testid='add-dependency']"));
    }

    [Fact]
    public async Task Column_width_from_the_column_menu_resizes_the_column()
    {
        await using var ctx = CreateContext(new FakeDependencyService(), out var popovers);
        var dialogs = ctx.Render<MudDialogProvider>();
        var hours = new ColumnDto(Guid.NewGuid(), "Planned hours", ColumnKind.Number, 0, [], null);
        var sheet = new SheetDetail(new SheetSummary(SheetId, "Plan", null, "Workspace", 1, false, null, DateTime.UtcNow), [hours]);

        var grid = ctx.Render<SheetGridView>(p => p.Add(v => v.Sheet, sheet).Add(v => v.Items, [Design]));
        grid.WaitForElement("button[aria-label='Planned hours column actions']").Click();
        popovers.FindComponents<MudMenuItem>().Single(m => m.Markup.Contains("Column width")).Find("[role=menuitem]").Click();

        dialogs.WaitForAssertion(() => Assert.NotNull(dialogs.Find("[data-testid='column-width'] input, input[data-testid='column-width']")));
        A11yAssert.Check(dialogs);
        dialogs.Find("[data-testid='column-width'] input, input[data-testid='column-width']").Input("240");
        await dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Apply").ClickAsync(new());

        grid.WaitForAssertion(() => Assert.Equal("width:240px", grid.Find($"col[data-key='{hours.Id}']").GetAttribute("style")));
    }

    [Fact]
    public async Task Column_width_dialog_applies_on_enter_and_keeps_widths_in_range()
    {
        await using var ctx = CreateContext(new FakeDependencyService());
        var provider = ctx.Render<MudDialogProvider>();
        var service = ctx.Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<ColumnWidthDialog> { { d => d.Width, 140 }, { d => d.DefaultWidth, 140 } };
        var reference = await provider.InvokeAsync(() => service.ShowAsync<ColumnWidthDialog>("Budget column width", parameters));

        var input = provider.Find("[data-testid='column-width'] input, input[data-testid='column-width']");
        input.Input("10");
        input.KeyDown("Enter");

        Assert.Equal(ColumnWidthDialog.MinWidth, (await reference.Result)!.Data);
    }

    [Fact]
    public async Task Certifying_officials_name_declares_its_purpose()
    {
        await using var ctx = CreateContext(new FakeDependencyService());
        var provider = ctx.Render<MudDialogProvider>();
        var service = ctx.Services.GetRequiredService<IDialogService>();
        await provider.InvokeAsync(() => service.ShowAsync<Workbench.Ui.IncurredCost.CertifyDialog>("Certify"));

        Assert.Equal("name", provider.Find("input[autocomplete='name']").GetAttribute("autocomplete"));
    }

    private static BunitContext CreateContext(FakeDependencyService dependencies) => CreateContext(dependencies, out _);

    private static BunitContext CreateContext(FakeDependencyService dependencies, out IRenderedComponent<MudPopoverProvider> popovers)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<IDependencyService>(dependencies);
        ctx.Services.AddSingleton<IItemService>(new NoItemService());
        ctx.Services.AddSingleton<IColumnService>(new NoColumnService());
        popovers = ctx.Render<MudPopoverProvider>();
        return ctx;
    }

    private static ItemDto Item(string title) => new(Guid.NewGuid(), null, 0, title, null, null, null, new Dictionary<string, string?>());

    private sealed class FakeDependencyService : IDependencyService
    {
        public List<DependencyDto> Links { get; } = [];

        public Task<IReadOnlyList<DependencyDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DependencyDto>>(Links.ToList());

        public Task<DependencyDto> AddAsync(Guid sheetId, CreateDependencyRequest request, CancellationToken cancellationToken = default)
        {
            var link = new DependencyDto(Guid.NewGuid(), request.PredecessorId, request.SuccessorId, request.Type, request.LagDays);
            Links.Add(link);
            return Task.FromResult(link);
        }

        public Task RemoveAsync(Guid sheetId, Guid dependencyId, CancellationToken cancellationToken = default)
        {
            Links.RemoveAll(l => l.Id == dependencyId);
            return Task.CompletedTask;
        }

        public Task<ScheduleAnalysis> AnalyzeAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScheduleAnalysis(null, null, [], []));
    }

    private sealed class NoItemService : IItemService
    {
        public Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoColumnService : IColumnService
    {
        public Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
