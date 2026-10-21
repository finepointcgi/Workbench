using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Companies;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;
using Workbench.Domain.Materials;

namespace Workbench.Application.Identity;

/// <summary>
/// Sales orders and shipping: orders are read by Materials, Finance, Admin and Business development, and changed by
/// Materials, Finance and Admin; picking, packing, shipping, the DD250 and WAWF are Materials, Finance and Admin; the
/// delivery invoice is Finance and Admin. Orders and shipments in a company the user can't see are not found.
/// </summary>
public static class SalesShippingPermissionRegistration
{
    public static IServiceCollection AddWorkbenchSalesShippingPermissions(this IServiceCollection services)
    {
        services.Decorate<ISalesOrderService, PermissionCheckingSalesOrderService>();
        services.Decorate<IShippingService, PermissionCheckingShippingService>();
        return services;
    }
}

internal static class SalesAccess
{
    public static bool CanReadOrders(PermissionSet p) => p.Has(Permission.SalesOrdersView);

    public static Task RequireOrderReadAsync(this AccessControl access, CancellationToken ct) =>
        access.RequireAsync(CanReadOrders, "Only Materials, Finance, Business development and administrators can see sales orders.", ct);

    public static Task RequireShippingAsync(this AccessControl access, CancellationToken ct) =>
        access.RequireAsync(p => p.Has(Permission.SalesOrdersManage), "Only the Materials role, Finance and administrators can enter sales orders and ship them.", ct);
}

public sealed class PermissionCheckingSalesOrderService(ISalesOrderService inner, AccessControl access, CompanyGuard companies) : ISalesOrderService
{
    private const string Kind = "Sales order";

    public async Task<IReadOnlyList<SalesOrderSummaryDto>> ListAsync(SalesOrderQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireOrderReadAsync(cancellationToken);
        return await companies.FilterAsync<SalesOrder, SalesOrderSummaryDto>(await inner.ListAsync(query, cancellationToken), o => o.Id, cancellationToken);
    }

    public async Task<SalesOrderDto> GetAsync(Guid salesOrderId, CancellationToken cancellationToken = default)
    {
        await access.RequireOrderReadAsync(cancellationToken);
        await companies.EnsureAsync<SalesOrder>(salesOrderId, Kind, cancellationToken);
        return await inner.GetAsync(salesOrderId, cancellationToken);
    }

    public async Task<SalesOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireOrderReadAsync(cancellationToken);
        return await inner.GetOptionsAsync(cancellationToken);
    }

    public async Task<SalesOrderDto> CreateAsync(SaveSalesOrderRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireShippingAsync(cancellationToken);
        var order = await inner.CreateAsync(request, cancellationToken);
        if (request.ContractId is null && request.ProjectId is null)
        {
            // A commercial order: it belongs to the company the user is working in (otherwise the contract's or project's).
            await companies.StampNewAsync<SalesOrder>(order.Summary.Id, cancellationToken);
        }

        return order;
    }

    public async Task<SalesOrderDto> UpdateAsync(Guid salesOrderId, SaveSalesOrderRequest request, CancellationToken cancellationToken = default)
    {
        await RequireChangeAsync(salesOrderId, cancellationToken);
        return await inner.UpdateAsync(salesOrderId, request, cancellationToken);
    }

    public async Task<SalesOrderDto> AddLinesFromClinsAsync(Guid salesOrderId, AddClinLinesRequest request, CancellationToken cancellationToken = default)
    {
        await RequireChangeAsync(salesOrderId, cancellationToken);
        return await inner.AddLinesFromClinsAsync(salesOrderId, request, cancellationToken);
    }

    public async Task<SalesOrderDto> ReleaseAsync(Guid salesOrderId, CancellationToken cancellationToken = default)
    {
        await RequireChangeAsync(salesOrderId, cancellationToken);
        return await inner.ReleaseAsync(salesOrderId, cancellationToken);
    }

    public async Task<SalesOrderDto> SetHoldAsync(Guid salesOrderId, SetHoldRequest request, CancellationToken cancellationToken = default)
    {
        await RequireChangeAsync(salesOrderId, cancellationToken);
        if (request.OnCreditHold != (await inner.GetAsync(salesOrderId, cancellationToken)).Summary.OnCreditHold)
        {
            await access.RequireAsync(Permission.SalesCreditHold, "put orders on or off credit hold", cancellationToken);
        }

        return await inner.SetHoldAsync(salesOrderId, request, cancellationToken);
    }

    public async Task<SalesOrderDto> CloseAsync(Guid salesOrderId, CancellationToken cancellationToken = default)
    {
        await RequireChangeAsync(salesOrderId, cancellationToken);
        return await inner.CloseAsync(salesOrderId, cancellationToken);
    }

    public async Task<SalesOrderDto> CancelAsync(Guid salesOrderId, CancelSalesOrderRequest request, CancellationToken cancellationToken = default)
    {
        await RequireChangeAsync(salesOrderId, cancellationToken);
        return await inner.CancelAsync(salesOrderId, request, cancellationToken);
    }

    private async Task RequireChangeAsync(Guid salesOrderId, CancellationToken ct)
    {
        await access.RequireShippingAsync(ct);
        await companies.EnsureAsync<SalesOrder>(salesOrderId, Kind, ct);
    }
}

