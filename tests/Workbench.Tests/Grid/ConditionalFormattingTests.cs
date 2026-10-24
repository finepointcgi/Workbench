using ClosedXML.Excel;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Grid;

/// <summary>Conditional formatting: rules in plain words, applied in order, stored with the sheet, kept from people who can't see a restricted column.</summary>
public sealed class ConditionalFormattingTests
{
    private static readonly DateOnly Today = new(2026, 10, 23);

    private static readonly ColumnDto Status = new(Guid.NewGuid(), "Status", ColumnKind.Dropdown, 0, ["Working on it", "Stuck", "Done"], null);
    private static readonly ColumnDto Due = new(Guid.NewGuid(), "Due", ColumnKind.Date, 1, [], null);
    private static readonly ColumnDto Done = new(Guid.NewGuid(), "Done", ColumnKind.Progress, 2, [], null);
    private static readonly IReadOnlyList<ColumnDto> Columns = [Status, Due, Done];

    private static ItemDto Row(string title, string status, string due, string done) =>
        new(Guid.NewGuid(), null, 0, title, null, null, null, new Dictionary<string, string?>
        {
            [Status.Id.ToString()] = status, [Due.Id.ToString()] = due, [Done.Id.ToString()] = done,
        });

    private static ListFilterCondition When(ColumnDto column, ListFilterOperator op, string? value = null) => new(column.Id.ToString(), op, value);

    [Fact]
    public void Rules_match_rows_and_relative_dates_and_apply_in_order()
    {
        var stuck = Row("Stuck one", "Stuck", "2026-11-30", "10");
        var late = Row("Late one", "Working on it", "2026-10-20", "60");
        var lateButDone = Row("Late but done", "Done", "2026-10-20", "100");
        var fine = Row("Fine", "Working on it", "2026-11-30", "20");
        SheetFormatRuleDefinition[] rules =
        [
            // When Status is Stuck, color the row red.
            new([When(Status, ListFilterOperator.Equals, "Stuck")], Color: "red"),
            // When Due is before today and Done < 100, bold the Due cell (and color it amber, which loses to red above).
            new([When(Due, ListFilterOperator.LessThan, RelativeDates.Today), When(Done, ListFilterOperator.LessThan, "100")], Target: Due.Id.ToString(), Color: "amber", Bold: true),
            // When Status is Done, strike the title through.
            new([When(Status, ListFilterOperator.Equals, "Done")], Target: SheetFields.Title, Strikethrough: true),
            // Off: never applies.
            new([When(Status, ListFilterOperator.IsNotEmpty)], Color: "purple", Enabled: false),
        ];

        var formats = SheetFormats.Evaluate([stuck, late, lateButDone, fine], Columns, rules, Today);

        Assert.Equal(StatusColors.Red, formats.Row(stuck.Id).Background);
        Assert.Equal(StatusColors.Red, formats.Cell(stuck.Id, Due.Id.ToString()).Background);
        Assert.Null(formats.Row(late.Id).Background); // the amber rule targets one cell
        Assert.Equal(new CellStyle(StatusColors.Amber, true, false), formats.Cell(late.Id, Due.Id.ToString()));
        Assert.True(formats.Cell(late.Id, Status.Id.ToString()).IsEmpty);
        Assert.True(formats.Cell(lateButDone.Id, Due.Id.ToString()).IsEmpty); // 100% done: not late
        Assert.True(formats.Cell(lateButDone.Id, SheetFields.Title).Strikethrough);
        Assert.Empty(formats.RulesFor(fine.Id));
        Assert.Contains("font-weight:600", formats.Cell(late.Id, Due.Id.ToString()).Css);

        // Order matters: with the amber rule first, the overdue cell of a stuck row is amber, the rest of the row red.
        var overdueStuck = Row("Overdue and stuck", "Stuck", "2026-10-01", "0");
        var reordered = SheetFormats.Evaluate([overdueStuck], Columns, [rules[1], rules[0]], Today);
        Assert.Equal(StatusColors.Amber, reordered.Cell(overdueStuck.Id, Due.Id.ToString()).Background);
        Assert.Equal(StatusColors.Red, reordered.Cell(overdueStuck.Id, Status.Id.ToString()).Background);
        Assert.Equal(StatusColors.Red, SheetFormats.Evaluate([overdueStuck], Columns, rules, Today).Cell(overdueStuck.Id, Due.Id.ToString()).Background);

        // "Any" instead of "all"; and "today" moves with the date it's checked on.
        var any = new SheetFormatRuleDefinition([When(Status, ListFilterOperator.Equals, "Stuck"), When(Done, ListFilterOperator.GreaterOrEqual, "60")], MatchAny: true, Bold: true);
        var anyFormats = SheetFormats.Evaluate([stuck, late, lateButDone, fine], Columns, [any], Today);
        Assert.Equal(["Stuck one", "Late one", "Late but done"], new[] { stuck, late, lateButDone, fine }.Where(r => anyFormats.RulesFor(r.Id).Count > 0).Select(r => r.Title));
        var soon = new SheetFormatRuleDefinition([When(Due, ListFilterOperator.LessOrEqual, RelativeDates.Days(7))], Bold: true);
        Assert.False(SheetFormats.Evaluate([fine], Columns, [soon], Today).Any);
        Assert.True(SheetFormats.Evaluate([fine], Columns, [soon], new DateOnly(2026, 11, 25)).Any);
    }

