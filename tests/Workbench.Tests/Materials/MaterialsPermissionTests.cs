using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.Materials.MaterialsKit;

namespace Workbench.Tests.Materials;

public sealed class MaterialsPermissionTests
{
    [Fact]
    public async Task Materials_changes_finance_reads_and_everyone_else_is_refused()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var part = await kit.PartAsync("WIDGET");

        kit.As(Finance);
        Assert.Single(await kit.Parts.ListAsync(new PartQuery()));
        await kit.Reports.ValuationAsync();
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.PartAsync("NOPE"));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Mrp.RunAsync(new RunMrpRequest()));

        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Parts.ListAsync(new PartQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Inventory.ListBalancesAsync(new BalanceQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Reports.GovernmentFurnishedAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Inventory.AdjustAsync(new AdjustInventoryRequest(part.Id, kit.Main.Id, kit.A1, 1, Day, "Found")));

        // Work-in-process cost includes labor cost: Finance and administrators only.
        kit.As(MaterialsUser);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Reports.WipAsync());
        kit.As(Admin);
        await kit.Reports.WipAsync();
        await kit.PartAsync("ADMIN-OK");

        // The project's manager sees its material cost; another employee doesn't.
        kit.As(Owner);
        await kit.Reports.ProjectMaterialCostAsync(kit.Kit.Fx.Project1);
        kit.As(Editor);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Reports.ProjectMaterialCostAsync(kit.Kit.Fx.Project1));
    }

    [Fact]
    public async Task Employees_report_their_own_shop_labor_only_and_never_see_labor_cost()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var leg = await kit.PartAsync("LEG");
        var table = await kit.PartAsync("TABLE", PartSourceKind.Make);
        await kit.Manufacturing.SaveBomAsync(null, new SaveBomRequest(table.Id, "A", [new SaveBomComponentRequest(leg.Id, 4)]));
        var center = await kit.Manufacturing.SaveWorkCenterAsync(null, new SaveWorkCenterRequest("WOOD", "Wood shop"));
        await kit.Manufacturing.SaveRoutingAsync(null, new SaveRoutingRequest(table.Id, "A", [new SaveRoutingOperationRequest(10, "Assemble", center.Id, 0, 1)]));
        var wo = await kit.Manufacturing.ReleaseAsync((await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(table.Id, 1, Day, Day))).Summary.Id);

        kit.As(Editor);
        var mine = await kit.Manufacturing.ReportLaborAsync(wo.Summary.Id, new ReportLaborRequest(wo.Operations[0].Id, Day, 1.5m));
        var report = Assert.Single(mine.Labor);
        Assert.Equal(("Alex Rivera", 1.5m, (decimal?)null), (report.Employee, report.Hours, report.Cost));
        Assert.Null(mine.Cost.LaborCost);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            kit.Manufacturing.ReportLaborAsync(wo.Summary.Id, new ReportLaborRequest(wo.Operations[0].Id, Day, 1m, EmployeeId: kit.MaterialsEmployeeId)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(1, Day)));

        kit.As(Finance);
        Assert.Equal(131.25m, (await kit.Manufacturing.GetWorkOrderAsync(wo.Summary.Id)).Cost.LaborCost);
    }

    [Fact]
    public void The_materials_role_is_in_every_role_list()
    {
        Assert.Contains(OrgRoles.Materials, OrgRoleChecks.All);
        Assert.True(OrgRoleChecks.Everything.HasFlag(OrgRoles.Materials));
        Assert.True(OrgRoles.Materials.IsMaterials());
        Assert.True(OrgRoles.Admin.IsMaterials());
        Assert.False(OrgRoles.Finance.IsMaterials());
        Assert.True(OrgRoles.Finance.CanViewMaterials());
        Assert.False(OrgRoles.ProjectManager.CanViewMaterials());
        Assert.Equal("Materials", OrgRoleChecks.Label(OrgRoles.Materials));
    }
}
