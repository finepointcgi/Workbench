using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Workbench.Application.PerDiem;
using Workbench.Contracts;
using Workbench.Contracts.PerDiem;
using Workbench.Domain.PerDiem;
using Workbench.Tests.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.PerDiem;

public sealed class PerDiemRateTests
{
    private static PerDiemLocationDto Conus(string state, string destination) => new(PerDiemTable.Conus, "US", state, destination);

    [Fact]
    public async Task Seasonal_rates_pick_the_season_containing_the_date()
    {
        await using var host = await CreateAsync();
        await host.As(Finance).Get<IPerDiemRateService>().InstallSampleRatesAsync(2027);
        var rates = host.Get<IPerDiemRateService>();

        var october = await rates.LookupAsync(Conus("CA", "San Diego"), new DateOnly(2026, 10, 20));
        var july = await rates.LookupAsync(Conus("CA", "San Diego"), new DateOnly(2027, 7, 15));
        var septemberDc = await rates.LookupAsync(Conus("DC", "District of Columbia"), new DateOnly(2027, 9, 1));
        var augustDc = await rates.LookupAsync(Conus("dc", "district of columbia"), new DateOnly(2027, 8, 31));

        Assert.Equal((205m, 86m, false), (october.LodgingMax, october.MieRate, october.IsStandardFallback));
        Assert.Equal(252m, july.LodgingMax);
        Assert.Equal(258m, septemberDc.LodgingMax);
        Assert.Equal((195m, 92m), (augustDc.LodgingMax, augustDc.MieRate));
        Assert.Equal((23m, 26m, 38m, 5m, 69m), (augustDc.Tier.Breakfast, augustDc.Tier.Lunch, augustDc.Tier.Dinner, augustDc.Tier.Incidentals, augustDc.Tier.FirstLastDay));
    }

    [Fact]
    public async Task Unlisted_destinations_fall_back_to_the_standard_conus_rate()
    {
        await using var host = await CreateAsync();
        var rates = host.As(Finance).Get<IPerDiemRateService>();

        // No table at all: the built-in standard rate.
        var builtIn = await rates.LookupAsync(Conus("OH", "Dayton"), new DateOnly(2026, 11, 3));
        Assert.Equal((110m, 68m, true), (builtIn.LodgingMax, builtIn.MieRate, builtIn.IsStandardFallback));

        await rates.InstallSampleRatesAsync(2027);
        var dayton = await rates.LookupAsync(Conus("OH", "Dayton"), new DateOnly(2026, 11, 3));
        Assert.Equal((110m, 68m, true), (dayton.LodgingMax, dayton.MieRate, dayton.IsStandardFallback));
        Assert.Contains(dayton.Notes, n => n.Contains("standard CONUS rate", StringComparison.Ordinal));
        Assert.Equal((16m, 19m, 28m), (dayton.Tier.Breakfast, dayton.Tier.Lunch, dayton.Tier.Dinner));

        // A year with no table uses the latest earlier year's.
        var nextYear = await rates.LookupAsync(Conus("AL", "Huntsville"), new DateOnly(2027, 11, 3));
        Assert.Equal(121m, nextYear.LodgingMax);
        Assert.Contains(nextYear.Notes, n => n.Contains("FY2028", StringComparison.Ordinal));
    }

