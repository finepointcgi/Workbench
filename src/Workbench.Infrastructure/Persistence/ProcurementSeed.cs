using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for procure-to-pay, entered through the real services: analyst@ requisitions four rugged field
/// laptops on 1001.01.04 (pm@ approves as project manager), pm@ orders them from PRIMA on a PO, receives two, and bills
/// those two (a three-way match), which dev@ approves and posts. The other two stay open as a commitment. Runs once,
/// after the purchasing seed.
/// </summary>
internal static class ProcurementSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<PurchaseRequisition>().AnyAsync(cancellationToken))
        {
            return;
        }

        var prima = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Code == "PRIMA", cancellationToken);
        var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Code == "1001.01.04", cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= today && p.End >= today && p.Status == PeriodStatus.Open, cancellationToken);
        if (prima is null || wbs is null || period is null)
        {
            return;
        }

        var user = new SeedUser();
        var kit = new Kit(services, user);

        user.Email = "analyst@example.com";
        var req = await kit.Requisitions.CreateAsync(new SaveRequisitionRequest(
            "Field test team needs ruggedized laptops for the site surveys",
            [new SaveRequisitionLineRequest("Rugged field laptop, 14\", 32 GB", 4, 2_450m, WbsElementId: wbs.Id, OdcType: OdcCostType.Equipment,
                NeededBy: period.End, SuggestedVendorId: prima.Id)]), cancellationToken);
        await kit.Requisitions.SubmitAsync(req.Requisition.Id, cancellationToken);

        user.Email = "pm@example.com";
        await kit.Requisitions.ApproveAsync(req.Requisition.Id, new RequisitionDecisionRequest("Approved for the site surveys"), cancellationToken);
        var po = await kit.Orders.CreateFromRequisitionsAsync(new CreatePurchaseOrderFromRequisitionsRequest(
            [req.Requisition.Id], prima.Id, period.Start, Terms: "Net 15, FOB destination", ShipTo: "Workbench HQ, 1200 Wilson Blvd, Arlington VA",
            Receivers: ["analyst@example.com"]), cancellationToken);
        po = await kit.Orders.IssueAsync(po.Order.Id, cancellationToken);
        po = await kit.Orders.ReceiveAsync(po.Order.Id, new ReceiveRequest(period.Start.AddDays(3), [new ReceiveLineRequest(po.Lines[0].Id, 2)], "First two units"), cancellationToken);

        var bill = await kit.Bills.CreateAsync(new SaveBillRequest(prima.Id, "PR-90102", period.Start.AddDays(4),
            [new SaveBillLineRequest(BillLineKind.ProjectCost, "", 0, PurchaseOrderLineId: po.Lines[0].Id, Quantity: 2)],
            Description: $"{po.Order.Number}: first two laptops"), cancellationToken);
        await kit.Bills.SubmitAsync(bill.Bill.Id, new SubmitBillRequest(), cancellationToken);

        user.Email = "dev@workbench.local";
        await kit.Bills.ApproveAsync(bill.Bill.Id, cancellationToken);
    }

    /// <summary>The procurement services built by hand (no permission decorators), running as <see cref="SeedUser"/>.</summary>
    private sealed class Kit
    {
        public Kit(IServiceProvider services, SeedUser user)
        {
            var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
            var clock = TimeProvider.System;
            var audit = new AuditRecorder(user, clock);
            var ledger = new GeneralLedger(audit, clock);
            var engine = new PostingEngine(factory, ledger, NullLogger<PostingEngine>.Instance);
            var access = new AccessControl(factory, user);
            var options = Options.Create(new PurchasingOptions());
            var notifications = new NoNotifications();
            Requisitions = new RequisitionService(factory, audit, access, new RequisitionAccess(factory, access), notifications, options, clock);
            Orders = new PurchaseOrderService(factory, audit, access, notifications, clock);
            Bills = new ApBillService(factory, audit, ledger, engine, access, notifications, options, clock, NullLogger<ApBillService>.Instance);
        }

        public RequisitionService Requisitions { get; }
        public PurchaseOrderService Orders { get; }
        public ApBillService Bills { get; }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }

    private sealed class NoNotifications : INotificationPublisher
    {
        public Task<bool> PublishAsync(string recipientEmail, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> PublishAsync(IEnumerable<string?> recipientEmails, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
