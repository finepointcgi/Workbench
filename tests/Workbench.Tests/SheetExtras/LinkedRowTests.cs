using Workbench.Contracts;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Formulas;
using Workbench.Tests.Identity;

namespace Workbench.Tests.SheetExtras;

/// <summary>"Link to row" and lookup columns, and what people who can't see the linked sheet get.</summary>
public sealed class LinkedRowTests
{
    /// <summary>Budget (Finance workspace, admins only) has Amount; Schedule links its Kickoff row to Labor and Travel.</summary>
    private static async Task<(SeededHost Host, ColumnDto Link, ColumnDto Lookup, ColumnDto Amount, ItemDto Labor, ItemDto Travel)> LinkedAsync()
    {
        var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Admin);
        var amount = await host.AddColumnAsync(host.FinanceSheetId, "Amount", ColumnKind.Currency);
        var labor = await host.AddItemAsync(host.FinanceSheetId, "Labor", (amount, "100"));
        var travel = await host.AddItemAsync(host.FinanceSheetId, "Travel", (amount, "50"));
        var link = await host.AddColumnAsync(host.SheetId, "Budget lines", ColumnKind.LinkedRow, host.FinanceSheetId.ToString());
        var lookup = await host.AddColumnAsync(host.SheetId, "Line amounts", ColumnKind.Lookup, link.Id.ToString(), amount.Id.ToString());
        var kickoff = await host.ItemAsync(host.SheetId, host.ItemId);
        await host.SetAsync(host.SheetId, kickoff, link, $"{labor.Id},{travel.Id}");
        return (host, link, lookup, amount, labor, travel);
    }

    [Fact]
    public async Task A_lookup_shows_the_linked_rows_field_and_follows_changes_live()
    {
        var (host, link, lookup, amount, labor, travel) = await LinkedAsync();
        await using var _ = host;

        var kickoff = await host.ItemAsync(host.SheetId, host.ItemId);
        Assert.Equal($"{labor.Id},{travel.Id}", kickoff.Cell(link));
        Assert.Equal("100, 50", kickoff.Cell(lookup));

        await host.SetAsync(host.FinanceSheetId, labor, amount, "125");

        Assert.Equal("125, 50", (await host.ItemAsync(host.SheetId, host.ItemId)).Cell(lookup));
    }

    [Fact]
    public async Task Lookups_are_never_stored_and_deleted_rows_drop_out()
    {
        var (host, link, lookup, _, labor, travel) = await LinkedAsync();
        await using var _ = host;
        var kickoff = await host.ItemAsync(host.SheetId, host.ItemId);
        await host.SetAsync(host.SheetId, kickoff, lookup, "typed over");

        await host.Host.Items.DeleteAsync(host.FinanceSheetId, labor.Id);

        var after = await host.ItemAsync(host.SheetId, host.ItemId);
        Assert.Equal(travel.Id.ToString(), after.Cell(link));
        Assert.Equal("50", after.Cell(lookup));
        var stored = await host.DbAsync(db => Task.FromResult(db.Items.Single(i => i.Id == host.ItemId).Values));
        Assert.DoesNotContain(lookup.Id.ToString(), stored.Keys);
    }

    [Fact]
    public async Task People_who_cannot_see_the_linked_sheet_see_nothing_of_it()
    {
        var (host, link, lookup, _, labor, _) = await LinkedAsync();
        await using var _ = host;
        host.As(SeededHost.Owner); // owns Program Office, not a member of Finance

        var kickoff = await host.ItemAsync(host.SheetId, host.ItemId);
        Assert.Null(kickoff.Cell(link));
        Assert.Null(kickoff.Cell(lookup));
        var links = await host.Links().GetAsync(host.SheetId);
        var column = Assert.Single(links.Links);
        Assert.False(column.CanSee);
        Assert.Null(column.TargetSheetName);
        Assert.Empty(column.Rows);
        Assert.Null(Assert.Single(links.Lookups).FieldName);
        Assert.DoesNotContain(await host.Links().ListSheetsAsync(), s => s.Id == host.FinanceSheetId);

        // Their full-set update can't erase (or change) links they can't see.
        await host.SetAsync(host.SheetId, kickoff, link, labor.Id.ToString());
        host.As(SeededHost.Admin);
        Assert.Equal("100, 50", (await host.ItemAsync(host.SheetId, host.ItemId)).Cell(lookup));
    }

    [Fact]
    public async Task You_can_only_link_to_a_sheet_you_can_see()
    {
        await using var host = await SeededHost.CreateAsync();
        host.As(SeededHost.Owner);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.AddColumnAsync(host.SheetId, "Lines", ColumnKind.LinkedRow, host.FinanceSheetId.ToString()));
        Assert.Contains("sheet you can see", ex.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => host.AddColumnAsync(host.SheetId, "Lines", ColumnKind.LinkedRow));
        await Assert.ThrowsAsync<ArgumentException>(() => host.AddColumnAsync(host.SheetId, "Lookup", ColumnKind.Lookup, Guid.NewGuid().ToString(), "title"));
    }

    [Fact]
    public async Task Links_keep_only_rows_of_the_linked_sheet()
    {
        var (host, link, lookup, _, labor, _) = await LinkedAsync();
        await using var _ = host;
        var kickoff = await host.ItemAsync(host.SheetId, host.ItemId);

        await host.SetAsync(host.SheetId, kickoff, link, $"{labor.Id},{host.ItemId},{Guid.NewGuid()}");

        Assert.Equal(labor.Id.ToString(), (await host.ItemAsync(host.SheetId, host.ItemId)).Cell(link));
        await Assert.ThrowsAsync<ArgumentException>(() => host.SetAsync(host.SheetId, kickoff, link, "Labor"));
    }

    [Fact]
    public async Task A_restricted_field_looked_up_from_another_sheet_is_blank_for_those_who_cannot_see_it()
    {
        await using var host = await SeededHost.CreateAsync();
        var vendors = await host.NewProgramSheetAsync("Vendors");
        var rate = await host.Host.Columns.CreateAsync(vendors, new CreateColumnRequest("Rate", ColumnKind.Currency, IsRestricted: true));
        var acme = await host.AddItemAsync(vendors, "Acme", (rate, "180"));
        var link = await host.AddColumnAsync(host.SheetId, "Vendor", ColumnKind.LinkedRow, vendors.ToString());
        var name = await host.AddColumnAsync(host.SheetId, "Vendor name", ColumnKind.Lookup, link.Id.ToString(), "title");
        var vendorRate = await host.AddColumnAsync(host.SheetId, "Vendor rate", ColumnKind.Lookup, link.Id.ToString(), rate.Id.ToString());
        var kickoff = await host.ItemAsync(host.SheetId, host.ItemId);
        await host.SetAsync(host.SheetId, kickoff, link, acme.Id.ToString());

        Assert.Equal("180", (await host.ItemAsync(host.SheetId, host.ItemId)).Cell(vendorRate)); // the workspace's owner sees restricted columns

        host.As(SeededHost.Editor);
        var seen = await host.ItemAsync(host.SheetId, host.ItemId);
        Assert.Equal("Acme", seen.Cell(name));
        Assert.Null(seen.Cell(vendorRate));
        Assert.Equal(acme.Id.ToString(), seen.Cell(link));
        var rows = Assert.Single((await host.Links().GetAsync(host.SheetId)).Links).Rows;
        Assert.Equal("Acme", Assert.Single(rows).Title);
    }

    [Fact]
    public async Task Formulas_read_a_single_looked_up_value_with_its_type()
    {
        await using var host = await SeededHost.CreateAsync();
        var deliverables = await host.NewProgramSheetAsync("Deliverables");
        var due = await host.AddColumnAsync(deliverables, "Due", ColumnKind.Date);
        var report = await host.AddItemAsync(deliverables, "Status report", (due, "2020-01-01"));
        var link = await host.AddColumnAsync(host.SheetId, "Deliverable", ColumnKind.LinkedRow, deliverables.ToString());
        await host.AddColumnAsync(host.SheetId, "Deliverable due", ColumnKind.Lookup, link.Id.ToString(), due.Id.ToString());
        var late = await host.Host.Columns.CreateAsync(host.SheetId, new CreateColumnRequest("Late", ColumnKind.Checkbox, Formula: "[Deliverable due] < TODAY()"));
        await host.SetAsync(host.SheetId, await host.ItemAsync(host.SheetId, host.ItemId), link, report.Id.ToString());

        Assert.Equal("true", (await host.ItemAsync(host.SheetId, host.ItemId)).Cell(late));
    }

    [Fact]
    public async Task Renaming_a_link_column_keeps_its_settings()
    {
        var (host, link, lookup, _, _, _) = await LinkedAsync();
        await using var _ = host;

        await host.Host.Columns.UpdateAsync(host.SheetId, link.Id, new UpdateColumnRequest("Budget rows", [], null, link.Order));

        Assert.Equal("100, 50", (await host.ItemAsync(host.SheetId, host.ItemId)).Cell(lookup));
        Assert.DoesNotContain(FormulaErrors.Ref, (await host.ItemAsync(host.SheetId, host.ItemId)).Values.Values);
    }

    [Fact]
    public async Task Viewers_cannot_read_links_of_a_sheet_they_cannot_see()
    {
        var (host, _, _, _, _, _) = await LinkedAsync();
        await using var _ = host;
        host.As(SeededHost.Outsider);

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Links().GetAsync(host.SheetId));
    }
}
