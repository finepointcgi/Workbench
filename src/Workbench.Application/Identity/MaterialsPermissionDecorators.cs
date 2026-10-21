using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;

namespace Workbench.Application.Identity;

/// <summary>
/// Materials: reads are Materials, Finance or Admin; parts, inventory movements, BOMs, routings, work orders and MRP
/// change with the Materials role (or Admin); a standard cost revaluation (it posts to the GL) is Finance or Admin. Anyone
/// signed in may look up work orders and report their own shop floor labor. Labor cost (it reveals cost rates) and the
/// WIP report are Finance and Admin only.
/// </summary>
public static class MaterialsPermissionRegistration
{
    public static IServiceCollection AddWorkbenchMaterialsPermissions(this IServiceCollection services)
    {
        services.Decorate<IPartService, PermissionCheckingPartService>();
        services.Decorate<IInventoryService, PermissionCheckingInventoryService>();
        services.Decorate<IManufacturingService, PermissionCheckingManufacturingService>();
        services.Decorate<IMrpService, PermissionCheckingMrpService>();
        services.Decorate<IMaterialReportService, PermissionCheckingMaterialReportService>();
        return services;
    }
}

internal static class MaterialsAccess
{
    public static Task RequireReadAsync(this AccessControl access, CancellationToken ct) =>
        access.RequireAsync(p => p.Has(Permission.MaterialsView), "Only Materials, Finance and administrators can see inventory and manufacturing.", ct);

    public static Task RequireWriteAsync(this AccessControl access, CancellationToken ct) =>
        access.RequireAsync(p => p.Has(Permission.MaterialsManage), "Only the Materials role and administrators can change parts, inventory, BOMs, work orders and MRP.", ct);
}

public sealed class PermissionCheckingPartService(IPartService inner, AccessControl access) : IPartService
{
    public async Task<IReadOnlyList<PartSummaryDto>> ListAsync(PartQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<PartDto> GetAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GetAsync(partId, cancellationToken);
    }