    [Fact]
    public void A_rule_naming_a_column_the_caller_cant_see_never_applies()
    {
        var row = Row("Row", "Stuck", "2026-10-20", "0");
        var hidden = Guid.NewGuid().ToString();
        SheetFormatRuleDefinition[] rules = [new([new ListFilterCondition(hidden, ListFilterOperator.IsEmpty)], Color: "red")];
        Assert.False(SheetFormats.Evaluate([row], Columns, rules, Today).Any);
    }

    [Fact]
    public void Rules_read_in_plain_words()
    {
        string Name(string key) => Columns.FirstOrDefault(c => c.Id.ToString() == key)?.Name ?? "Title";
        Assert.Equal("When Status is Stuck, color the whole row red",
            SheetFormatText.Describe(new([When(Status, ListFilterOperator.Equals, "Stuck")], Color: "red"), Name));
        Assert.Equal("When Due is before today and Done is less than 100, color Due amber, bold",
            SheetFormatText.Describe(new([When(Due, ListFilterOperator.LessThan, "@today"), When(Done, ListFilterOperator.LessThan, "100")], Target: Due.Id.ToString(), Color: "amber", Bold: true), Name));
        Assert.Equal("When Due is on or before 7 days from today, make the whole row bold",
            SheetFormatText.Describe(new([When(Due, ListFilterOperator.LessOrEqual, "@today+7")], Bold: true), Name));
        Assert.Equal(-3, RelativeDates.Offset("@today-3"));
        Assert.Null(RelativeDates.Offset("2026-10-01"));
    }

