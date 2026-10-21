using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Customization;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Customization;

public sealed class ListsAndStatusTests
{
    [Fact]
    public async Task Built_in_lists_start_from_todays_values_and_drive_the_vendor_rules()
    {
        await using var host = (await SeededHost.CreateAsync()).As(SeededHost.Admin);
        var lists = host.Host.Get<ICustomListService>();
        var socio = await lists.GetAsync("vendor-socioeconomic");
        Assert.True(socio.IsSystem);
        Assert.False(socio.IsCustomized);
        Assert.Equal(PurchasingCatalog.SocioEconomicCategories, socio.Items.Select(i => i.Code));
        Assert.Contains(await lists.ListAsync(), l => l.Key == "tax-exemption-reasons" && l.Items.Any(i => i.Code == "Resale"));

        var vendors = host.Host.Get<IVendorService>();
        await Assert.ThrowsAsync<ArgumentException>(() => vendors.CreateAsync(new SaveVendorRequest("NEWCO", "NewCo", SocioEconomicCategories: ["ANC"])));

        // An administrator adds a category (and renames one); the vendor rules follow the list.
        await lists.SaveAsync(new SaveValueListRequest("vendor-socioeconomic", socio.Name, null,
            [.. socio.Items.Select(i => new SaveValueListItem(i.Code, i.Code == "WOSB" ? "Women-owned small business" : i.Label)), new("ANC", "Alaska Native Corporation")]));
        var vendor = await vendors.CreateAsync(new SaveVendorRequest("NEWCO", "NewCo", SocioEconomicCategories: ["anc", "Women-owned small business"]));
        Assert.Equal(["ANC", "WOSB"], vendor.SocioEconomicCategories.Order());

        // A code stored on a vendor can't be removed, only turned off.
        var withoutAnc = (await lists.GetAsync("vendor-socioeconomic")).Items.Where(i => i.Code != "ANC").Select(i => new SaveValueListItem(i.Code, i.Label)).ToList();
        await Assert.ThrowsAsync<InvalidOperationException>(() => lists.SaveAsync(new SaveValueListRequest("vendor-socioeconomic", socio.Name, null, withoutAnc)));
        await lists.SaveAsync(new SaveValueListRequest("vendor-socioeconomic", socio.Name, null, [.. withoutAnc, new("ANC", "Alaska Native Corporation", IsActive: false)]));
        await Assert.ThrowsAsync<ArgumentException>(() => vendors.CreateAsync(new SaveVendorRequest("NEWCO2", "NewCo Two", SocioEconomicCategories: ["ANC"])));
        await vendors.UpdateAsync(vendor.Id, new SaveVendorRequest("NEWCO", "NewCo", SocioEconomicCategories: ["ANC"])); // Kept where it's already held.

        await lists.DeleteAsync("vendor-socioeconomic"); // Built-in: back to the defaults.
        Assert.False((await lists.GetAsync("vendor-socioeconomic")).IsCustomized);

        host.As(SeededHost.Finance);
        await Assert.ThrowsAsync<Contracts.WorkbenchForbiddenException>(() => lists.SaveAsync(new SaveValueListRequest("program-offices", "Program offices", null, [new("a", "A")])));
    }

    [Fact]
    public async Task Relabeling_a_status_changes_only_its_label()
    {
        await using var host = (await SeededHost.CreateAsync()).As(SeededHost.Admin);
        Guid projectId, employeeId;
        await using (var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var project = new Project { Code = "7101", Name = "Relabel", Status = ProjectStatus.Active };
            db.Set<Project>().Add(project);
            db.Set<WbsElement>().Add(new WbsElement { ProjectId = project.Id, Code = "7101.01", Name = "Work", RequiresAssignment = false });
            var employee = new Employee { EmployeeNumber = "E1131", Email = "remy@example.com", DisplayName = "Remy Hart", HireDate = new DateOnly(2024, 1, 2) };
            db.Set<Employee>().Add(employee);
            await db.SaveChangesAsync();
            (projectId, employeeId) = (project.Id, employee.Id);
        }

        var projects = host.Host.Get<IProjectService>();
        var chargeCodesBefore = await projects.ListChargeCodesAsync(employeeId, new DateOnly(2026, 10, 20));
        var lists = host.Host.Get<ICustomListService>();
        var saved = await lists.SaveStatusSetAsync("project-status", new SaveStatusSetRequest(
            [new SaveStatusValue("Active", "In flight", [new("awaiting_funding", "Awaiting funding"), new("at_risk", "At risk")]), new SaveStatusValue("OnHold", "Paused")]));
        Assert.Equal("In flight", saved.Values.Single(v => v.Value == "Active").Label);
        Assert.Equal("On hold", saved.Values.Single(v => v.Value == "OnHold").DefaultLabel);

        // Same state machine: the project is still Active and charges exactly as before.
        Assert.Equal(ProjectState.Active, (await projects.GetProjectAsync(projectId))!.Status);
        Assert.Equal(chargeCodesBefore.Select(c => c.WbsElementId), (await projects.ListChargeCodesAsync(employeeId, new DateOnly(2026, 10, 20))).Select(c => c.WbsElementId));
        await Assert.ThrowsAsync<ArgumentException>(() => lists.SaveStatusSetAsync("project-status", new SaveStatusSetRequest([new SaveStatusValue("Shipped", "Gone")])));
        await Assert.ThrowsAsync<ArgumentException>(() => lists.SaveStatusSetAsync("timesheet-status", new SaveStatusSetRequest([new SaveStatusValue("Draft", null, [new("x", "X")])])));

        // Sub-statuses refine the record's current status, and fall away when it moves on.
        var sub = await lists.SetSubStatusAsync("project-status", projectId, "at_risk");
        Assert.Equal(("Active", "at_risk", "At risk"), (sub.ParentValue, sub.Code, sub.Label));
        var audit = await host.Host.Get<IAuditLogService>().ListAsync("Project", projectId);
        Assert.Contains(audit, a => a.Action == "Sub-status set");
        await using (var db = await host.Host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            (await db.Set<Project>().SingleAsync(p => p.Id == projectId)).Status = ProjectStatus.OnHold;
            await db.SaveChangesAsync();
        }

        var after = await lists.GetSubStatusAsync("project-status", projectId);
        Assert.Equal(("OnHold", null), (after.ParentValue, after.Code));
        await Assert.ThrowsAsync<ArgumentException>(() => lists.SetSubStatusAsync("project-status", projectId, "at_risk"));

        // Back to the default label by clearing it.
        var reset = await lists.SaveStatusSetAsync("project-status", new SaveStatusSetRequest([new SaveStatusValue("Active", null)]));
        Assert.Equal("Active", reset.Values.Single(v => v.Value == "Active").Label);
        Assert.Equal(2, reset.Values.Single(v => v.Value == "Active").SubStatuses.Count);
    }
}
