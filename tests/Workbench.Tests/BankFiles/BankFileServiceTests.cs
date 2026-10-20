using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.BankFiles;
using Workbench.Application.Notifications;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.BankFiles;
using Workbench.Contracts.Cash;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Approvals;
using Workbench.Domain.Cash;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Security;
using Workbench.Tests.Automation;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.BankFiles;

/// <summary>
/// Vendor ACH details, NACHA and positive pay from payment runs, and remittance advice, over the real registrations
/// (<see cref="PurchasingKit"/>, calls as finance@ unless switched; admin@ is the second approver).
/// </summary>
public sealed class BankFileServiceTests
{
    private const string PrimaAccount = "987654321";

    private static async Task<(PurchasingKit Kit, RecordingEmailSender Email, Guid BankAccountId)> KitAsync()
    {
        var email = new RecordingEmailSender();
        var kit = await PurchasingKit.CreateAsync(s => s.AddSingleton<IEmailSender>(email));
        kit.As(Finance);
        var bank = await kit.Get<IBankAccountService>().CreateAsync(new SaveBankAccountRequest("Operating", "First Capital Bank", "123456784321", "054001725", kit.Account("1010")));
        await kit.Get<IPaymentFileService>().SaveSettingsAsync(bank.Id, new SaveBankFileSettingsRequest(
            "054001725", "First Capital Bank", "1123456789", "Workbench Federal LLC", "1123456789", "Workbench Fed", "Vendor Pay"));
        return (kit, email, bank.Id);
    }

    private static async Task<VendorBankChangeDto> ApprovedBankAsync(PurchasingKit kit, VendorDto vendor, string account, string routing = "021000021", string? remit = null)
    {
        kit.As(Finance);
        var change = await kit.Get<IVendorBankService>().RequestChangeAsync(vendor.Id,
            new RequestVendorBankChangeRequest(vendor.Name, routing, account, BankAccountType.Checking, "Confirmed by phone at the number on file", remit));
        kit.As(Admin);
        var approved = await kit.Get<IVendorBankService>().ApproveChangeAsync(change.Id, new DecideVendorBankChangeRequest("Called back"));
        kit.As(Finance);
        return approved;
    }

