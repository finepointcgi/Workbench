using System.Net;
using System.Net.Http.Json;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;

namespace Workbench.Tests.Identity;

/// <summary>
/// Project visibility through the HTTP API on the Development sample data: Alex Rivera (analyst@, an ordinary employee)
/// is on 1001 (charge assignments), 1002 (assigned tasks, plan workspace member) and the indirect codes everyone charges;
/// Pat Morgan (pm@, Finance) sees every project.
/// </summary>
public sealed class ProjectVisibilityWebTests
{
    [Fact]
    public async Task An_employee_gets_only_their_projects_from_the_api_and_not_found_for_the_rest()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var alex = factory.CreateClientAs("analyst@example.com");
        using var pat = factory.CreateClientAs("pm@example.com");

        var all = (await pat.GetFromJsonAsync<List<ProjectDto>>("/api/v1/projects"))!;
        var mine = (await alex.GetFromJsonAsync<List<ProjectDto>>("/api/v1/projects"))!;

        // Alex is on 5103 and 5104 (the staged lifecycle projects), so they are his too.
        Assert.Equal(["1001", "1002", "5103", "5104", "IND"], mine.Select(p => p.Code).Order(StringComparer.Ordinal));
        Assert.Contains(all, p => p.Code == "4101");
        Assert.True(all.Count > mine.Count);

        var hidden = all.First(p => p.Code == "4101");
        using var get = await alex.GetAsync($"/api/v1/projects/{hidden.Id}");
        using var wbs = await alex.GetAsync($"/api/v1/projects/{hidden.Id}/wbs");
        using var hub = await alex.GetAsync($"/api/v1/project-hub/{hidden.Id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wbs.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hub.StatusCode);

        var own = mine.First(p => p.Code == "1001");
        using var ownGet = await alex.GetAsync($"/api/v1/projects/{own.Id}");
        Assert.Equal(HttpStatusCode.OK, ownGet.StatusCode);
        var hubList = (await alex.GetFromJsonAsync<ProjectHubListDto>("/api/v1/project-hub"))!;
        Assert.Equal(["1001", "1002", "5103", "5104", "IND"], hubList.Projects.Select(p => p.Code).Order(StringComparer.Ordinal));
        using var patGet = await pat.GetAsync($"/api/v1/projects/{hidden.Id}");
        Assert.Equal(HttpStatusCode.OK, patGet.StatusCode);
    }
}
