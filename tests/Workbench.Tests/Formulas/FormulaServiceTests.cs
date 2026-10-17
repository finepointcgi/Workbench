using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Sheets;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Formulas;

public sealed class FormulaServiceTests
{
    private readonly IWorkbenchDbContextFactory _factory = TestDb.CreateFactory();
    private readonly ItemService _items;
    private readonly ColumnService _columns;
    private readonly SheetService _sheets;

    public FormulaServiceTests()
    {
        _items = new ItemService(_factory, TimeProvider.System);
        _columns = new ColumnService(_factory, TimeProvider.System);
        _sheets = new SheetService(_factory, TimeProvider.System);
    }

    private async Task<(Guid SheetId, ColumnDto Hours, ColumnDto Budget, ColumnDto Rate)> NewCostSheetAsync()
    {
        var sheetId = (await _sheets.CreateAsync(new CreateSheetRequest("Plan", null, true))).Id;
        var hours = await _columns.CreateAsync(sheetId, new CreateColumnRequest("Planned hours", ColumnKind.Number));
        var budget = await _columns.CreateAsync(sheetId, new CreateColumnRequest("Budget", ColumnKind.Currency));
        var rate = await _columns.CreateAsync(sheetId, new CreateColumnRequest("Cost per hour", ColumnKind.Currency, Formula: "[Budget] / [Planned hours]"));
        return (sheetId, hours, budget, rate);
    }

    private static Dictionary<string, string?> Values(params (ColumnDto Column, string? Value)[] values) =>
        values.ToDictionary(v => v.Column.Id.ToString(), v => v.Value);

    [Fact]
    public async Task List_includes_computed_values()
    {
        var (sheetId, hours, budget, rate) = await NewCostSheetAsync();
        var item = await _items.CreateAsync(sheetId, new CreateItemRequest("Kickoff", Values: Values((hours, "8"), (budget, "1200"))));

        var listed = Assert.Single(await _items.ListAsync(sheetId));

        Assert.Equal("150", listed.Values[rate.Id.ToString()]);
        Assert.Equal("8", listed.Values[hours.Id.ToString()]);
        Assert.Equal("150", item.Values[rate.Id.ToString()]);
    }

    [Fact]
    public async Task Create_and_update_never_store_formula_values()
    {
        var (sheetId, hours, budget, rate) = await NewCostSheetAsync();
        var item = await _items.CreateAsync(sheetId, new CreateItemRequest("Task", Values: Values((hours, "10"), (rate, "999"))));
        Assert.DoesNotContain(rate.Id.ToString(), await StoredValuesAsync(item.Id));

        var updated = await _items.UpdateAsync(sheetId, item.Id,
            new UpdateItemRequest("Task", null, null, null, Values((hours, "10"), (budget, "500"), (rate, "999"))));

        var stored = await StoredValuesAsync(item.Id);
        Assert.DoesNotContain(rate.Id.ToString(), stored);
        Assert.DoesNotContain(rate.Id.ToString().ToUpperInvariant(), stored);
        Assert.Equal("500", stored[budget.Id.ToString()]);
        Assert.Equal("50", updated.Values[rate.Id.ToString()]);
    }

    [Fact]
    public async Task Uppercase_formula_keys_are_stripped_too()
    {
        var (sheetId, hours, _, rate) = await NewCostSheetAsync();
        var item = await _items.CreateAsync(sheetId, new CreateItemRequest("Task",
            Values: new Dictionary<string, string?> { [hours.Id.ToString()] = "1", [rate.Id.ToString().ToUpperInvariant()] = "x" }));

        Assert.Equal([hours.Id.ToString()], (await StoredValuesAsync(item.Id)).Keys);
    }

    [Fact]
    public async Task Parent_rows_roll_up_children()
    {
        var (sheetId, hours, budget, _) = await NewCostSheetAsync();
        var total = await _columns.CreateAsync(sheetId, new CreateColumnRequest("Total budget", ColumnKind.Currency,
            Formula: "[Budget] + SUM(CHILDREN([Total budget]))"));
        var phase = await _items.CreateAsync(sheetId, new CreateItemRequest("Phase"));
        await _items.CreateAsync(sheetId, new CreateItemRequest("A", phase.Id, Values: Values((hours, "8"), (budget, "1200"))));
        var b = await _items.CreateAsync(sheetId, new CreateItemRequest("B", phase.Id, Values: Values((hours, "60"), (budget, "9000"))));
        await _items.CreateAsync(sheetId, new CreateItemRequest("B.1", b.Id, Values: Values((budget, "100"))));

        var listed = (await _items.ListAsync(sheetId)).ToDictionary(i => i.Title);

        Assert.Equal("10300", listed["Phase"].Values[total.Id.ToString()]);
        Assert.Equal("9100", listed["B"].Values[total.Id.ToString()]);
    }