    public async Task<PartDto> CreateAsync(SavePartRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<PartDto> UpdateAsync(Guid partId, SavePartRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.UpdateAsync(partId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListWarehousesAsync(cancellationToken);
    }

    public async Task<WarehouseDto> SaveWarehouseAsync(Guid? warehouseId, SaveWarehouseRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.SaveWarehouseAsync(warehouseId, request, cancellationToken);
    }

    public async Task<WarehouseDto> SaveBinAsync(Guid warehouseId, Guid? binId, SaveBinRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.SaveBinAsync(warehouseId, binId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<ForecastDto>> ListForecastsAsync(Guid? partId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListForecastsAsync(partId, cancellationToken);
    }

    public async Task<ForecastDto> AddForecastAsync(SaveForecastRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.AddForecastAsync(request, cancellationToken);
    }

    public async Task DeleteForecastAsync(Guid forecastId, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        await inner.DeleteForecastAsync(forecastId, cancellationToken);
    }

    public async Task<IReadOnlyList<MaterialRequirementDto>> ListRequirementsAsync(Guid? projectId = null, bool openOnly = true, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListRequirementsAsync(projectId, openOnly, cancellationToken);
    }

    public async Task<MaterialRequirementDto> AddRequirementAsync(SaveMaterialRequirementRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.AddRequirementAsync(request, cancellationToken);
    }

    public async Task<MaterialRequirementDto> CloseRequirementAsync(Guid requirementId, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.CloseRequirementAsync(requirementId, cancellationToken);
    }

    public async Task<MaterialsOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GetOptionsAsync(cancellationToken);
    }
}

public sealed class PermissionCheckingInventoryService(IInventoryService inner, AccessControl access) : IInventoryService
{
    public async Task<IReadOnlyList<InventoryBalanceDto>> ListBalancesAsync(BalanceQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListBalancesAsync(query, cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> ListTransactionsAsync(TransactionQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListTransactionsAsync(query, cancellationToken);
    }

    public async Task<IReadOnlyList<ReceivablePartLineDto>> ListReceivableLinesAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListReceivableLinesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LinkablePurchaseLineDto>> ListLinkableLinesAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListLinkableLinesAsync(cancellationToken);
    }

    public async Task LinkPurchaseOrderLineAsync(LinkPurchaseLineRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        await inner.LinkPurchaseOrderLineAsync(request, cancellationToken);
    }

    /// <summary>Materials (or Admin); purchasing's own check then applies (Finance, Materials or the order's receivers).</summary>
    public async Task<IReadOnlyList<InventoryTransactionDto>> ReceivePurchaseOrderAsync(ReceiveIntoInventoryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.ReceivePurchaseOrderAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> ReceiveGovernmentFurnishedAsync(GovernmentReceiptRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.ReceiveGovernmentFurnishedAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> IssueToProjectAsync(IssueToProjectRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.IssueToProjectAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> TransferAsync(TransferRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.TransferAsync(request, cancellationToken);
    }

    public async Task<InventoryTransactionDto> AdjustAsync(AdjustInventoryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.AdjustAsync(request, cancellationToken);
    }

    public async Task<InventoryTransactionDto?> CycleCountAsync(CycleCountRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.CycleCountAsync(request, cancellationToken);
    }

    public async Task<InventoryTransactionDto> ChangeStandardCostAsync(Guid partId, ChangeStandardCostRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.MaterialsCosting, "revalue inventory at a new standard cost", cancellationToken);
        return await inner.ChangeStandardCostAsync(partId, request, cancellationToken);
    }

    public async Task<TraceDto> TraceAsync(string? lot, string? serial, Guid? partId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.TraceAsync(lot, serial, partId, cancellationToken);
    }
}

public sealed class PermissionCheckingManufacturingService(IManufacturingService inner, AccessControl access) : IManufacturingService
{
    /// <summary>Labor cost reveals cost rates: only Finance and administrators see it (and the WIP balance that includes it).</summary>
    private async Task<WorkOrderDto> MaskAsync(WorkOrderDto dto, CancellationToken ct)
    {
        if (await access.HasAsync(Permission.LaborCostView, ct))
        {
            return dto;
        }

        return dto with
        {
            Cost = dto.Cost with { LaborCost = null, WipBalance = null, ActualUnitCost = null },
            Labor = dto.Labor.Select(l => l with { Cost = null }).ToList(),
        };
    }

    public async Task<IReadOnlyList<BomSummaryDto>> ListBomsAsync(Guid? partId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListBomsAsync(partId, cancellationToken);
    }

    public async Task<BomDto> GetBomAsync(Guid bomId, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GetBomAsync(bomId, cancellationToken);
    }

    public async Task<BomDto> SaveBomAsync(Guid? bomId, SaveBomRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.SaveBomAsync(bomId, request, cancellationToken);
    }

    public async Task<BomDto> ActivateBomAsync(Guid bomId, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.ActivateBomAsync(bomId, cancellationToken);
    }

    public async Task<BomDto> CopyBomAsync(Guid bomId, string newRevision, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.CopyBomAsync(bomId, newRevision, cancellationToken);
    }

    public async Task<IReadOnlyList<BomExplosionRowDto>> ExplodeAsync(Guid partId, decimal quantity, DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ExplodeAsync(partId, quantity, asOf, cancellationToken);
    }

    public async Task<IReadOnlyList<WhereUsedDto>> WhereUsedAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.WhereUsedAsync(partId, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkCenterDto>> ListWorkCentersAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListWorkCentersAsync(cancellationToken);
    }

    public async Task<WorkCenterDto> SaveWorkCenterAsync(Guid? workCenterId, SaveWorkCenterRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.SaveWorkCenterAsync(workCenterId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<RoutingDto>> ListRoutingsAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ListRoutingsAsync(partId, cancellationToken);
    }

    public async Task<RoutingDto> SaveRoutingAsync(Guid? routingId, SaveRoutingRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.SaveRoutingAsync(routingId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkOrderSummaryDto>> ListWorkOrdersAsync(WorkOrderQuery query, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return await inner.ListWorkOrdersAsync(query, cancellationToken);
    }

    public async Task<WorkOrderDto> GetWorkOrderAsync(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return await MaskAsync(await inner.GetWorkOrderAsync(workOrderId, cancellationToken), cancellationToken);
    }

    public async Task<WorkOrderDto> CreateWorkOrderAsync(SaveWorkOrderRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await MaskAsync(await inner.CreateWorkOrderAsync(request, cancellationToken), cancellationToken);
    }

    public async Task<WorkOrderDto> ReleaseAsync(Guid workOrderId, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await MaskAsync(await inner.ReleaseAsync(workOrderId, cancellationToken), cancellationToken);
    }

    public async Task<WorkOrderDto> IssueComponentsAsync(Guid workOrderId, IssueComponentsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await MaskAsync(await inner.IssueComponentsAsync(workOrderId, request, cancellationToken), cancellationToken);
    }

    /// <summary>Materials and Admin report for anyone; everyone else only their own labor.</summary>
    public async Task<WorkOrderDto> ReportLaborAsync(Guid workOrderId, ReportLaborRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (!await access.HasAsync(Permission.MaterialsManage, cancellationToken))
        {
            var me = await access.GetEmployeeIdAsync(cancellationToken);
            if (me is null || (request?.EmployeeId is { } other && other != me))
            {
                await access.RequireWriteAsync(cancellationToken);
            }

            request = request! with { EmployeeId = me };
        }

        return await MaskAsync(await inner.ReportLaborAsync(workOrderId, request!, cancellationToken), cancellationToken);
    }

    public async Task<WorkOrderDto> ScrapAsync(Guid workOrderId, ScrapRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await MaskAsync(await inner.ScrapAsync(workOrderId, request, cancellationToken), cancellationToken);
    }

    public async Task<WorkOrderDto> CompleteAsync(Guid workOrderId, CompleteWorkOrderRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await MaskAsync(await inner.CompleteAsync(workOrderId, request, cancellationToken), cancellationToken);
    }

    public async Task<WorkOrderDto> CloseAsync(Guid workOrderId, CloseWorkOrderRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await MaskAsync(await inner.CloseAsync(workOrderId, request, cancellationToken), cancellationToken);
    }

    public async Task<WorkOrderDto> CancelAsync(Guid workOrderId, CancelWorkOrderRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await MaskAsync(await inner.CancelAsync(workOrderId, request, cancellationToken), cancellationToken);
    }
}

public sealed class PermissionCheckingMrpService(IMrpService inner, AccessControl access) : IMrpService
{
    public async Task<MrpRunDto> RunAsync(RunMrpRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.RunAsync(request, cancellationToken);
    }

    public async Task<MrpRunDto?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GetLatestAsync(cancellationToken);
    }

    public async Task<FirmResultDto> FirmAsync(FirmPlannedOrdersRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireWriteAsync(cancellationToken);
        return await inner.FirmAsync(request, cancellationToken);
    }
}

public sealed class PermissionCheckingMaterialReportService(IMaterialReportService inner, AccessControl access, ProjectAccess projects) : IMaterialReportService
{
    public async Task<InventoryValuationDto> ValuationAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.ValuationAsync(cancellationToken);
    }

    public async Task<InventoryAgingDto> AgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.AgingAsync(asOf, cancellationToken);
    }

    public async Task<IReadOnlyList<GfmRowDto>> GovernmentFurnishedAsync(Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireReadAsync(cancellationToken);
        return await inner.GovernmentFurnishedAsync(projectId, cancellationToken);
    }

    public async Task<WipReportDto> WipAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.MaterialsCosting, "see work-in-process cost (it includes labor cost)", cancellationToken);
        return await inner.WipAsync(cancellationToken);
    }

    /// <summary>Materials, Finance, Admin, or whoever may read the project's financials (its manager).</summary>
    public async Task<ProjectMaterialCostDto> ProjectMaterialCostAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (!await access.HasAsync(Permission.MaterialsView, cancellationToken))
        {
            await projects.RequireProjectFinancialsAsync(projectId, "see the project's material cost", cancellationToken);
        }

        return await inner.ProjectMaterialCostAsync(projectId, cancellationToken);
    }
}
