using Workbench.Contracts.Imports;
using Workbench.Contracts.Sheets;
using ImportJobStatus = Workbench.Contracts.Imports.ImportJobStatus;

namespace Workbench.Tests.Imports;

/// <summary>Smartsheet's multi-select, symbol, Harvey ball, link and auto-number columns import as the matching Workbench types.</summary>
public sealed class SmartsheetColumnTypeImportTests
{
    private const string SheetId = "2331373580117892";

    [Fact]
    public async Task Newer_column_types_map_and_import_their_values()
    {
        await using var host = await ImportHost.CreateAsync();
        host.Smartsheet.Json("2.0/sheets?includeAll=true", Samples.Fixture("smartsheet-sheets.json"));
        host.Smartsheet.Json($"2.0/sheets/{SheetId}?include=objectValue&level=2", Samples.Fixture("smartsheet-sheet-types.json"));
        var connected = await host.Imports.ConnectSmartsheetAsync(new ConnectSmartsheetRequest(ImportHost.Token, null));
        var job = await host.Imports.SelectSmartsheetSheetsAsync(connected.Job.Id, new SelectSmartsheetSheetsRequest([SheetId]));

        var columns = job.Mapping.Columns.ToDictionary(c => c.Source);
        Assert.Equal(ColumnKind.MultiSelect, columns["Areas"].Type);
        Assert.Equal(["Cost", "Schedule", "Scope"], columns["Areas"].Options);
        Assert.Equal(ColumnKind.Symbol, columns["Health"].Type);
        Assert.Equal([SymbolSets.Rag], columns["Health"].Options);
        Assert.Equal(ColumnKind.Symbol, columns["Rating"].Type);
        Assert.Equal(ColumnKind.Symbol, columns["Watch"].Type);
        Assert.Equal(ColumnKind.Progress, columns["Mitigated"].Type);
        Assert.Equal(ColumnKind.Hyperlink, columns["Reference"].Type);
        Assert.Equal(ColumnKind.AutoNumber, columns["Risk ID"].Type);
        Assert.Equal(["RSK-", "4", "12"], columns["Risk ID"].Options);
        Assert.False(columns["Risk ID"].Include); // system columns are off unless asked for
        Assert.Equal(ColumnKind.Symbol, columns["Priority"].Type);

        var mapping = job.Mapping with { Columns = job.Mapping.Columns.Select(c => c.Source == "Risk ID" ? c with { Include = true } : c).ToList() };
        await host.Imports.SaveMappingAsync(job.Job.Id, new SaveImportMappingRequest(mapping, job.Options));
        await host.Imports.ValidateAsync(job.Job.Id);
        var dry = await host.Imports.DryRunAsync(job.Job.Id);
        var done = await host.Imports.ImportAsync(dry.Job.Id);
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);

        var sheet = (await host.Get<ISheetService>().ListAsync()).Single(s => s.Name == "Risk Register");
        var detail = (await host.Get<ISheetService>().GetAsync(sheet.Id))!;
        string? Value(ItemDto row, string column) => row.Values.GetValueOrDefault(detail.Columns.Single(c => c.Name == column).Id.ToString());
        var rows = (await host.Get<IItemService>().ListAsync(sheet.Id)).ToDictionary(r => r.Title);
        var vendor = rows["Vendor slips"];
        Assert.Equal("Cost\nSchedule", Value(vendor, "Areas"));
        Assert.Equal("Red", Value(vendor, "Health"));
        Assert.Equal("4", Value(vendor, "Rating"));
        Assert.Equal("Flagged", Value(vendor, "Watch"));
        Assert.Equal("50", Value(vendor, "Mitigated"));
        Assert.Equal("[Contract](https://example.com/contract)", Value(vendor, "Reference"));
        Assert.Equal("High", Value(vendor, "Priority"));
        Assert.Equal("RSK-0012", Value(vendor, "Risk ID")); // numbered in sheet order from the first number
        var scope = rows["Scope creep"];
        Assert.Null(Value(scope, "Watch"));
        Assert.Equal("100", Value(scope, "Mitigated"));
        Assert.Equal("https://example.com/scope", Value(scope, "Reference"));
        Assert.Equal("RSK-0013", Value(scope, "Risk ID"));
    }
}