public sealed class PermissionCheckingShippingService(IShippingService inner, AccessControl access, CompanyGuard companies) : IShippingService
{
    private const string Kind = "Shipment";

    public async Task<IReadOnlyList<PickableStockDto>> ListPickableStockAsync(Guid salesOrderLineId, CancellationToken cancellationToken = default)
    {
        await access.RequireShippingAsync(cancellationToken);
        return await inner.ListPickableStockAsync(salesOrderLineId, cancellationToken);
    }

    public async Task<PickListDto> CreatePickListAsync(Guid salesOrderId, CreatePickListRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireShippingAsync(cancellationToken);
        await companies.EnsureAsync<SalesOrder>(salesOrderId, "Sales order", cancellationToken);
        return await inner.CreatePickListAsync(salesOrderId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<PickListSummaryDto>> ListPickListsAsync(bool openOnly = true, CancellationToken cancellationToken = default)
    {
        await access.RequireShippingAsync(cancellationToken);
        return await companies.FilterAsync<SalesOrder, PickListSummaryDto>(await inner.ListPickListsAsync(openOnly, cancellationToken), p => p.SalesOrderId, cancellationToken);
    }

    public async Task<PickListDto> GetPickListAsync(Guid pickListId, CancellationToken cancellationToken = default)
    {
        await access.RequireShippingAsync(cancellationToken);
        var pick = await inner.GetPickListAsync(pickListId, cancellationToken);
        await companies.EnsureAsync<SalesOrder>(pick.Summary.SalesOrderId, "Pick list", cancellationToken);
        return pick;
    }

    public async Task<PickListDto> ConfirmPickAsync(Guid pickListLineId, ConfirmPickRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireShippingAsync(cancellationToken);
        return await inner.ConfirmPickAsync(pickListLineId, request, cancellationToken);
    }

    public async Task<PickListDto> CancelPickListAsync(Guid pickListId, CancellationToken cancellationToken = default)
    {
        await GetPickListAsync(pickListId, cancellationToken);
        return await inner.CancelPickListAsync(pickListId, cancellationToken);
    }

    public async Task<ShipmentDto> CreateShipmentAsync(CreateShipmentRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireShippingAsync(cancellationToken);
        return await inner.CreateShipmentAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<ShipmentSummaryDto>> ListShipmentsAsync(ShipmentQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireShippingAsync(cancellationToken);
        return await companies.FilterAsync<Shipment, ShipmentSummaryDto>(await inner.ListShipmentsAsync(query, cancellationToken), s => s.Id, cancellationToken);
    }

    public async Task<ShipmentDto> GetShipmentAsync(Guid shipmentId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(shipmentId, cancellationToken);
        return await inner.GetShipmentAsync(shipmentId, cancellationToken);
    }

    public async Task<ShipmentDto> SaveShipmentAsync(Guid shipmentId, SaveShipmentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(shipmentId, cancellationToken);
        return await inner.SaveShipmentAsync(shipmentId, request, cancellationToken);
    }

    public async Task<ShipmentDto> ConfirmShipmentAsync(Guid shipmentId, ConfirmShipmentRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(shipmentId, cancellationToken);
        return await inner.ConfirmShipmentAsync(shipmentId, request, cancellationToken);
    }

    public async Task<ShipmentDto> CancelShipmentAsync(Guid shipmentId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(shipmentId, cancellationToken);
        return await inner.CancelShipmentAsync(shipmentId, cancellationToken);
    }

    public async Task<Dd250Dto> GetDd250Async(Guid shipmentId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(shipmentId, cancellationToken);
        return await inner.GetDd250Async(shipmentId, cancellationToken);
    }

    public async Task<ShippingFile> GetDd250PdfAsync(Guid shipmentId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(shipmentId, cancellationToken);
        return await inner.GetDd250PdfAsync(shipmentId, cancellationToken);
    }

    public async Task<WawfValidationDto> ValidateWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default)
    {
        await RequireAsync(shipmentId, cancellationToken);
        return await inner.ValidateWawfAsync(shipmentId, documentType, cancellationToken);
    }

    public async Task<ShippingFile> ExportWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default)
    {
        await RequireAsync(shipmentId, cancellationToken);
        return await inner.ExportWawfAsync(shipmentId, documentType, cancellationToken);
    }

    public async Task<ShipmentDto> SubmitWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default)
    {
        await RequireAsync(shipmentId, cancellationToken);
        return await inner.SubmitWawfAsync(shipmentId, documentType, cancellationToken);
    }

    public async Task<ShipmentDto> SetWawfStatusAsync(Guid shipmentId, SetWawfStatusRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(shipmentId, cancellationToken);
        return await inner.SetWawfStatusAsync(shipmentId, request, cancellationToken);
    }

    public async Task<ShipmentDto> CreateInvoiceAsync(Guid shipmentId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.BillingInvoicesManage, "create delivery invoices", cancellationToken);
        await companies.EnsureAsync<Shipment>(shipmentId, Kind, cancellationToken);
        return await inner.CreateInvoiceAsync(shipmentId, cancellationToken);
    }

    private async Task RequireAsync(Guid shipmentId, CancellationToken ct)
    {
        await access.RequireShippingAsync(ct);
        await companies.EnsureAsync<Shipment>(shipmentId, Kind, ct);
    }
}
