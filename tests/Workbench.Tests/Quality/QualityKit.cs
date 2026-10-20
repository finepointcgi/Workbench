using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Quality;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Materials;

namespace Workbench.Tests.Quality;

/// <summary>
/// <see cref="MaterialsKit"/> plus quality@ (a second member of the Materials role, so dispositions and engineering
/// changes have someone other than the submitter to approve them), and helpers for inspected parts.
/// </summary>
internal sealed class QualityKit : IAsyncDisposable
{
    public const string QualityUser = "quality@example.com";

    private QualityKit(MaterialsKit materials) => M = materials;

    public MaterialsKit M { get; }

    public IInspectionService Inspections => M.Kit.Get<IInspectionService>();
    public INonconformanceService Ncrs => M.Kit.Get<INonconformanceService>();
    public ICorrectiveActionService Cars => M.Kit.Get<ICorrectiveActionService>();
    public IQualityFileService Files => M.Kit.Get<IQualityFileService>();
    public IEngineeringChangeService Changes => M.Kit.Get<IEngineeringChangeService>();

    public static async Task<QualityKit> CreateAsync()
    {
        var kit = new QualityKit(await MaterialsKit.CreateAsync());
        await using var db = await kit.M.Kit.Factory.CreateAsync();
        db.Set<AppUser>().Add(new AppUser { Email = QualityUser, DisplayName = "Quinn", IsMaterials = true });
        db.Set<Employee>().Add(new Employee
        {
            EmployeeNumber = "E10", Email = QualityUser, DisplayName = "Quinn Quality", OrgUnitId = kit.M.Kit.OrgId, HireDate = new DateOnly(2024, 1, 1),
        });
        await db.SaveChangesAsync();
        return kit;
    }

    public QualityKit As(string email)
    {
        M.As(email);
        return this;
    }

    /// <summary>A receiving plan: one measured characteristic (10 ± 0.1 mm) and a certificate of conformance.</summary>
    public Task<InspectionPlanDto> ReceivingPlanAsync(Guid partId, string code = "RCV", Sampling sampling = Sampling.Full, int sampleSize = 0, int acceptNumber = 0) =>
        Inspections.SavePlanAsync(null, new SaveInspectionPlanRequest(code, "Receiving inspection", partId, InspectionType.Receiving,
            [new SaveCharacteristicRequest("Length", CharacteristicType.Measured, 10m, 9.9m, 10.1m, "mm"), new SaveCharacteristicRequest("No visible damage")],
            sampling, sampleSize, acceptNumber, RequiredDocuments: ["Certificate of conformance"]));

    public static IReadOnlyList<RecordResultRequest> Results(InspectionDto inspection, decimal length = 10m, bool visual = true) =>
        inspection.Characteristics.Select(c => c.Kind == CharacteristicType.Measured
            ? new RecordResultRequest(c.Id, MeasuredValue: length)
            : new RecordResultRequest(c.Id, Passed: visual)).ToList();

    public async Task AttachCertAsync(Guid inspectionId) =>
        await Files.AttachAsync(QualityFileOwner.Inspection, inspectionId, "Certificate of conformance", "coc.pdf", new MemoryStream("%PDF-1.4 cert"u8.ToArray()));

    /// <summary>Receives <paramref name="quantity"/> on a new PO at <paramref name="price"/> and returns the pending inspection it opened.</summary>
    public async Task<(PurchaseOrderAndLine Order, InspectionDto Inspection)> ReceiveForInspectionAsync(Guid partId, decimal quantity, decimal price, string? lot = null)
    {
        var po = await M.PartOrderAsync(partId, quantity, price);
        await M.Inventory.ReceivePurchaseOrderAsync(new ReceiveIntoInventoryRequest(po.Order.Id, MaterialsKit.Day, [new ReceiveIntoInventoryLine(po.Lines[0].Id, quantity, Lot: lot)]));
        var pending = (await Inspections.ListAsync(new InspectionQuery(PartId: partId))).Single(i => i.Status == InspectionState.Pending);
        return (new PurchaseOrderAndLine(po.Order.Id, po.Lines[0].Id), await Inspections.GetAsync(pending.Id));
    }

    public async Task<decimal> QuantityAsync(Guid partId, StockStatus status) =>
        (await M.Inventory.ListBalancesAsync(new BalanceQuery(partId))).Where(b => b.Status == status).Sum(b => b.Quantity);

    public async Task<T> DbAsync<T>(Func<Workbench.Application.IWorkbenchDbContext, Task<T>> read)
    {
        await using var db = await M.Kit.Factory.CreateAsync();
        return await read(db);
    }

    public ValueTask DisposeAsync() => M.DisposeAsync();
}

internal sealed record PurchaseOrderAndLine(Guid OrderId, Guid LineId);
