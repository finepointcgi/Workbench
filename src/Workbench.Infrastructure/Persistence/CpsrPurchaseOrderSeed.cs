using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data: three more issued purchase orders (lab equipment and engineering support on project 1001, office furniture) that the government contracting module's
/// CPSR files and CAS disclosure sample build on, and that the funding runway of 1001 counts as committed. They are dated at the in-service date of the accounting module's
/// conference room AV system when that module made one (so the sample reads as it always did), else today. Runs once, after sourcing.
/// </summary>
internal static class CpsrPurchaseOrderSeed
{
    public static async Task SeedAsync(IWorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<PurchaseOrder>().AnyAsync(o => o.Lines.Any(l => l.Description == "Integration lab test equipment (spectrum, signal generators)"), cancellationToken))
        {
            return;
        }

        var acme = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Code == "ACME", cancellationToken);
        var prima = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Code == "PRIMA", cancellationToken);
        var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Code == "1001.01.04", cancellationToken);
        var account = await db.Set<Domain.Accounting.Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == "1520", cancellationToken);
        var ops = await db.Set<OrgUnit>().AsNoTracking().FirstOrDefaultAsync(o => o.Code == "OPS", cancellationToken);
        if (acme is null || prima is null || wbs is null || account is null || ops is null)
        {
            return;
        }

        using var scope = services.CreateScope();
        var assets = scope.ServiceProvider.GetService<IAssetSource>();
        var date = (assets is null ? null : await assets.InServiceDateAsync(db, "Conference room AV system", cancellationToken)) ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
        var user = new SeedUser { Email = "pm@example.com" };
        var numbers = await db.Set<PurchaseOrder>().AsNoTracking().Select(o => o.Number).ToListAsync(cancellationToken);
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
