using Workbench.Contracts;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Formulas;
using Workbench.Tests.Identity;

namespace Workbench.Tests.SheetExtras;

/// <summary>Cross-sheet references ({Name}), checked per viewer, and renames that rewrite formulas.</summary>
public sealed class CrossSheetFormulaTests
{
    [Fact]
    public async Task A_reference_reads_another_sheets_column_and_hides_it_from_those_who_cannot_see_it()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Admin);
        var amount = await host.AddColumnAsync(host.FinanceSheetId, "Amount", ColumnKind.Currency);
        var kind = await host.AddColumnAsync(host.FinanceSheetId, "Kind", ColumnKind.Text);
        await host.AddItemAsync(host.FinanceSheetId, "Labor", (amount, "100"), (kind, "Direct"));
        await host.AddItemAsync(host.FinanceSheetId, "Travel", (amount, "50"), (kind, "ODC"));
        await host.Formulas().CreateReferenceAsync(host.SheetId, new CreateSheetReferenceRequest("Budget amounts", host.FinanceSheetId, [amount.Id.ToString()]));
        await host.Formulas().CreateReferenceAsync(host.SheetId, new CreateSheetReferenceRequest("Budget lines", host.FinanceSheetId, [kind.Id.ToString(), amount.Id.ToString()]));
        var total = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("Budget total", ColumnKind.Currency, Formula: "SUM({Budget amounts})"));
        var odc = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("ODC budget", ColumnKind.Currency, Formula: "VLOOKUP(\"ODC\", {Budget lines}, 2, false)"));

        var item = await host.ItemAsync(host.SheetId, host.ItemId);
        Assert.Equal("150", item.Cell(total));
        Assert.Equal("50", item.Cell(odc));

        host.As(SeededHost.Owner);
        Assert.Equal(FormulaErrors.NoAccess, (await host.ItemAsync(host.SheetId, host.ItemId)).Cell(total));
        var reference = (await host.Formulas().ListReferencesAsync(host.SheetId)).First();
        Assert.False(reference.CanSee);
        Assert.Null(reference.SourceSheetName);
        Assert.Empty(reference.FieldNames);
    }

    [Fact]
    public async Task Renaming_the_source_column_keeps_the_reference_working()
    {
        await using var host = await SeededHost.CreateAsync();
        var risks = await host.NewProgramSheetAsync("Risks");
        var status = await host.AddColumnAsync(risks, "Status", ColumnKind.Dropdown, "Open", "Closed");
        await host.AddItemAsync(risks, "Risk 1", (status, "Open"));
        await host.AddItemAsync(risks, "Risk 2", (status, "Closed"));
        await host.Formulas().CreateReferenceAsync(host.SheetId, new CreateSheetReferenceRequest("Risk status", risks, [status.Id.ToString()]));
        var open = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("Open risks", ColumnKind.Number, Formula: "COUNTIF({Risk status}, \"Open\")"));

        await host.Host.Columns.UpdateAsync(risks, status.Id, new UpdateColumnRequest("State", ["Open", "Closed"], null, status.Order));

        Assert.Equal("1", (await host.ItemAsync(host.SheetId, host.ItemId)).Cell(open));
        Assert.Equal(["State"], (await host.Formulas().ListReferencesAsync(host.SheetId)).Single().FieldNames);
    }

    [Fact]
    public async Task A_restricted_source_column_is_no_access_for_those_who_cannot_see_it()
    {
        await using var host = await SeededHost.CreateAsync();
        var rates = await host.NewProgramSheetAsync("Rates");
        var rate = await host.Host.Columns.CreateAsync(rates, new CreateColumnRequest("Rate", ColumnKind.Currency, IsRestricted: true));
        await host.AddItemAsync(rates, "Engineer", (rate, "150"));
        await host.Formulas().CreateReferenceAsync(host.SheetId, new CreateSheetReferenceRequest("Rates", rates, [rate.Id.ToString()]));
        var max = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("Top rate", ColumnKind.Currency, Formula: "MAX({Rates})"));
        Assert.Equal("150", (await host.ItemAsync(host.SheetId, host.ItemId)).Cell(max));

        host.As(SeededHost.Editor);

        Assert.Equal(FormulaErrors.NoAccess, (await host.ItemAsync(host.SheetId, host.ItemId)).Cell(max));
    }

    [Fact]
    public async Task References_need_access_to_the_other_sheet_and_a_free_name()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() =>
            host.Formulas().CreateReferenceAsync(host.SheetId, new CreateSheetReferenceRequest("Budget", host.FinanceSheetId, ["title"])));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Formulas().CreateReferenceAsync(host.SheetId, new CreateSheetReferenceRequest("Self", host.SheetId, ["title"])));

        var other = await host.NewProgramSheetAsync("Other");
        await host.Formulas().CreateReferenceAsync(host.SheetId, new CreateSheetReferenceRequest("Titles", other, ["title"]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Formulas().CreateReferenceAsync(host.SheetId, new CreateSheetReferenceRequest("titles", other, ["title"])));

        host.As(SeededHost.Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            host.Formulas().CreateReferenceAsync(host.SheetId, new CreateSheetReferenceRequest("Mine", other, ["title"])));
    }

    [Fact]
    public async Task Saving_a_formula_with_an_unknown_reference_explains_it()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("Count", ColumnKind.Number, Formula: "COUNT({Nowhere})")));

        Assert.Contains("no cross-sheet reference named {Nowhere}", ex.Message);
    }

    [Fact]
    public async Task Check_explains_problems_and_shows_the_first_rows_answer_in_plain_words()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);
        var hours = await host.AddColumnAsync(host.SheetId, "Hours", ColumnKind.Number);
        await host.SetAsync(host.SheetId, await host.ItemAsync(host.SheetId, host.ItemId), hours, "8");

        var ok = await host.Formulas().CheckAsync(host.SheetId, "=[Hours] * 2");
        Assert.Null(ok.Problem);
        Assert.Equal("Kickoff", ok.SampleRowTitle);
        Assert.Equal("16", ok.SampleValue);

        var divide = await host.Formulas().CheckAsync(host.SheetId, "[Hours] / 0");
        Assert.Equal(FormulaErrors.DivideByZero, divide.SampleValue);
        Assert.Contains("divides by zero", divide.Explanation);

        var typo = await host.Formulas().CheckAsync(host.SheetId, "[Huors] * 2");
        Assert.Contains("Did you mean [Hours]?", typo.Problem);

        Assert.Contains(await host.Formulas().ListFunctionsAsync(), f => f.Name == "COUNTIFS" && f.Description.Length > 0);
    }

    [Fact]
    public async Task Renaming_a_column_rewrites_every_formula_that_uses_it()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);
        var budget = await host.AddColumnAsync(host.SheetId, "Budget", ColumnKind.Currency);
        var hours = await host.AddColumnAsync(host.SheetId, "Hours", ColumnKind.Number);
        var rate = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("Rate", ColumnKind.Currency, Formula: "[Budget] / [Hours]"));
        var total = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("All budgets", ColumnKind.Currency, Formula: "SUM([Budget]:[Budget]) + SUM(CHILDREN([budget]))"));
        var kickoff = await host.ItemAsync(host.SheetId, host.ItemId);
        await host.Host.Items.UpdateAsync(host.SheetId, kickoff.Id, new UpdateItemRequest(kickoff.Title, null, null, null,
            new Dictionary<string, string?> { [budget.Id.ToString()] = "800", [hours.Id.ToString()] = "4" }));

        await host.Host.Columns.UpdateAsync(host.SheetId, budget.Id, new UpdateColumnRequest("Cost", [], null, budget.Order));

        var sheet = await host.Host.Sheets.GetAsync(host.SheetId);
        Assert.Equal("[Cost] / [Hours]", sheet!.Columns.Single(c => c.Id == rate.Id).Formula);
        Assert.Equal("SUM([Cost]:[Cost]) + SUM(CHILDREN([Cost]))", sheet.Columns.Single(c => c.Id == total.Id).Formula);
        var item = await host.ItemAsync(host.SheetId, host.ItemId);
        Assert.Equal("200", item.Cell(rate));
        Assert.Equal("800", item.Cell(total));
    }

    [Fact]
    public async Task Renaming_onto_another_columns_name_is_refused()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);
        var budget = await host.AddColumnAsync(host.SheetId, "Budget", ColumnKind.Currency);
        await host.AddColumnAsync(host.SheetId, "Cost", ColumnKind.Currency);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Host.Columns.UpdateAsync(host.SheetId, budget.Id, new UpdateColumnRequest("cost", [], null, budget.Order)));
        Assert.Contains("already a column named", ex.Message);

        await host.Host.Columns.UpdateAsync(host.SheetId, budget.Id, new UpdateColumnRequest("BUDGET", [], null, budget.Order));
    }
}
