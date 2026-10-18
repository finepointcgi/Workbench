using Microsoft.EntityFrameworkCore;
using Workbench.Application.Files;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Security;
using Workbench.Domain.Files;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

public sealed class VendorService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    TaxIdProtector protector,
    FileStorage files,
    SecurityEventLog securityLog,
    TimeProvider clock) : IVendorService
{
    public async Task<IReadOnlyList<VendorDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendors = await db.Set<Vendor>().AsNoTracking()
            .Where(v => includeInactive || v.Status != VendorStatus.Inactive)
            .OrderBy(v => v.Code).ToListAsync(cancellationToken);
        return await ToDtosAsync(db, vendors, cancellationToken);
    }

    public async Task<VendorDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await ToDtoAsync(db, await PurchasingSupport.FindVendorAsync(db, vendorId, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<VendorDuplicateDto>> FindDuplicatesAsync(string name, string? uei, Guid? excludeVendorId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DuplicatesAsync(db, name, uei, excludeVendorId, cancellationToken);
    }

    public async Task<VendorDto> CreateAsync(SaveVendorRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = new Vendor { Code = "", Name = "", CreatedAtUtc = clock.GetUtcNow().UtcDateTime };
        await ApplyAsync(db, vendor, request, null, cancellationToken);
        db.Set<Vendor>().Add(vendor);
        audit.Record(db, PurchasingSupport.VendorEntity, vendor.Id, "Created", after: Snapshot(vendor));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, vendor, cancellationToken);
    }

    public async Task<VendorDto> UpdateAsync(Guid vendorId, SaveVendorRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, cancellationToken, track: true);
        var before = Snapshot(vendor);
        var status = vendor.Status;
        await ApplyAsync(db, vendor, request, vendorId, cancellationToken);
        audit.Record(db, PurchasingSupport.VendorEntity, vendor.Id, status != vendor.Status ? "StatusChanged" : "Updated", before, Snapshot(vendor));
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, vendor, cancellationToken);
    }

    public async Task<VendorDto> SetTaxIdAsync(Guid vendorId, SetVendorTaxIdRequest request, CancellationToken cancellationToken = default)
    {
        var digits = TaxIdProtector.Normalize(request.TaxId);
        var kind = (request.Kind ?? "EIN").Trim().ToUpperInvariant();
        if (kind is not ("EIN" or "SSN"))
        {
            throw new ArgumentException("The TIN kind is EIN or SSN.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, cancellationToken, track: true);
        var record = await db.Set<VendorTaxId>().FirstOrDefaultAsync(t => t.VendorId == vendorId, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        if (record is null)
        {
            record = new VendorTaxId { VendorId = vendorId, Kind = kind, ProtectedValue = "", UpdatedBy = audit.Actor };
            db.Set<VendorTaxId>().Add(record);
        }

        record.Kind = kind;
        record.ProtectedValue = protector.Protect(digits);
        record.UpdatedBy = audit.Actor;
        record.UpdatedAtUtc = now;
        var before = vendor.TaxIdLast4;
        vendor.TaxIdLast4 = digits[^4..];

        // Only the last four digits are ever written to the audit trail or the security log.
        audit.Record(db, PurchasingSupport.VendorEntity, vendor.Id, "TaxIdChanged",
            new { TaxId = PurchasingCatalog.MaskTaxId(before) }, new { TaxId = PurchasingCatalog.MaskTaxId(vendor.TaxIdLast4), Kind = kind });
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(PurchasingSecurityEvents.TaxIdChanged, $"vendor {vendor.Code}", SecurityOutcomes.Success, $"{kind} ending {vendor.TaxIdLast4}", cancellationToken: cancellationToken);
        return await ToDtoAsync(db, vendor, cancellationToken);
    }

    public async Task<VendorTaxIdDto> RevealTaxIdAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, cancellationToken);
        var record = await db.Set<VendorTaxId>().AsNoTracking().FirstOrDefaultAsync(t => t.VendorId == vendorId, cancellationToken)
            ?? throw new WorkbenchNotFoundException($"Vendor {vendor.Code} has no full TIN on file.");
        string digits;
        try
        {
            digits = protector.Unprotect(record.ProtectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            await securityLog.RecordAsync(PurchasingSecurityEvents.TaxIdRevealed, $"vendor {vendor.Code}", SecurityOutcomes.Failure, "The TIN couldn't be decrypted (key unavailable)", cancellationToken: cancellationToken);
            throw new InvalidOperationException("The TIN couldn't be decrypted: its key isn't available on this server. Enter it again.");
        }

        await securityLog.RecordAsync(PurchasingSecurityEvents.TaxIdRevealed, $"vendor {vendor.Code}", SecurityOutcomes.Success, $"{record.Kind} ending {vendor.TaxIdLast4}", cancellationToken: cancellationToken);
        return new VendorTaxIdDto(vendorId, record.Kind, TaxIdProtector.Format(digits, record.Kind));
    }

    public async Task<VendorDto> AttachW9Async(Guid vendorId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, cancellationToken, track: true);
        var file = await files.SaveAsync(db, PurchasingSupport.W9OwnerType, vendorId, fileName, content, cancellationToken);
        var before = vendor.W9FileId;
        vendor.W9FileId = file.Id;
        audit.Record(db, PurchasingSupport.VendorEntity, vendor.Id, "W9Attached", new { W9FileId = before }, new { W9FileId = file.Id, file.FileName });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await files.DiscardAsync(file);
            throw;
        }

        return await ToDtoAsync(db, vendor, cancellationToken);
    }

    public async Task<ReceiptContent> OpenW9Async(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, cancellationToken);
        if (vendor.W9FileId is not { } fileId)
        {
            throw new WorkbenchNotFoundException($"Vendor {vendor.Code} has no W-9 on file.");
        }

        var (file, stream) = await files.OpenAsync(db, fileId, cancellationToken);
        return new ReceiptContent(file.FileName, file.ContentType, stream);
    }

    public string GetW9Url(Guid vendorId) => $"api/v1/purchasing/vendors/{vendorId}/w9";

    // ---- Helpers ----

    private async Task ApplyAsync(IWorkbenchDbContext db, Vendor vendor, SaveVendorRequest r, Guid? existingId, CancellationToken ct)
    {
        var code = PurchasingSupport.Required(r.Code, "Vendor code", 32).ToUpperInvariant();
        var name = PurchasingSupport.Required(r.Name, "Vendor name", 200);
        var uei = PurchasingSupport.Optional(r.Uei, "UEI", 12)?.ToUpperInvariant();
        if (uei is not null && (uei.Length != 12 || !uei.All(char.IsAsciiLetterOrDigit)))
        {
            throw new ArgumentException("A UEI is 12 letters and digits.");
        }

        if (r.PaymentTermsDays is < 0 or > 365)
        {
            throw new ArgumentException("Payment terms are 0 to 365 days.");
        }

        if (await db.Set<Vendor>().AnyAsync(v => v.Code == code && v.Id != existingId, ct))
        {
            throw new ArgumentException($"Vendor code {code} is already used.");
        }

        var duplicates = await DuplicatesAsync(db, name, uei, existingId, ct);
        if (duplicates.FirstOrDefault(d => d.Reason.StartsWith("Same UEI", StringComparison.Ordinal)) is { } sameUei)
        {
            throw new ArgumentException($"{sameUei.Code} {sameUei.Name} already has UEI {uei}.");
        }

        // Only a name change can create a new name duplicate; saving an existing vendor unchanged never asks again.
        if (duplicates.Count > 0 && !r.ConfirmDuplicate && (existingId is null || PurchasingSupport.NameKey(vendor.Name) != PurchasingSupport.NameKey(name)))
        {
            throw new ArgumentException($"Possible duplicate of {string.Join(", ", duplicates.Select(d => $"{d.Code} {d.Name}"))}. Confirm to save anyway.");
        }

        var box = PurchasingSupport.Optional(r.Form1099Box, "1099 box", 16);
        if (box is not null && PurchasingCatalog.Form1099Boxes.All(b => b.Box != box))
        {
            throw new ArgumentException($"Unknown 1099 box {box}.");
        }

        var categories = (r.SocioEconomicCategories ?? [])
            .Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => PurchasingCatalog.SocioEconomicCategories.FirstOrDefault(k => string.Equals(k, c, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown socio-economic category {c}."))
            .ToList();

        var email = PurchasingSupport.Optional(r.ContactEmail, "Contact email", 320);
        if (email is not null && !System.Net.Mail.MailAddress.TryCreate(email, out _))
        {
            throw new ArgumentException("The contact email isn't valid.");
        }

        vendor.Code = code;
        vendor.Name = name;
        vendor.Status = r.Status.ToDomain();
        vendor.Is1099 = r.Is1099;
        vendor.Form1099Box = r.Is1099 ? box ?? PurchasingCatalog.DefaultForm1099Box : box;
        vendor.Uei = uei;
        vendor.BusinessSize = PurchasingSupport.Optional(r.BusinessSize, "Business size", 32);
        vendor.SocioEconomicCategories = categories.Count == 0 ? null : string.Join(',', categories);
        vendor.PaymentTermsDays = r.PaymentTermsDays;
        vendor.RemitToAddress = PurchasingSupport.Optional(r.RemitToAddress, "Remit-to address", 1000);
        vendor.ContactName = PurchasingSupport.Optional(r.ContactName, "Contact name", 200);
        vendor.ContactEmail = email;
        vendor.ContactPhone = PurchasingSupport.Optional(r.ContactPhone, "Contact phone", 40);
    }

    private static async Task<IReadOnlyList<VendorDuplicateDto>> DuplicatesAsync(IWorkbenchDbContext db, string name, string? uei, Guid? excludeId, CancellationToken ct)
    {
        var key = PurchasingSupport.NameKey(name ?? "");
        var normalizedUei = string.IsNullOrWhiteSpace(uei) ? null : uei.Trim().ToUpperInvariant();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => v.Id != excludeId).ToListAsync(ct);
        var result = new List<VendorDuplicateDto>();
        foreach (var v in vendors.OrderBy(v => v.Code))
        {
            if (normalizedUei is not null && string.Equals(v.Uei, normalizedUei, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new(v.Id, v.Code, v.Name, $"Same UEI ({normalizedUei})"));
            }
            else if (key.Length > 0 && PurchasingSupport.NameKey(v.Name) == key)
            {
                result.Add(new(v.Id, v.Code, v.Name, "Similar name"));
            }
        }

        return result;
    }

    private static object Snapshot(Vendor v) => new
    {
        v.Code,
        v.Name,
        Status = v.Status.ToString(),
        v.Is1099,
        v.Form1099Box,
        v.Uei,
        v.BusinessSize,
        v.SocioEconomicCategories,
        v.PaymentTermsDays,
        v.RemitToAddress,
        v.ContactName,
        v.ContactEmail,
        v.ContactPhone,
    };

    private static async Task<VendorDto> ToDtoAsync(IWorkbenchDbContext db, Vendor vendor, CancellationToken ct) =>
        (await ToDtosAsync(db, [vendor], ct))[0];

    internal static async Task<IReadOnlyList<VendorDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<Vendor> vendors, CancellationToken ct)
    {
        var ids = vendors.Select(v => v.Id).ToList();
        var tins = await db.Set<VendorTaxId>().AsNoTracking().Where(t => ids.Contains(t.VendorId)).ToDictionaryAsync(t => t.VendorId, t => t.Kind, ct);
        var fileIds = vendors.Where(v => v.W9FileId is not null).Select(v => v.W9FileId!.Value).ToList();
        var w9s = await db.Set<StoredFile>().AsNoTracking().Where(f => fileIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        return vendors.Select(v =>
        {
            var w9 = v.W9FileId is { } f ? w9s.GetValueOrDefault(f) : null;
            return new VendorDto(
                v.Id, v.Code, v.Name, v.Status.ToContract(), v.Is1099, v.Form1099Box ?? PurchasingCatalog.DefaultForm1099Box,
                PurchasingCatalog.MaskTaxId(v.TaxIdLast4), tins.ContainsKey(v.Id), tins.GetValueOrDefault(v.Id), v.Uei, v.BusinessSize,
                (v.SocioEconomicCategories ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                v.PaymentTermsDays, v.RemitToAddress, v.ContactName, v.ContactEmail, v.ContactPhone, w9?.Id, w9?.FileName, w9?.UploadedAtUtc);
        }).ToList();
    }
}
