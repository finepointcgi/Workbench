using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Security;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

public sealed class VendorTests
{
    [Fact]
    public async Task The_full_TIN_is_stored_encrypted_and_shown_masked()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var vendor = await kit.Vendors.SetTaxIdAsync(kit.Acme.Id, new SetVendorTaxIdRequest("47-1234821"));

        Assert.Equal("•••••4821", vendor.TaxIdMasked);
        Assert.True(vendor.HasFullTaxId);
        Assert.Equal("EIN", vendor.TaxIdKind);
        Assert.Equal("•••••4821", (await kit.Vendors.ListAsync()).Single(v => v.Code == "ACME").TaxIdMasked);

        await using var db = await kit.Factory.CreateAsync();
        var row = await db.Set<VendorTaxId>().SingleAsync(t => t.VendorId == kit.Acme.Id);
        Assert.DoesNotContain("471234821", row.ProtectedValue);
        Assert.NotEqual("471234821", row.ProtectedValue);
        Assert.Equal("4821", (await db.Set<Vendor>().SingleAsync(v => v.Id == kit.Acme.Id)).TaxIdLast4);

        // Neither the audit trail nor the security log ever carries the full TIN.
        var audits = await db.Set<AuditEvent>().Where(a => a.EntityId == kit.Acme.Id).ToListAsync();
        Assert.Contains(audits, a => a.Action == "TaxIdChanged");
        Assert.All(audits, a => Assert.DoesNotContain("1234821", (a.BeforeJson ?? "") + a.AfterJson));
    }

    [Fact]
    public async Task Revealing_the_TIN_is_for_finance_and_is_logged()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await kit.Vendors.SetTaxIdAsync(kit.Acme.Id, new SetVendorTaxIdRequest("123-45-6789", "SSN"));

        var revealed = await kit.Vendors.RevealTaxIdAsync(kit.Acme.Id);
        Assert.Equal("123-45-6789", revealed.TaxId);

        kit.As(Owner);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Vendors.RevealTaxIdAsync(kit.Acme.Id));

        await using var db = await kit.Factory.CreateAsync();
        var events = await db.Set<SecurityEvent>().ToListAsync();
        var reveal = Assert.Single(events, e => e.EventType == PurchasingSecurityEvents.TaxIdRevealed);
        Assert.Equal(Finance, reveal.Actor);
        Assert.Equal("SSN ending 6789", reveal.Detail);
        Assert.Contains(events, e => e.EventType == Contracts.Security.SecurityEventTypes.PermissionDenied && e.Actor == Owner);
        Assert.All(events, e => Assert.DoesNotContain("123456789", e.Detail ?? ""));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12-34567890")]
    [InlineData("11-1111111")]
    public async Task An_invalid_TIN_is_refused(string tin)
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Vendors.SetTaxIdAsync(kit.Acme.Id, new SetVendorTaxIdRequest(tin)));
    }

    [Fact]
    public async Task Duplicate_vendors_are_caught_by_UEI_and_name()
    {
        await using var kit = await PurchasingKit.CreateAsync();

        var sameUei = await Assert.ThrowsAsync<ArgumentException>(() => kit.Vendors.CreateAsync(new SaveVendorRequest("ACME2", "Totally Different", Uei: "acmefed00001")));
        Assert.Contains("UEI", sameUei.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Vendors.CreateAsync(new SaveVendorRequest("ACME2", "Totally Different", Uei: "ACMEFED00001", ConfirmDuplicate: true)));

        // "ACME FEDERAL, LLC" normalizes to the same name as "Acme Federal".
        var duplicates = await kit.Vendors.FindDuplicatesAsync("ACME FEDERAL, LLC", null);
        Assert.Equal("ACME", Assert.Single(duplicates).Code);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Vendors.CreateAsync(new SaveVendorRequest("ACME2", "ACME FEDERAL, LLC")));
        var confirmed = await kit.Vendors.CreateAsync(new SaveVendorRequest("ACME2", "ACME FEDERAL, LLC", ConfirmDuplicate: true));
        Assert.Equal("ACME2", confirmed.Code);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Vendors.CreateAsync(new SaveVendorRequest("acme", "Another")));
    }

    [Fact]
    public async Task A_W9_is_stored_through_the_files_service()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var vendor = await kit.Vendors.AttachW9Async(kit.Prima.Id, "w9.pdf", new MemoryStream("%PDF-1.4 test"u8.ToArray()));
        Assert.Equal("w9.pdf", vendor.W9FileName);

        await using var content = await kit.Vendors.OpenW9Async(kit.Prima.Id);
        Assert.Equal("w9.pdf", content.FileName);
        using var reader = new StreamReader(content.Content);
        Assert.Equal("%PDF-1.4 test", await reader.ReadToEndAsync());

        kit.As(Owner);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Vendors.OpenW9Async(kit.Prima.Id));
    }
}