    [Fact]
    public void Gsa_style_csv_with_a_title_row_and_month_seasons_is_parsed()
    {
        var csv = """
            FY2027 Per Diem Rates for CONUS
            ID,STATE,DESTINATION,COUNTY/LOCATION DEFINED,SEASON BEGIN,SEASON END,FY27 Lodging Rate,FY27 M&IE
            1,AL,Birmingham,Jefferson,,,"$118",74
            2,CO,Aspen,Pitkin,October,November,"$212",86
            3,CO,Aspen,Pitkin,12/1,03/31,"$406",86
            4,,Standard Rate,,,,110,68
            5,XX,Broken,,,,abc,1
            """;

        var result = PerDiemFileParser.Parse("rates.csv", Encoding.UTF8.GetBytes(csv), 2027, PerDiemRateKind.Conus);

        Assert.Equal(4, result.Rates.Count);
        var birmingham = result.Rates[0];
        Assert.Equal(("AL", "Birmingham", "Jefferson", 118m, 74m), (birmingham.State, birmingham.Destination, birmingham.County, birmingham.LodgingMax, birmingham.MieRate));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2027, 9, 30)), (birmingham.SeasonStart, birmingham.SeasonEnd));
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30)), (result.Rates[1].SeasonStart, result.Rates[1].SeasonEnd));
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2027, 3, 31), 406m), (result.Rates[2].SeasonStart, result.Rates[2].SeasonEnd, result.Rates[2].LodgingMax));
        Assert.True(result.Rates[3].IsStandard);
        Assert.Equal(PerDiemFileParser.StandardDestination, result.Rates[3].Destination);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Sample_file_and_an_excel_workbook_parse_the_same_way()
    {
        var sample = PerDiemFileParser.Parse("gsa-conus-sample.csv", PerDiemRateService.SampleFile(), 2027, PerDiemRateKind.Conus);
        Assert.Equal(8, sample.Rates.Count);
        Assert.Single(sample.Rates, r => r.IsStandard);

        var xlsx = Xlsx(
            ["Country", "State", "Destination", "County", "Season Begin", "Season End", "Lodging", "M&IE"],
            ["Germany", "", "Stuttgart", "Boeblingen", "October 1", "September 30", "170", "113"]);
        var excel = PerDiemFileParser.Parse("oconus.xlsx", xlsx, 2027, PerDiemRateKind.Oconus);
        var stuttgart = Assert.Single(excel.Rates);
        Assert.Equal(("Germany", "Stuttgart", 170m, 113m, false), (stuttgart.Country, stuttgart.Destination, stuttgart.LodgingMax, stuttgart.MieRate, stuttgart.IsStandard));
    }

    [Fact]
    public async Task Oconus_import_scales_the_mie_breakdown_and_the_latest_import_is_in_force()
    {
        await using var host = await CreateAsync();
        var rates = host.As(Finance).Get<IPerDiemRateService>();
        var csv = "Country,Destination,Season Begin,Season End,Lodging,M&IE\nGermany,Stuttgart,,,170,113\n";
        var first = await rates.ImportFileAsync(new ImportPerDiemFileRequest(PerDiemTable.Oconus, 2027, "oconus.csv", Encoding.UTF8.GetBytes(csv)));
        Assert.Equal(("OCONUS CSV", 1), (first.Batch.Source, first.Batch.RateCount));

        var stuttgart = await rates.LookupAsync(new PerDiemLocationDto(PerDiemTable.Oconus, "Germany", "", "Stuttgart"), new DateOnly(2026, 12, 1));
        Assert.Equal((170m, 113m), (stuttgart.LodgingMax, stuttgart.MieRate));
        Assert.Equal(113m, stuttgart.Tier.Breakfast + stuttgart.Tier.Lunch + stuttgart.Tier.Dinner + stuttgart.Tier.Incidentals);

        await Task.Delay(5);
        await rates.ImportFileAsync(new ImportPerDiemFileRequest(PerDiemTable.Oconus, 2027, "oconus2.csv", Encoding.UTF8.GetBytes(csv.Replace("170", "180"))));
        var table = await rates.GetTableAsync(new PerDiemRateQuery(2027, PerDiemTable.Oconus));
        Assert.Equal(180m, Assert.Single(table.Rates).LodgingMax);
        Assert.Equal(2, table.Batches.Count);
    }

    [Fact]
    public async Task Gsa_api_import_goes_through_the_client_and_turns_months_into_seasons()
    {
        await using var host = await CreateAsync();
        using var json = JsonDocument.Parse("""
            [
              { "City": "Standard Rate", "State": "", "County": null, "Meals": 68, "StandardRate": "true",
                "Jan": 110, "Feb": 110, "Mar": 110, "Apr": 110, "May": 110, "Jun": 110, "Jul": 110, "Aug": 110, "Sep": 110, "Oct": 110, "Nov": 110, "Dec": 110 },
              { "City": "San Diego", "State": "CA", "County": "San Diego", "Meals": 86,
                "Oct": 205, "Nov": 205, "Dec": 205, "Jan": 205, "Feb": 205, "Mar": 205, "Apr": 205, "May": 205, "Jun": 205, "Jul": 252, "Aug": 252, "Sep": 205 }
            ]
            """);
        host.Gsa.Lodging.AddRange(GsaPerDiemHttpClient.ParseLodging(json.RootElement));
        host.Gsa.Tiers.AddRange(GsaPerDiemHttpClient.ParseMie(JsonDocument.Parse("""[{"total":86,"breakfast":22,"lunch":23,"dinner":36,"incidentals":5,"FirstLastDay":64.5}]""").RootElement));

        var result = await host.As(Finance).Get<IPerDiemRateService>().ImportFromGsaApiAsync(2027);

        Assert.Equal(2, host.Gsa.Calls);
        Assert.Equal(("GSA API", 4, 1), (result.Batch.Source, result.Batch.RateCount, result.Batch.TierCount)); // standard + 3 San Diego seasons
        var july = await host.Get<IPerDiemRateService>().LookupAsync(Conus("CA", "San Diego"), new DateOnly(2027, 7, 4));
        Assert.Equal((252m, 64.5m), (july.LodgingMax, july.Tier.FirstLastDay));
    }

    [Fact]
    public async Task Only_finance_and_admins_import_rates_but_anyone_signed_in_looks_them_up()
    {
        await using var host = await CreateAsync();
        var csv = Encoding.UTF8.GetBytes("Destination,Lodging,M&IE\nStandard CONUS,110,68\n");

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Analyst).Get<IPerDiemRateService>().ImportFileAsync(new ImportPerDiemFileRequest(PerDiemTable.Conus, 2027, "a.csv", csv)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(ResourceManager).Get<IPerDiemRateService>().InstallSampleRatesAsync(2027));
        await host.As(Admin).Get<IPerDiemRateService>().ImportFileAsync(new ImportPerDiemFileRequest(PerDiemTable.Conus, 2027, "a.csv", csv));

        Assert.NotEmpty((await host.As(Analyst).Get<IPerDiemRateService>().GetTableAsync()).Rates);
    }

    private static byte[] Xlsx(params string[][] rows)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            void Entry(string path, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }

            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Entry("xl/workbook.xml", $"""<workbook xmlns="{ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Rates" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Entry("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml" Type="worksheet"/></Relationships>""");
            var strings = rows.SelectMany(r => r).Distinct().ToList();
            Entry("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\">{string.Concat(strings.Select(s => $"<si><t>{System.Security.SecurityElement.Escape(s)}</t></si>"))}</sst>");
            var sheetRows = rows.Select((r, i) => $"<row r=\"{i + 1}\">{string.Concat(r.Select((c, j) => c.Length == 0 ? "" : $"<c r=\"{(char)('A' + j)}{i + 1}\" t=\"s\"><v>{strings.IndexOf(c)}</v></c>"))}</row>");
            Entry("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData>{string.Concat(sheetRows)}</sheetData></worksheet>");
        }

        return stream.ToArray();
    }
}