    [Fact]
    public async Task Today_comes_from_the_clock()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero));
        var items = new ItemService(_factory, clock);
        var sheetId = (await _sheets.CreateAsync(new CreateSheetRequest("Plan", null, true))).Id;
        var today = await _columns.CreateAsync(sheetId, new CreateColumnRequest("Today", ColumnKind.Date, Formula: "TODAY()"));
        await items.CreateAsync(sheetId, new CreateItemRequest("Task"));

        Assert.Equal("2026-03-09", Assert.Single(await items.ListAsync(sheetId)).Values[today.Id.ToString()]);
    }

    [Theory]
    [InlineData("[Budget] /", "ends too early")]
    [InlineData("SUMM([Budget])", "Unknown function SUMM")]
    [InlineData("[Budgett] * 2", "There is no column named [Budgett]")]
    [InlineData("ROUND()", "ROUND takes 1 to 2 arguments")]
    public async Task Create_rejects_invalid_formulas(string formula, string expectedFragment)
    {
        var (sheetId, _, _, _) = await NewCostSheetAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _columns.CreateAsync(sheetId, new CreateColumnRequest("Bad", ColumnKind.Number, Formula: formula)));

        Assert.Contains("Invalid formula for column 'Bad'", ex.Message);
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public async Task Update_validates_and_trims_formulas()
    {
        var (sheetId, _, _, rate) = await NewCostSheetAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _columns.UpdateAsync(sheetId, rate.Id, new UpdateColumnRequest("Cost per hour", [], "[Budget] / (", rate.Order)));
        var updated = await _columns.UpdateAsync(sheetId, rate.Id,
            new UpdateColumnRequest("Cost per hour", [], "  ROUND([Budget] / [Planned hours], 2)  ", rate.Order));

        Assert.Equal("ROUND([Budget] / [Planned hours], 2)", updated.Formula);
    }

    [Fact]
    public async Task Formula_may_reference_its_own_new_name()
    {
        var (sheetId, _, _, _) = await NewCostSheetAsync();

        var column = await _columns.CreateAsync(sheetId,
            new CreateColumnRequest("Total", ColumnKind.Number, Formula: "[Planned hours] + SUM(CHILDREN([Total]))"));

        Assert.NotNull(column.Formula);
    }

    [Fact]
    public async Task Blank_formula_means_no_formula()
    {
        var (sheetId, _, _, _) = await NewCostSheetAsync();

        var column = await _columns.CreateAsync(sheetId, new CreateColumnRequest("Notes", ColumnKind.Text, Formula: "   "));

        Assert.Null(column.Formula);
    }

    [Fact]
    public async Task Turning_a_column_into_a_formula_drops_its_stored_values()
    {
        var (sheetId, hours, _, _) = await NewCostSheetAsync();
        var manual = await _columns.CreateAsync(sheetId, new CreateColumnRequest("Double hours", ColumnKind.Number));
        var item = await _items.CreateAsync(sheetId, new CreateItemRequest("Task", Values: Values((hours, "4"), (manual, "typed"))));

        await _columns.UpdateAsync(sheetId, manual.Id, new UpdateColumnRequest("Double hours", [], "[Planned hours] * 2", manual.Order));

        Assert.DoesNotContain(manual.Id.ToString(), await StoredValuesAsync(item.Id));
        Assert.Equal("8", Assert.Single(await _items.ListAsync(sheetId)).Values[manual.Id.ToString()]);
    }

    [Fact]
    public async Task Circular_formulas_are_allowed_but_show_an_error()
    {
        var (sheetId, _, _, _) = await NewCostSheetAsync();
        var a = await _columns.CreateAsync(sheetId, new CreateColumnRequest("A", ColumnKind.Number, Formula: "1"));
        var b = await _columns.CreateAsync(sheetId, new CreateColumnRequest("B", ColumnKind.Number, Formula: "[A] + 1"));
        await _columns.UpdateAsync(sheetId, a.Id, new UpdateColumnRequest("A", [], "[B] + 1", a.Order));
        await _items.CreateAsync(sheetId, new CreateItemRequest("Task"));

        var values = Assert.Single(await _items.ListAsync(sheetId)).Values;

        Assert.Equal("#CIRCULAR!", values[a.Id.ToString()]);
        Assert.Equal("#CIRCULAR!", values[b.Id.ToString()]);
    }

    private async Task<Dictionary<string, string?>> StoredValuesAsync(Guid itemId)
    {
        await using var db = await _factory.CreateAsync();
        return (await db.Items.AsNoTracking().SingleAsync(i => i.Id == itemId)).Values;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
