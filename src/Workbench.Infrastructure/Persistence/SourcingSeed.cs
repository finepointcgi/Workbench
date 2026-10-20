using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for sourcing, entered through the real services: pm@ asks PRIMA, DIGI and ACME to quote six
/// lab network switches and three rack UPS units for 1001.01.04, records their three quotes, and awards both lines to
/// PRIMA (lowest price on both; price reasonableness on file) as an issued purchase order, which PRIMA acknowledges.
/// Jordan Reyes (jordan.reyes@primavera.example) is PRIMA's supplier portal contact; the password comes from
/// <c>SupplierPortal:DemoContactPassword</c> when it's configured (Development), else they use an emailed link. DIGI is
/// set up as an EDI partner (file drop folder "digi"). Runs once, after the procurement and materials seeds.
/// </summary>
internal static class SourcingSeed
{
    public const string DemoContactEmail = "jordan.reyes@primavera.example";

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<Rfq>().AnyAsync(cancellationToken))
        {
            return;
        }

        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => v.Code == "PRIMA" || v.Code == "DIGI" || v.Code == "ACME").ToDictionaryAsync(v => v.Code, cancellationToken);
        var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Code == "1001.01.04", cancellationToken);
        if (vendors.Count < 3 || wbs is null)
        {
            return;
        }

        var factory = (IWorkbenchDbContextFactory)services.GetService(typeof(IWorkbenchDbContextFactory))!;
        var clock = TimeProvider.System;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = new SeedUser { Email = "pm@example.com" };
        var audit = new AuditRecorder(user, clock);
        var access = new AccessControl(factory, user);
        var orders = new PurchaseOrderService(factory, audit, access, new NoNotifications(), clock);
        var mail = new SupplierPortalMail(new NullEmailSender(), new NotificationOptions(), NullLogger<SupplierPortalMail>.Instance);
        var rfqs = new RfqService(factory, audit, access, orders, mail, Options.Create(new SourcingOptions()), clock);

        var (prima, digi, acme) = (vendors["PRIMA"], vendors["DIGI"], vendors["ACME"]);
        var rfq = await rfqs.CreateAsync(new SaveRfqRequest(
            "Lab network refresh: switches and UPS",
            today.AddDays(7),
            [
                new SaveRfqLineRequest("24-port managed PoE switch, rack mount", 6, "EA", today.AddDays(30), 1_150m, WbsElementId: wbs.Id, OdcType: OdcCostType.Equipment),
                new SaveRfqLineRequest("Rack-mount UPS, 1500 VA, network card", 3, "EA", today.AddDays(30), 650m, WbsElementId: wbs.Id, OdcType: OdcCostType.Equipment),
            ],
            [prima.Id, digi.Id, acme.Id],
            Terms: "Net 30, FOB destination",
            ShipTo: "Workbench HQ lab, 1200 Wilson Blvd, Arlington VA",
            Instructions: "Quote new equipment only, with the manufacturer's warranty. Include any quantity discounts."), cancellationToken);
        rfq = await rfqs.IssueAsync(rfq.Rfq.Id, cancellationToken);
        var (switches, ups) = (rfq.Lines[0].Id, rfq.Lines[1].Id);

        await rfqs.RecordQuoteAsync(rfq.Rfq.Id, new SaveQuoteRequest(prima.Id, today, 10,
            [new SaveQuoteLineRequest(switches, 1_090m, [new PriceBreakDto(5, 1_060m)]), new SaveQuoteLineRequest(ups, 595m)],
            today.AddDays(30), "PR-Q-4471", "Net 15, FOB destination"), cancellationToken);
        await rfqs.RecordQuoteAsync(rfq.Rfq.Id, new SaveQuoteRequest(digi.Id, today, 21,
            [new SaveQuoteLineRequest(switches, 1_120m), new SaveQuoteLineRequest(ups, 640m)],
            today.AddDays(45), "DQ-20931"), cancellationToken);
        rfq = await rfqs.RecordQuoteAsync(rfq.Rfq.Id, new SaveQuoteRequest(acme.Id, today, 7,
            [new SaveQuoteLineRequest(switches, 1_175m), new SaveQuoteLineRequest(ups, 610m, LeadTimeDays: 14)],
            today.AddDays(20), "AF-117"), cancellationToken);

        var winner = rfq.Quotes.First(q => q.VendorId == prima.Id);
        rfq = await rfqs.AwardAsync(rfq.Rfq.Id, new AwardRfqRequest(
            winner.Lines.Select(l => new AwardLineRequest(l.RfqLineId, l.Id)).ToList(),
            "Adequate price competition: three vendors quoted both lines and the award is the lowest price on each, 8% under the estimate.",
            OrderDate: today, Receivers: ["analyst@example.com"], IssueOrders: true), cancellationToken);

        // PRIMA's portal contact, who has acknowledged the order with promised dates.
        var contact = new SupplierContact
        {
            VendorId = prima.Id,
            Email = DemoContactEmail,
            Name = "Jordan Reyes",
            InvitedBy = "pm@example.com",
            CreatedAtUtc = DateTime.UtcNow,
        };
        var configuration = services.GetService(typeof(IConfiguration)) as IConfiguration;
        if (configuration?["SupplierPortal:DemoContactPassword"] is { Length: >= SupplierSignIn.MinimumPasswordLength } password)
        {
            contact.PasswordHash = SupplierSignIn.HashForSeed(contact, password);
        }

        db.Set<SupplierContact>().Add(contact);
        var poId = rfq.Lines[0].PurchaseOrderId!.Value;
        var poLines = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => l.PurchaseOrderId == poId).OrderBy(l => l.LineNumber).ToListAsync(cancellationToken);
        var ack = new PurchaseOrderAcknowledgement
        {
            PurchaseOrderId = poId,
            VendorId = prima.Id,
            Revision = 0,
            Status = PurchaseOrderAcknowledgementStatus.Accepted,
            Source = SupplierDocumentSource.Portal,
            AcknowledgedBy = SupplierDocuments.PortalActor(contact),
            AcknowledgedAtUtc = DateTime.UtcNow,
        };
        foreach (var l in poLines)
        {
            ack.Lines.Add(new PurchaseOrderAcknowledgementLine { AcknowledgementId = ack.Id, PurchaseOrderLineId = l.Id, PromisedDate = today.AddDays(10) });
        }

        db.Set<PurchaseOrderAcknowledgement>().Add(ack);
        db.Set<EdiPartner>().Add(new EdiPartner
        {
            VendorId = digi.Id,
            InterchangeId = "DIGIPARTS",
            Address = "digi",
            UpdatedBy = "pm@example.com",
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
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
