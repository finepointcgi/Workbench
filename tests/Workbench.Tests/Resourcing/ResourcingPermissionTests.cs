using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// pm@ manages 1001, pm2@ manages 2002; rm@ is a resource manager; analyst@ and dev@ are employees reporting to pm@.
/// </summary>
public sealed class ResourcingPermissionTests
{
    [Theory]
    [InlineData(Pm, true, false)]
    [InlineData(Pm2, false, true)]
    [InlineData(ResourceManager, true, true)]
    [InlineData(Admin, true, true)]
    [InlineData(Finance, false, false)]
    [InlineData(Analyst, false, false)]
    public async Task Project_managers_staff_their_projects_and_resource_managers_staff_all(string email, bool project1, bool project2)
    {
        await using var host = await CreateAsync();
        var allocations = host.As(email).Get<IResourceAllocationService>();

        async Task<bool> TryAsync(Guid projectId)
        {
            try
            {
                await allocations.CreateAsync(new SaveAllocationRequest(host.DevId, projectId, ThisWeek, 8));
                return true;
            }
            catch (WorkbenchForbiddenException)
            {
                return false;
            }
        }

        Assert.Equal((project1, project2), (await TryAsync(host.Project1), await TryAsync(host.Project2)));
        Assert.Equal((project1, project2), (await allocations.CanManageAsync(host.Project1), await allocations.CanManageAsync(host.Project2)));
    }

    [Fact]
    public async Task Changes_and_generation_need_the_allocations_project()
    {
        await using var host = await CreateAsync();
        var created = await host.As(Pm2).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project2, ThisWeek, 8));
        var pm = host.As(Pm).Get<IResourceAllocationService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pm.UpdateAsync(created[0].Id, new SaveAllocationRequest(host.DevId, host.Project2, ThisWeek, 4)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pm.UpdateAsync(created[0].Id, new SaveAllocationRequest(host.DevId, host.Project1, ThisWeek, 4)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pm.DeleteAsync(created[0].Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Analyst).Get<IResourceAllocationService>().GenerateFromPlanAsync(host.Project1));
        await host.As(ResourceManager).Get<IResourceAllocationService>().DeleteAsync(created[0].Id);
    }

    [Fact]
    public async Task Employees_see_their_own_allocations_and_managers_see_their_projects()
    {
        await using var host = await CreateAsync();
        await host.As(Admin).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, ThisWeek, 8));
        await host.Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.AnalystId, host.Project2, ThisWeek, 8));
        var query = new AllocationQuery(ThisWeek, ThisWeek.AddDays(6));

        Assert.Equal(host.DevId, Assert.Single(await host.As(Dev).Get<IResourceAllocationService>().ListAsync(query)).EmployeeId);
        Assert.Equal(host.DevId, Assert.Single(await host.As(Dev).Get<IResourceAllocationService>().ListMineAsync(ThisWeek, ThisWeek.AddDays(6))).EmployeeId);
        Assert.Equal(host.Project1, Assert.Single(await host.As(Pm).Get<IResourceAllocationService>().ListAsync(query)).ProjectId);
        Assert.Equal(2, (await host.As(Finance).Get<IResourceAllocationService>().ListAsync(query)).Count);
        Assert.Equal(2, (await host.As(ResourceManager).Get<IResourceAllocationService>().ListAsync(query)).Count);
    }

    [Fact]
    public async Task Staffing_needs_a_staffing_role_and_employees_see_only_themselves_on_the_heat_map()
    {
        await using var host = await CreateAsync();

        await host.As(Pm).Get<IResourcePlanningService>().GetProjectStaffingAsync(host.Project1);
        await host.As(ResourceManager).Get<IResourcePlanningService>().GetProjectStaffingAsync(host.Project2);
        await host.As(Finance).Get<IResourcePlanningService>().GetProjectStaffingAsync(host.Project2);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Pm).Get<IResourcePlanningService>().GetProjectStaffingAsync(host.Project2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Analyst).Get<IResourcePlanningService>().ListOpenDemandAsync(host.Project1));

        var mine = await host.As(Analyst).Get<IResourcePlanningService>().GetHeatMapAsync(new HeatMapQuery(Today));
        Assert.Equal(host.AnalystId, Assert.Single(mine.Rows).EmployeeId);
        Assert.Equal(4, (await host.As(Pm).Get<IResourcePlanningService>().GetHeatMapAsync(new HeatMapQuery(Today))).Rows.Count);
        Assert.Equal(4, (await host.As(ResourceManager).Get<IResourcePlanningService>().GetHeatMapAsync(new HeatMapQuery(Today))).Rows.Count);

        // Utilization: a supervisor sees their reports and themselves; an employee only themselves.
        Assert.Equal(3, (await host.As(Pm).Get<IResourcePlanningService>().GetUtilizationAsync(Today, Today)).ByPerson.Count);
        Assert.Single((await host.As(Dev).Get<IResourcePlanningService>().GetUtilizationAsync(Today, Today)).ByPerson);
    }

    [Fact]
    public async Task Allocation_based_etc_follows_the_project_financials_rule()
    {
        await using var host = await CreateAsync();

        await host.As(Pm).Get<IAllocationForecastService>().GetProjectSummaryAsync(host.Project1);
        await host.As(Finance).Get<IAllocationForecastService>().GetProjectSummaryAsync(host.Project1);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(ResourceManager).Get<IAllocationForecastService>().GetProjectSummaryAsync(host.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Pm2).Get<IAllocationForecastService>().GetWbsFinancialsAsync(host.Project1));
    }

    [Fact]
    public async Task Resource_manager_is_its_own_org_role_and_does_not_see_all_timesheets()
    {
        await using var host = await CreateAsync();

        var info = await host.As(ResourceManager).Get<ICurrentUserContext>().GetAsync();

        Assert.Equal(OrgRoles.ResourceManager, info.Roles);
        Assert.False(info.CanViewAllTimesheets);
        Assert.False(info.IsFinance);
        Assert.True(info.Roles.IsResourceManager());
        Assert.True(OrgRoles.Admin.IsResourceManager());
    }
}