    [Fact]
    public async Task Rules_are_stored_in_order_and_kept_from_people_who_cant_see_a_restricted_column()
    {
        await using var host = await SeededHost.CreateAsync();
        var columns = host.As(SeededHost.Owner).Host.Get<IColumnService>();
        var status = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Open", "Stuck"]));
        var cost = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Cost", ColumnKind.Currency, IsRestricted: true));
        ISheetFormatRuleService As(string email) => host.As(email).Host.Get<ISheetFormatRuleService>();

        var stuck = new SheetFormatRuleDefinition([new(status.Id.ToString(), ListFilterOperator.Equals, "Stuck")], Color: "red");
        var expensive = new SheetFormatRuleDefinition([new(cost.Id.ToString(), ListFilterOperator.GreaterThan, "1000")], Color: "orange", Bold: true);
        var open = new SheetFormatRuleDefinition([new(status.Id.ToString(), ListFilterOperator.Equals, "Open")], Bold: true);
        var saved = await As(SeededHost.Owner).SaveAsync(host.SheetId, new SaveSheetFormatRulesRequest([new(null, stuck), new(null, expensive), new(null, open)]));
        Assert.Equal(["red", "orange", null], saved.Rules.Select(r => r.Definition.Color));
        Assert.True(saved.CanEdit);

        // An editor who can't see Cost doesn't get the rule that reads it (its color would give the cost away)...
        var editorView = await As(SeededHost.Editor).ListAsync(host.SheetId);
        Assert.Equal(["red", null], editorView.Rules.Select(r => r.Definition.Color));
        Assert.Equal(1, editorView.HiddenCount);

        // ...and saving their list (reordered, one rule edited) keeps it where it was.
        var edited = editorView.Rules[1].Definition with { Strikethrough = true };
        await As(SeededHost.Editor).SaveAsync(host.SheetId, new SaveSheetFormatRulesRequest([new(editorView.Rules[1].Id, edited), new(editorView.Rules[0].Id, stuck)]));
        var after = await As(SeededHost.Owner).ListAsync(host.SheetId);
        Assert.Equal([null, "orange", "red"], after.Rules.Select(r => r.Definition.Color));
        Assert.True(after.Rules[0].Definition.Strikethrough);
        Assert.Equal(saved.Rules[0].Id, after.Rules[2].Id); // kept its id

        // A rule naming the hidden column is rejected for them, as is one with no format.
        await Assert.ThrowsAsync<ArgumentException>(() => As(SeededHost.Editor).SaveAsync(host.SheetId, new SaveSheetFormatRulesRequest([new(null, expensive)])));
        await Assert.ThrowsAsync<ArgumentException>(() => As(SeededHost.Owner).SaveAsync(host.SheetId,
            new SaveSheetFormatRulesRequest([new(null, stuck with { Color = null })])));
        await Assert.ThrowsAsync<ArgumentException>(() => As(SeededHost.Owner).SaveAsync(host.SheetId,
            new SaveSheetFormatRulesRequest([new(null, stuck with { Color = "chartreuse" })])));

        // Viewers read the rules but can't change them; outsiders get nothing.
        var viewer = await As(SeededHost.Viewer).ListAsync(host.SheetId);
        Assert.False(viewer.CanEdit);
        Assert.Equal(2, viewer.Rules.Count);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => As(SeededHost.Viewer).SaveAsync(host.SheetId, new SaveSheetFormatRulesRequest([])));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => As(SeededHost.Outsider).ListAsync(host.SheetId));

        // Administrators see every rule.
        Assert.Equal(3, (await As(SeededHost.Admin).ListAsync(host.SheetId)).Rules.Count);
    }

    [Fact]
    public async Task Excel_export_carries_the_formatting()
    {
        await using var host = await SeededHost.CreateAsync();
        var columns = host.As(SeededHost.Owner).Host.Get<IColumnService>();
        var items = host.As(SeededHost.Owner).Host.Get<IItemService>();
        var status = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Open", "Stuck", "Done"]));
        var key = status.Id.ToString();
        await items.UpdateAsync(host.SheetId, host.ItemId, new UpdateItemRequest("Kickoff", null, null, null, new Dictionary<string, string?> { [key] = "Stuck" }));
        await items.CreateAsync(host.SheetId, new CreateItemRequest("Wrap up", Values: new Dictionary<string, string?> { [key] = "Done" }));
        await host.Host.Get<ISheetFormatRuleService>().SaveAsync(host.SheetId, new SaveSheetFormatRulesRequest(
        [
            new(null, new([new(key, ListFilterOperator.Equals, "Stuck")], Color: "red")),
            new(null, new([new(key, ListFilterOperator.Equals, "Done")], Target: SheetFields.Title, Bold: true, Strikethrough: true)),
        ]));

        var file = await host.As(SeededHost.Viewer).Host.Get<ISheetViewService>().ExportAsync(host.SheetId, new SheetExportRequest(SheetExportFormat.Xlsx));
        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        var sheet = workbook.Worksheets.First();
        var header = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Title").RowNumber();
        var kickoff = sheet.Row(header + 1);
        var wrapUp = sheet.Row(header + 2);
        Assert.Equal("Kickoff", kickoff.Cell(1).GetString());
        Assert.Equal(XLColor.FromHtml(StatusColors.Red.Background), kickoff.Cell(1).Style.Fill.BackgroundColor);
        Assert.Equal(XLColor.FromHtml(StatusColors.Red.Background), kickoff.Cell(2).Style.Fill.BackgroundColor); // the whole row
        Assert.True(wrapUp.Cell(1).Style.Font.Strikethrough);
        Assert.True(wrapUp.Cell(1).Style.Font.Bold);
        Assert.False(wrapUp.Cell(2).Style.Font.Strikethrough); // the title cell only
    }
}