    private static async Task<PaymentRunDto> AchRunAsync(PurchasingKit kit, params (Guid BillId, decimal Amount)[] items)
    {
        kit.As(Finance);
        var run = await kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Ach, items.Select(i => new PaymentRunItemRequest(i.BillId, i.Amount)).ToList()));
        kit.As(Admin);
        run = await kit.Payments.ApproveRunAsync(run.Id);
        kit.As(Finance);
        return run;
    }

    // ---- Vendor bank details ----

    [Fact]
    public async Task Bank_details_are_encrypted_at_rest_masked_everywhere_and_need_someone_else_to_approve()
    {
        var (kit, _, _) = await KitAsync();
        await using var _kit = kit;
        var service = kit.Get<IVendorBankService>();
        var change = await service.RequestChangeAsync(kit.Prima.Id,
            new RequestVendorBankChangeRequest("Primavera Reseller LLC", "021000021", PrimaAccount, BankAccountType.Checking, "W-9 and bank letter received; called to confirm"));

        Assert.Equal(BankChangeState.Pending, change.Status);
        Assert.Equal("••••4321", change.MaskedAccount);
        Assert.Equal("••••0021", change.MaskedRouting);
        Assert.False(change.CanDecide); // the requester never can

        await using (var db = await kit.Factory.CreateAsync())
        {
            var row = await db.Set<VendorBankChange>().SingleAsync();
            Assert.DoesNotContain(PrimaAccount, row.ProtectedAccountNumber, StringComparison.Ordinal);
            Assert.DoesNotContain("021000021", row.ProtectedRoutingNumber, StringComparison.Ordinal);
            var protector = kit.Get<VendorBankProtector>();
            Assert.Equal(PrimaAccount, protector.UnprotectAccount(row.ProtectedAccountNumber));
            Assert.Equal("021000021", protector.UnprotectRouting(row.ProtectedRoutingNumber));

            // The change is routed through the approval engine and audited without the numbers.
            Assert.True(await db.Set<ApprovalInstance>().AnyAsync(i => i.DocumentType == ApprovalDocumentTypes.VendorBankChange && i.DocumentId == change.Id));
            var audit = await db.Set<AuditEvent>().Where(a => a.EntityType == nameof(VendorBankChange)).ToListAsync();
            Assert.NotEmpty(audit);
            Assert.All(audit, a => Assert.DoesNotContain(PrimaAccount, (a.BeforeJson ?? "") + a.AfterJson + a.Reason, StringComparison.Ordinal));
        }

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.ApproveChangeAsync(change.Id, new DecideVendorBankChangeRequest()));
        Assert.False((await service.GetAsync(kit.Prima.Id)).HasAccount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestChangeAsync(kit.Prima.Id,
            new RequestVendorBankChangeRequest("Prima", "021000021", "1111", BankAccountType.Checking, "Again")));

        kit.As(Admin);
        var approved = await service.ApproveChangeAsync(change.Id, new DecideVendorBankChangeRequest("Called the vendor back"));
        Assert.Equal(BankChangeState.Approved, approved.Status);

        kit.As(Finance);
        var details = await service.GetAsync(kit.Prima.Id);
        Assert.True(details.HasAccount);
        Assert.Equal("••••4321", details.MaskedAccount);
        Assert.Equal(Admin, details.ApprovedBy);
        Assert.Null(details.PendingChange);

        var revealed = await service.RevealAsync(kit.Prima.Id);
        Assert.Equal(PrimaAccount, revealed.AccountNumber);
        await using (var db = await kit.Factory.CreateAsync())
        {
            var events = await db.Set<SecurityEvent>().Select(e => e.EventType).ToListAsync();
            Assert.Contains(BankFileSecurityEvents.VendorBankChangeRequested, events);
            Assert.Contains(BankFileSecurityEvents.VendorBankChanged, events);
            Assert.Contains(BankFileSecurityEvents.VendorBankRevealed, events);
        }
    }

    [Fact]
    public async Task A_rejected_change_leaves_the_old_details_and_bad_numbers_are_refused()
    {
        var (kit, _, _) = await KitAsync();
        await using var _kit = kit;
        await ApprovedBankAsync(kit, kit.Prima, PrimaAccount);
        var service = kit.Get<IVendorBankService>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestChangeAsync(kit.Prima.Id,
            new RequestVendorBankChangeRequest("Prima", "021000022", "555566667777", BankAccountType.Checking, "New bank")));

        var change = await service.RequestChangeAsync(kit.Prima.Id, new RequestVendorBankChangeRequest("Prima", "011000015", "555566667777", BankAccountType.Savings, "Email from vendor"));
        kit.As(Admin);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RejectChangeAsync(change.Id, new DecideVendorBankChangeRequest()));
        await service.RejectChangeAsync(change.Id, new DecideVendorBankChangeRequest("Email request; vendor didn't confirm by phone"));
        kit.As(Finance);
        var details = await service.GetAsync(kit.Prima.Id);
        Assert.Equal("••••4321", details.MaskedAccount);
        Assert.Equal(BankChangeState.Rejected, details.History[0].Status);
    }

    [Fact]
    public async Task Only_finance_sees_or_changes_bank_details()
    {
        var (kit, _, bankId) = await KitAsync();
        await using var _kit = kit;
        kit.As(Viewer);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => kit.Get<IVendorBankService>().GetAsync(kit.Prima.Id));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => kit.Get<IPaymentFileService>().GetSettingsAsync(bankId));
    }

    [Fact]
    public async Task The_inbox_decides_bank_changes_through_the_service()
    {
        var (kit, _, _) = await KitAsync();
        await using var _kit = kit;
        var change = await kit.Get<IVendorBankService>().RequestChangeAsync(kit.Bolt.Id,
            new RequestVendorBankChangeRequest("Bolt Consulting", "021000021", "44445555", BankAccountType.Checking, "Onboarding packet"));
        kit.As(Admin);
        var handler = kit.Get<IEnumerable<Application.Approvals.IApprovalDocumentHandler>>().Single(h => h.DocumentType == ApprovalDocumentTypes.VendorBankChange);
        await handler.ApproveAsync(change.Id, "ok", default);
        kit.As(Finance);
        Assert.True((await kit.Get<IVendorBankService>().GetAsync(kit.Bolt.Id)).HasAccount);
    }

    // ---- NACHA ----

    [Fact]
    public async Task An_ach_run_gives_a_nacha_file_stamps_trace_numbers_and_logs_each_download()
    {
        var (kit, _, bankId) = await KitAsync();
        await using var _kit = kit;
        await ApprovedBankAsync(kit, kit.Prima, PrimaAccount);
        await ApprovedBankAsync(kit, kit.Bolt, "44445555", routing: "011000015");
        var b1 = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "INV-1001", PurchasingKit.Day, [kit.OverheadLine(1_500m)]));
        var b2 = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "INV-1002", PurchasingKit.Day, [kit.OverheadLine(200m)]));
        var b3 = await kit.PostedBillAsync(new SaveBillRequest(kit.Bolt.Id, "B-77", PurchasingKit.Day, [kit.OverheadLine(250.75m)]));
        var run = await AchRunAsync(kit, (b1.Bill.Id, 1_500m), (b2.Bill.Id, 200m), (b3.Bill.Id, 250.75m));
        var files = kit.Get<IPaymentFileService>();

        var status = await files.GetRunFilesAsync(run.Id);
        Assert.Equal(bankId, status.DefaultBankAccountId);
        Assert.Empty(status.Problems);

        var file = await files.GenerateAsync(run.Id, new GeneratePaymentFileRequest(bankId, PaymentFileType.Nacha));
        var records = file.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.All(records, r => Assert.Equal(94, r.Length));
        Assert.Equal(2, file.EntryCount); // one credit per vendor
        Assert.Equal(1_950.75m, file.TotalAmount);
        var bolt = records.Single(r => r[0] == '6' && r.Contains("BOLT", StringComparison.Ordinal));
        Assert.Equal("01100001", bolt[3..11]);
        Assert.Equal("0000025075", bolt[29..39]);
        var prima = records.Single(r => r[0] == '6' && r.Contains("PRIMA", StringComparison.Ordinal));
        Assert.Equal("0000170000", prima[29..39]);
        Assert.Contains(records, r => r.StartsWith("705RMR*IV*INV-1001**1500.00\\RMR*IV*INV-1002**200.00\\", StringComparison.Ordinal));
        Assert.EndsWith("-A.ach", file.FileName, StringComparison.Ordinal);

        // Each vendor's payments carry its trace number until they're recorded.
        var after = await kit.Payments.GetRunAsync(run.Id);
        Assert.All(after.Payments, p => Assert.Matches("^05400172\\d{7}$", p.Reference!));
        Assert.Equal(2, after.Payments.Select(p => p.Reference).Distinct().Count());

        var second = await files.GenerateAsync(run.Id, new GeneratePaymentFileRequest(bankId, PaymentFileType.Nacha));
        Assert.EndsWith("-B.ach", second.FileName, StringComparison.Ordinal);
        var downloads = (await files.GetRunFilesAsync(run.Id)).Downloads;
        Assert.Equal(2, downloads.Count);
        Assert.All(downloads, d => Assert.Equal(64, d.Sha256.Length));
        Assert.All(downloads, d => Assert.Equal(Finance, d.DownloadedBy));
        await using var db = await kit.Factory.CreateAsync();
        Assert.Equal(2, await db.Set<SecurityEvent>().CountAsync(e => e.EventType == BankFileSecurityEvents.PaymentFileDownloaded));
    }

    [Fact]
    public async Task Nacha_needs_approved_bank_details_an_approved_run_and_dollars()
    {
        var (kit, _, bankId) = await KitAsync();
        await using var _kit = kit;
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "INV-9", PurchasingKit.Day, [kit.OverheadLine(100m)]));
        kit.As(Finance);
        var draft = await kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Ach, [new PaymentRunItemRequest(bill.Bill.Id, 100m)]));
        var files = kit.Get<IPaymentFileService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => files.GenerateAsync(draft.Id, new GeneratePaymentFileRequest(bankId, PaymentFileType.Nacha)));

        kit.As(Admin);
        await kit.Payments.ApproveRunAsync(draft.Id);
        kit.As(Finance);
        var missing = await Assert.ThrowsAsync<ArgumentException>(() => files.GenerateAsync(draft.Id, new GeneratePaymentFileRequest(bankId, PaymentFileType.Nacha)));
        Assert.Contains("Primavera", missing.Message, StringComparison.Ordinal);
        Assert.Contains((await files.GetRunFilesAsync(draft.Id)).Problems, p => p.Contains("Primavera", StringComparison.Ordinal));

        await ApprovedBankAsync(kit, kit.Prima, PrimaAccount);
        await using (var db = await kit.Factory.CreateAsync())
        {
            var payment = await db.Set<ApPayment>().SingleAsync(p => p.RunId == draft.Id);
            payment.CurrencyCode = "EUR";
            await db.SaveChangesAsync();
        }

        var foreign = await Assert.ThrowsAsync<ArgumentException>(() => files.GenerateAsync(draft.Id, new GeneratePaymentFileRequest(bankId, PaymentFileType.Nacha)));
        Assert.Contains("US dollars", foreign.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => files.GenerateAsync(draft.Id, new GeneratePaymentFileRequest(bankId, PaymentFileType.PositivePay)));
    }

    // ---- Positive pay ----

    [Fact]
    public async Task Check_runs_give_positive_pay_and_voids_give_void_records()
    {
        var (kit, _, bankId) = await KitAsync();
        await using var _kit = kit;
        var b1 = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "INV-1", PurchasingKit.Day, [kit.OverheadLine(1_234.50m)]));
        var b2 = await kit.PostedBillAsync(new SaveBillRequest(kit.Bolt.Id, "B-1", PurchasingKit.Day, [kit.OverheadLine(99m)]));
        var run = await kit.PayAsync(PurchasingKit.Day, "5001", (b1.Bill.Id, 1_234.50m), (b2.Bill.Id, 99m));
        var files = kit.Get<IPaymentFileService>();

        var issued = await files.GenerateAsync(run.Id, new GeneratePaymentFileRequest(bankId, PaymentFileType.PositivePay));
        Assert.Equal(
            "Account,Check number,Issue date,Amount,Payee,Void\r\n" +
            "123456784321,5001,10/14/2026,99.00,Bolt Consulting,\r\n" +
            "123456784321,5002,10/14/2026,1234.50,Primavera Reseller LLC,\r\n", issued.Content);
        await Assert.ThrowsAsync<ArgumentException>(() => files.GenerateAsync(run.Id, new GeneratePaymentFileRequest(bankId, PaymentFileType.PositivePayVoids)));

        var bolt = run.Payments.Single(p => p.VendorCode == "BOLT");
        await kit.Payments.VoidPaymentAsync(bolt.Id, new VoidPaymentRequest("Lost in the mail"));
        var voids = await files.GenerateAsync(run.Id, new GeneratePaymentFileRequest(bankId, PaymentFileType.PositivePayVoids));
        Assert.Equal("Account,Check number,Issue date,Amount,Payee,Void\r\n123456784321,5001,10/14/2026,99.00,Bolt Consulting,V\r\n", voids.Content);
        Assert.Equal(0m, voids.TotalAmount);

        // A bank that wants fixed width: switch the layout on the account.
        await files.SaveSettingsAsync(bankId, new SaveBankFileSettingsRequest("054001725", "First Capital Bank", "1123456789", "Workbench Federal LLC", "1123456789", "Workbench Fed", "Vendor Pay",
            PositivePayLayoutId: PositivePayWriter.FixedWidth80Id));
        var fixedWidth = await files.GenerateAsync(run.Id, new GeneratePaymentFileRequest(bankId, PaymentFileType.PositivePay));
        Assert.All(fixedWidth.Content.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), l => Assert.Equal(80, l.Length));
        Assert.EndsWith(".txt", fixedWidth.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Custom_layouts_are_saved_and_presets_cannot_be_changed()
    {
        var (kit, _, _) = await KitAsync();
        await using var _kit = kit;
        var files = kit.Get<IPaymentFileService>();
        var layout = await files.SaveLayoutAsync(null, new SavePositivePayLayoutRequest("Our bank", PositivePayFileFormat.FixedWidth,
            [new(PositivePayField.CheckNumber, 8, '0', FieldAlign.Right), new(PositivePayField.AmountCents, 10, '0', FieldAlign.Right), new(PositivePayField.Literal, 3, Value: "USD")]));
        Assert.Contains(await files.ListLayoutsAsync(), l => l.Id == layout.Id && l.Fields.Count == 3);
        await Assert.ThrowsAsync<ArgumentException>(() => files.SaveLayoutAsync(PositivePayWriter.StandardCsvId, new SavePositivePayLayoutRequest("x", PositivePayFileFormat.Csv,
            [new(PositivePayField.CheckNumber), new(PositivePayField.Amount)])));
    }

    // ---- Remittance ----

    [Fact]
    public async Task Remittance_advice_is_emailed_to_each_vendor_and_logged()
    {
        var (kit, email, _) = await KitAsync();
        await using var _kit = kit;
        await ApprovedBankAsync(kit, kit.Prima, PrimaAccount, remit: "ap@primavera.example");
        var b1 = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "INV-1001", PurchasingKit.Day, [kit.OverheadLine(1_500m)]));
        var b2 = await kit.PostedBillAsync(new SaveBillRequest(kit.Bolt.Id, "B-1", PurchasingKit.Day, [kit.OverheadLine(10m)]));
        var run = await AchRunAsync(kit, (b1.Bill.Id, 1_500m), (b2.Bill.Id, 10m));

        var result = await kit.Get<IPaymentFileService>().SendRemittanceAsync(run.Id);

        Assert.Equal(1, result.Sent);
        Assert.Contains(result.Skipped, s => s.StartsWith("BOLT", StringComparison.Ordinal)); // no email on file
        var message = Assert.Single(email.Sent);
        Assert.Equal("ap@primavera.example", message.To);
        Assert.Contains("INV-1001", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("$1,500.00", message.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(PrimaAccount, message.TextBody + message.HtmlBody, StringComparison.Ordinal);
        var logged = (await kit.Get<IPaymentFileService>().GetRunFilesAsync(run.Id)).Remittances.Single(r => r.VendorCode == "PRIMA");
        Assert.NotNull(logged.SentAtUtc);
    }
}
