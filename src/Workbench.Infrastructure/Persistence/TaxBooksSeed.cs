using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Assets;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Domain.Assets;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for tax books and transfers (the CPSR files and the CAS disclosure statement on the purchase orders below are seeded by the government contracting module): federal books on the
/// engineering file server (5-year MACRS with 40% bonus) and the conference room AV system (7-year), a move of the AV
/// system to another room and custodian, three more issued purchase orders for the CPSR sample. Runs once, after the
/// asset, procurement and company seeds.
/// </summary>
internal static class TaxBooksSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<AssetTaxBook>().AnyAsync(cancellationToken))
        {
            return;
        }

        var server = await db.Set<FixedAsset>().AsNoTracking().FirstOrDefaultAsync(a => a.Description == "Engineering file server", cancellationToken);
        var av = await db.Set<FixedAsset>().AsNoTracking().FirstOrDefaultAsync(a => a.Description == "Conference room AV system", cancellationToken);
        var acme = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Code == "ACME", cancellationToken);
        var prima = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Code == "PRIMA", cancellationToken);
        var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Code == "1001.01.04", cancellationToken);
        var account = await db.Set<Domain.Accounting.Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == "1520", cancellationToken);
        var ops = await db.Set<OrgUnit>().AsNoTracking().FirstOrDefaultAsync(o => o.Code == "OPS", cancellationToken);
        if (server is null || av is null || acme is null || prima is null || wbs is null || account is null || ops is null)
        {
            return;
        }

        var user = new SeedUser { Email = "pm@example.com" };
        var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
        var clock = TimeProvider.System;
        var audit = new AuditRecorder(user, clock);
        var assetOptions = Options.Create(new AssetOptions());
        var assets = new AssetService(factory, audit, new GeneralLedger(audit, clock), assetOptions, clock);
        var books = new AssetTaxBookService(factory, audit, assetOptions, clock);

        // Tax books: the book (GAAP) depreciation stays on the asset; these are for the return and the provision.
        await books.SaveBookAsync(server.Id, new SaveAssetTaxBookRequest(AssetBookKind.Federal, TaxMethod.MacrsGds, 5, TaxConventionKind.HalfYear, BonusPercent: 40m,
            Notes: "40% bonus elected; state decouples from bonus."), cancellationToken);
        await books.SaveBookAsync(server.Id, new SaveAssetTaxBookRequest(AssetBookKind.State, TaxMethod.MacrsGds, 5, TaxConventionKind.HalfYear), cancellationToken);
        await books.SaveBookAsync(av.Id, new SaveAssetTaxBookRequest(AssetBookKind.Federal, TaxMethod.MacrsGds, 7, TaxConventionKind.HalfYear, Section179Amount: 5_000m), cancellationToken);

        // A transfer (location and custodian only, so nothing posts).
        var custodian = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Email == "analyst@example.com", cancellationToken);
        await assets.TransferAsync(av.Id, new TransferAssetRequest(av.InServiceDate.AddDays(10), "HQ conference room B", custodian?.Id, av.OrgUnitId, av.ProjectId,
            "Moved to the larger room for program reviews"), cancellationToken);

        // Purchase orders for the CPSR sample (the module adds the procurement files).
        var numbers = await db.Set<PurchaseOrder>().AsNoTracking().Select(o => o.Number).ToListAsync(cancellationToken);
        var date = av.InServiceDate;
        PurchaseOrder Order(Vendor vendor, string description, decimal amount, bool government)
        {
            var next = numbers.Select(n => n.StartsWith("PO-", StringComparison.Ordinal) && int.TryParse(n[3..], out var i) ? i : 0).DefaultIfEmpty(0).Max() + 1;
            var number = $"PO-{next:D6}";
            numbers.Add(number);
            var po = new PurchaseOrder
            {
                Number = number,
                VendorId = vendor.Id,
                OrderDate = date,
                Status = PurchaseOrderStatus.Issued,
                Total = amount,
                Terms = "Net 30",
                CreatedBy = user.Email,
                CreatedAtUtc = DateTime.UtcNow,
                IssuedBy = user.Email,
                IssuedAtUtc = DateTime.UtcNow,
            };
            po.Lines.Add(new PurchaseOrderLine
            {
                PurchaseOrderId = po.Id,
                LineNumber = 1,
                Description = description,
                Quantity = 1,
                UnitPrice = amount,
                Amount = amount,
                ProjectId = government ? wbs.ProjectId : null,
                WbsElementId = government ? wbs.Id : null,
                OdcType = government ? OdcType.Equipment : null,
                AccountId = government ? null : account.Id,
                OrgUnitId = government ? null : ops.Id,
                IsService = false,
            });
            return po;
        }

        var lab = Order(acme, "Integration lab test equipment (spectrum, signal generators)", 185_000m, government: true);
        var services2 = Order(acme, "Systems engineering support, base year", 420_000m, government: true);
        var furniture = Order(prima, "Office furniture for suite 410", 12_500m, government: false);
        await using (var write = await factory.CreateAsync(cancellationToken))
        {
            write.Set<PurchaseOrder>().AddRange(lab, services2, furniture);
            await write.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }
}
