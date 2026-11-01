using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Accounting;
using Workbench.Application.Collaboration;
using Workbench.Application.Files;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Application.Security;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for accounts payable, entered through the real services so it posts like real work:
/// ACME's TIN, retainage, funding history and flow-downs on SUB-1001-01; an ACME subcontract bill linked to the seeded
/// "Subcontractor invoice 1" ODC (entered by pm@, approved and posted by dev@, 5% retainage, left open for payment); a
/// PRIMA bill for software (overhead) and one completed payment run that paid it by check. Runs once, after the contract
/// detail seed.
/// </summary>
internal static class PurchasingSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<ApBill>().AnyAsync(cancellationToken) || !await db.Set<Account>().AnyAsync(cancellationToken))
        {
            return;
        }

        var acme = await db.Set<Vendor>().FirstOrDefaultAsync(v => v.Code == "ACME", cancellationToken);
        var prima = await db.Set<Vendor>().FirstOrDefaultAsync(v => v.Code == "PRIMA", cancellationToken);
        var sub = await db.Set<Subcontract>().FirstOrDefaultAsync(s => s.Number == "SUB-1001-01", cancellationToken);
        var odc = await db.Set<OdcEntry>().FirstOrDefaultAsync(o => o.SubcontractId != null && o.Status == OdcStatus.Submitted, cancellationToken);
        var software = await db.Set<Account>().FirstOrDefaultAsync(a => a.Number == "6230", cancellationToken);
        var ops = await db.Set<OrgUnit>().FirstOrDefaultAsync(o => o.Code == "OPS", cancellationToken);
        if (acme is null || prima is null || sub is null || odc is null || software is null || ops is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        acme.SocioEconomicCategories = "SDVOSB,VOSB";
        acme.ContactName = "Jordan Price";
        acme.RemitToAddress = "Acme Federal\n1200 Wilson Blvd, Suite 400\nArlington, VA 22209";
        acme.CreatedAtUtc = now;
        prima.Form1099Box = PurchasingCatalog.DefaultForm1099Box;
        prima.SocioEconomicCategories = "WOSB";
        prima.RemitToAddress = "Primavera Reseller LLC\nPO Box 4410\nReston, VA 20195";
        prima.CreatedAtUtc = now;
        sub.RetainagePercent = 5m;
        db.Add(new SubcontractFundingAction
        {
            SubcontractId = sub.Id,
            Number = "M000",
            EffectiveDate = sub.PeriodStart,
            FundedChange = sub.FundedValue,
            CeilingChange = 0,
            Description = "Initial award (funding and ceiling at award)",
            CreatedBy = "pm@example.com",
            CreatedAtUtc = now.AddDays(-40),
        });
        var included = new HashSet<string> { "52.204-21", "52.219-8", "52.222-50", "252.204-7012", "252.204-7020" };
        db.AddRange(PurchasingCatalog.StandardFlowDowns.Select(c => new SubcontractFlowDown
        {
            SubcontractId = sub.Id,
            ClauseNumber = c.Clause,
            Title = c.Title,
            IsIncluded = included.Contains(c.Clause),
            Note = c.Clause == "252.204-7012" ? "CMMC Level 2 assessment on file" : null,
            UpdatedBy = "pm@example.com",
            UpdatedAtUtc = now,
        }));
        await db.SaveChangesAsync(cancellationToken);

        var user = new SeedUser();
        var kit = new Kit(services, user);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var period = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start <= today && p.End >= today && p.Status == PeriodStatus.Open, cancellationToken);
        if (period is null)
        {
            return;
        }

        user.Email = "pm@example.com";
        await kit.Vendors.SetTaxIdAsync(acme.Id, new SetVendorTaxIdRequest("47-1234821"), cancellationToken);
        await kit.Vendors.SetTaxIdAsync(prima.Id, new SetVendorTaxIdRequest("58-9061177"), cancellationToken);

        // ACME's subcontract invoice, linked to the ODC already entered on project 1001.
        var acmeBill = await kit.Bills.CreateAsync(new SaveBillRequest(acme.Id, "ACME-2026-031", odc.IncurredOn,
            [new SaveBillLineRequest(BillLineKind.Subcontract, "Data migration engineering, first invoice", odc.Amount, SubcontractId: sub.Id, OdcEntryId: odc.Id)],
            Description: "SUB-1001-01 invoice 1"), cancellationToken);
        await kit.Bills.SubmitAsync(acmeBill.Bill.Id, new SubmitBillRequest(), cancellationToken);

        // PRIMA's software subscription is an overhead (G&A office and IT) purchase.
        var primaBill = await kit.Bills.CreateAsync(new SaveBillRequest(prima.Id, "PR-88214", period.Start,
            [new SaveBillLineRequest(BillLineKind.Account, "Scheduling software subscription, 12 seats", 2_400m, AccountId: software.Id, OrgUnitId: ops.Id)],
            Description: "Annual subscription"), cancellationToken);
        await kit.Bills.SubmitAsync(primaBill.Bill.Id, new SubmitBillRequest(), cancellationToken);

        user.Email = "dev@workbench.local";
        await kit.Bills.ApproveAsync(acmeBill.Bill.Id, cancellationToken);
        await kit.Bills.ApproveAsync(primaBill.Bill.Id, cancellationToken);

        user.Email = "pm@example.com";
        var run = await kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(today, PaymentKind.Check, [new PaymentRunItemRequest(primaBill.Bill.Id, 2_400m)]), cancellationToken);
        user.Email = "dev@workbench.local";
        await kit.Payments.ApproveRunAsync(run.Id, cancellationToken);
        await kit.Payments.RecordPaymentsAsync(run.Id, new RecordPaymentsRequest(today, [new PaymentReferenceRequest(run.Payments[0].Id, "10001")]), cancellationToken);
    }

    /// <summary>The purchasing services built by hand, running as <see cref="SeedUser"/> (no one is signed in at startup).</summary>
    private sealed class Kit
    {
        public Kit(IServiceProvider services, SeedUser user)
        {
            var factory = services.GetRequiredService<IWorkbenchDbContextFactory>();
            var clock = TimeProvider.System;
            var audit = new AuditRecorder(user, clock);
            var ledger = new GeneralLedger(audit, clock);
            var engine = new PostingEngine(factory, ledger, NullLogger<PostingEngine>.Instance);
            var access = new AccessControl(factory, user);
            var security = new SecurityEventLog(factory, user, new SeedSecurityContext(), clock, NullLogger<SecurityEventLog>.Instance);
            var options = Options.Create(new PurchasingOptions());
            var protector = new TaxIdProtector(services.GetRequiredService<IDataProtectionProvider>());
            var files = new FileStorage(services.GetRequiredService<IBlobStore>(), user, clock);
            Vendors = new VendorService(factory, audit, protector, files, security, clock);
            Bills = new ApBillService(factory, audit, ledger, engine, access, new NoNotifications(), options, clock, NullLogger<ApBillService>.Instance);
            Payments = new ApPaymentService(factory, audit, ledger, access, security, options, clock);
        }

        public VendorService Vendors { get; }
        public ApBillService Bills { get; }
        public ApPaymentService Payments { get; }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email { get; set; } = "";
    }

    private sealed class SeedSecurityContext : ISecurityEventContext
    {
        public string? IpAddress => null;
        public string? UserAgent => "seed";
    }

    private sealed class NoNotifications : INotificationPublisher
    {
        public Task<bool> PublishAsync(string recipientEmail, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> PublishAsync(IEnumerable<string?> recipientEmails, string kind, string title, string? body = null, string? link = null, Guid? sheetId = null, Guid? itemId = null) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
