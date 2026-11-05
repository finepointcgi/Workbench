using Microsoft.EntityFrameworkCore;
using Workbench.ApiClient;
using Workbench.Contracts.Contracts;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;

namespace Workbench.Tests.GoldenPath;

/// <summary>A project manager sees the funding warning for the projects they manage, on Home and on the project's Overview; an employee does not.</summary>
public sealed class PmFundingWarningTests
{
    private static bool IsFunding(Workbench.Contracts.MyTodo.MyTodoItem i) => i.Key.StartsWith("funding:", StringComparison.Ordinal);

    [Fact]
    public async Task Pat_who_is_both_Finance_and_the_PM_sees_it_once_and_per_contract()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var pat = web.CreateClientAs("pm@example.com");

        var item = Assert.Single((await new HttpMyTodoService(pat).GetAsync()).Items, IsFunding); // 1001 has two CLINs, one running low
        Assert.Contains("1001 is running low on funding", item.Title);
        Assert.Contains("CLIN 0001 on", item.Reason);
        Assert.Contains("Slow spending or ask Finance to request incremental funding.", item.Reason);
        Assert.DoesNotContain("contracting officer", item.Reason);
    }

    [Fact]
    public async Task Sam_sees_the_warning_for_a_project_he_manages_and_Alex_does_not()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs("pm@example.com");
        using var sam = web.CreateClientAs("sam.pm@example.com");
        using var alex = web.CreateClientAs("analyst@example.com");
        var project = (await new HttpProjectService(finance).ListProjectsAsync()).Single(p => p.Code == "1001"); // its first CLIN is past the critical threshold

        Assert.DoesNotContain((await new HttpMyTodoService(sam).GetAsync()).Items, IsFunding); // not his yet
        var dbFactory = (Workbench.Application.IWorkbenchDbContextFactory)web.Services.GetService(typeof(Workbench.Application.IWorkbenchDbContextFactory))!;
        await using (var db = await dbFactory.CreateAsync())
        {
            var samId = await db.Set<Employee>().Where(e => e.Email == "sam.pm@example.com").Select(e => e.Id).SingleAsync();
            (await db.Set<Project>().SingleAsync(p => p.Id == project.Id)).ProjectManagerId = samId;
            await db.SaveChangesAsync();
        }

        var run = FundingRunway.From((await new HttpFundingService(sam).GetProjectFundingStatusAsync(project.Id))!)!;
        Assert.True(run.IsLow);
        Assert.Equal(["0001"], run.LowClins);

        var item = Assert.Single((await new HttpMyTodoService(sam).GetAsync()).Items, IsFunding);
        Assert.Contains("1001", item.Title);
        Assert.Equal($"projects/{project.Id}", item.Link);

        Assert.DoesNotContain((await new HttpMyTodoService(alex).GetAsync()).Items, IsFunding);
        await Assert.ThrowsAnyAsync<Exception>(() => new HttpFundingService(alex).GetProjectFundingStatusAsync(project.Id));
    }
}
