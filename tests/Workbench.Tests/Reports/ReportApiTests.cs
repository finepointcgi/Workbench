using System.Net;
using Workbench.ApiClient;
using Workbench.Contracts;
using Workbench.Contracts.Reports;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Reports;

/// <summary>The REST endpoints and the HTTP clients end to end on the Development web host and its seed.</summary>
public sealed class ReportApiTests
{
    [Fact]
    public async Task Report_endpoints_round_trip_through_the_api_client_with_each_users_permissions()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = factory.CreateClientAs("pm@example.com");
        using var employee = factory.CreateClientAs("analyst@example.com");

        var catalog = await new HttpStandardReportService(employee).ListAsync();
        Assert.False(catalog.Single(r => r.Key == StandardReportKeys.ArAging).Available);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => new HttpStandardReportService(employee).RunAsync(StandardReportKeys.ArAging, new ReportParameters()));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => new HttpStandardReportService(employee).RunAsync("missing", new ReportParameters()));

        var labor = await new HttpStandardReportService(finance).RunAsync(StandardReportKeys.LaborDistribution, new ReportParameters(Range: ReportDateRange.ThisFiscalYear));
        Assert.Contains(labor.Columns, c => c.Key == "cost");
        var own = await new HttpStandardReportService(employee).RunAsync(StandardReportKeys.LaborDistribution, new ReportParameters(Range: ReportDateRange.ThisFiscalYear));
        Assert.DoesNotContain(own.Columns, c => c.Key == "cost");

        var sources = await new HttpReportWriterService(employee).ListSourcesAsync();
        Assert.DoesNotContain(sources, s => s.Key == ReportSourceKeys.GlLines);
        await Assert.ThrowsAsync<ArgumentException>(() => new HttpReportWriterService(employee).RunAsync(new ReportDefinition(ReportSourceKeys.TimeEntries, [new("bogus")])));

        var saved = await new HttpSavedReportService(finance).CreateAsync(new SaveReportRequest("PSR", null, ReportVisibility.Private, null,
            StandardReportKeys.ProjectStatus, new ReportParameters(Range: ReportDateRange.ThisMonth), null));
        var file = await new HttpSavedReportService(finance).ExportAsync(saved.Id, ReportFileFormat.Xlsx);
        Assert.Equal((byte)'P', file.Content[0]);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => new HttpSavedReportService(employee).GetAsync(saved.Id));

        using var download = await finance.GetAsync($"api/v1/reports/saved/{saved.Id}/file?format=Csv");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/csv", download.Content.Headers.ContentType?.MediaType);

        await new HttpSavedReportService(finance).DeleteAsync(saved.Id);
        Assert.Empty(await new HttpSavedReportService(finance).ListAsync());
    }
}
