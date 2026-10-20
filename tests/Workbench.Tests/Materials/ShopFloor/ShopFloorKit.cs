using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using static Workbench.Tests.Materials.MaterialsKit;

namespace Workbench.Tests.Materials.ShopFloor;

/// <summary>Shop floor services and helpers on top of <see cref="MaterialsKit"/>.</summary>
internal static class ShopFloorKit
{
    public static IKittingService Kits(this MaterialsKit kit) => kit.Kit.Get<IKittingService>();

    public static IBackflushService Backflush(this MaterialsKit kit) => kit.Kit.Get<IBackflushService>();

    public static ILandedCostService LandedCost(this MaterialsKit kit) => kit.Kit.Get<ILandedCostService>();

    public static IPhysicalInventoryService Counts(this MaterialsKit kit) => kit.Kit.Get<IPhysicalInventoryService>();

    public static ICapacitySchedulingService Capacity(this MaterialsKit kit) => kit.Kit.Get<ICapacitySchedulingService>();

    /// <summary>An account id by number, including ones added after the kit was made (landed cost clearing 1406).</summary>
    public static async Task<Guid> AccountIdAsync(this MaterialsKit kit, string number)
    {
        await using var db = await kit.Kit.Factory.CreateAsync();
        return await db.Set<Account>().Where(a => a.Number == number).Select(a => a.Id).SingleAsync();
    }

    public static async Task<decimal> GlAsync(this MaterialsKit kit, string number)
    {
        var id = await kit.AccountIdAsync(number);
        return (await kit.EntriesAsync()).SelectMany(e => e.Lines).Where(l => l.AccountId == id).Sum(l => l.Debit - l.Credit);
    }

    /// <summary>A make part with a released stock work order: components (part, quantity per, operation) and one routing operation per work center given.</summary>
    public static async Task<WorkOrderDto> ReleasedOrderAsync(
        this MaterialsKit kit, string number, decimal quantity, (Guid Part, decimal Per, int? Op)[] components, (int Seq, Guid Center, decimal Setup, decimal Run)[]? operations = null,
        DateOnly? start = null, DateOnly? due = null, Guid? wbs = null)
    {
        var part = await kit.PartAsync(number, PartSourceKind.Make);
        await kit.Manufacturing.SaveBomAsync(null, new SaveBomRequest(part.Id, "A",
            components.Select(c => new SaveBomComponentRequest(c.Part, c.Per, OperationSequence: c.Op)).ToList()));
        if (operations is { Length: > 0 })
        {
            await kit.Manufacturing.SaveRoutingAsync(null, new SaveRoutingRequest(part.Id, "A",
                operations.Select(o => new SaveRoutingOperationRequest(o.Seq, $"Op {o.Seq}", o.Center, o.Setup, o.Run)).ToList()));
        }

        var wo = await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(part.Id, quantity, start ?? Day, due ?? (start ?? Day).AddDays(10), WbsElementId: wbs));
        return await kit.Manufacturing.ReleaseAsync(wo.Summary.Id);
    }
}
