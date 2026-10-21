using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Application.Cash;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.BankFiles;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.BankFiles;

/// <summary>Encrypts vendor routing and account numbers with the host's Data Protection keys (separate purposes per field).</summary>
public sealed class VendorBankProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _routing = provider.CreateProtector("Workbench.Purchasing.VendorBankRouting.v1");
    private readonly IDataProtector _account = provider.CreateProtector("Workbench.Purchasing.VendorBankAccount.v1");

    public string ProtectRouting(string digits) => _routing.Protect(digits);

    public string UnprotectRouting(string protectedValue) => _routing.Unprotect(protectedValue);

    public string ProtectAccount(string digits) => _account.Protect(digits);

    public string UnprotectAccount(string protectedValue) => _account.Unprotect(protectedValue);
}

/// <summary>Security log event names for bank payment files and vendor bank details.</summary>
public static class BankFileSecurityEvents
{
    public const string VendorBankChangeRequested = "vendor_bank.change_requested";
    public const string VendorBankChanged = "vendor_bank.changed";
    public const string VendorBankChangeRejected = "vendor_bank.change_rejected";
    public const string VendorBankRevealed = "vendor_bank.revealed";
    public const string PaymentFileDownloaded = "export.payment_file";
}

/// <summary>
/// Vendor ACH bank details. A change is a <see cref="VendorBankChange"/> routed through the vendor bank change workflow
/// (default: another Finance user; administrators may decide), never approved by its requester. Numbers are encrypted at
/// rest and shown masked; changes are audited (masked) and recorded in the security log.
/// </summary>
public sealed class VendorBankService(
    IWorkbenchDbContextFactory dbFactory,
    VendorBankProtector protector,
    AuditRecorder audit,
    AccessControl access,
    SecurityEventLog securityLog,
    INotificationPublisher notifications,
    TimeProvider clock,
    ApprovalEngine? approvals = null) : IVendorBankService
{
    public const string ChangeEntity = nameof(VendorBankChange);

    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public static string Mask(string last4) => $"••••{last4}";

    public async Task<VendorBankDetailsDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == vendorId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Vendor", vendorId);
        var account = await db.Set<VendorBankAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.VendorId == vendorId, cancellationToken);
        var changes = await db.Set<VendorBankChange>().AsNoTracking().Where(c => c.VendorId == vendorId).ToListAsync(cancellationToken);
        var dtos = new List<VendorBankChangeDto>();
        foreach (var c in changes.OrderByDescending(c => c.RequestedAtUtc))
        {
            dtos.Add(await ToDtoAsync(db, c, vendor, cancellationToken));
        }

        return new VendorBankDetailsDto(vendor.Id, vendor.Code, vendor.Name, account is not null, account?.AccountHolderName, account?.Kind.ToContract(),
            account is null ? null : Mask(account.RoutingLast4), account is null ? null : Mask(account.AccountLast4), account?.RemittanceEmail,
            account?.ApprovedBy, account?.ApprovedAtUtc, dtos.FirstOrDefault(d => d.Status == BankChangeState.Pending), dtos);
    }

    public async Task<IReadOnlyList<VendorBankChangeDto>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var changes = await db.Set<VendorBankChange>().AsNoTracking().Where(c => c.Status == VendorBankChangeStatus.Pending).ToListAsync(cancellationToken);
        var vendorIds = changes.Select(c => c.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var result = new List<VendorBankChangeDto>();
        foreach (var c in changes.OrderBy(c => c.RequestedAtUtc))
        {
            result.Add(await ToDtoAsync(db, c, vendors[c.VendorId], cancellationToken));
        }

        return result;
    }

    public async Task<VendorBankChangeDto> RequestChangeAsync(Guid vendorId, RequestVendorBankChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var holder = Clean(request.AccountHolderName, "The account holder's name", 200);
        var reason = Clean(request.Reason, "How the new details were confirmed", 1000);
        var routing = CashSupport.NormalizeRouting(request.RoutingNumber) ?? throw new ArgumentException("Enter the vendor's nine-digit routing number.");
        var accountNumber = CashSupport.NormalizeAccountNumber(request.AccountNumber ?? "");
        var email = string.IsNullOrWhiteSpace(request.RemittanceEmail) ? null : request.RemittanceEmail.Trim().ToLowerInvariant();
        if (email is not null && (email.Length > 320 || !email.Contains('@', StringComparison.Ordinal)))
        {
            throw new ArgumentException("The remittance email isn't a valid email address.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == vendorId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Vendor", vendorId);
        if (await db.Set<VendorBankChange>().AnyAsync(c => c.VendorId == vendorId && c.Status == VendorBankChangeStatus.Pending, cancellationToken))
        {
            throw new InvalidOperationException($"{vendor.Name} already has a bank details change waiting for approval. Cancel it or have it decided first.");
        }

        var change = new VendorBankChange
        {
            VendorId = vendor.Id,
            AccountHolderName = holder,
            Kind = request.AccountType.ToDomain(),
            ProtectedRoutingNumber = protector.ProtectRouting(routing),
            ProtectedAccountNumber = protector.ProtectAccount(accountNumber),
            RoutingLast4 = routing[^4..],
            AccountLast4 = accountNumber[^4..],
            RemittanceEmail = email,
            Reason = reason,
            RequestedBy = access.Email,
            RequestedAtUtc = UtcNow,
        };
        db.Set<VendorBankChange>().Add(change);
        var route = await _approvals.StartAsync(db, Subject(change, vendor), cancellationToken);
        var current = await db.Set<VendorBankAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.VendorId == vendorId, cancellationToken);
        audit.Record(db, ChangeEntity, change.Id, "Requested",
            current is null ? null : new { Vendor = vendor.Code, Routing = Mask(current.RoutingLast4), Account = Mask(current.AccountLast4) },
            new { Vendor = vendor.Code, Routing = Mask(change.RoutingLast4), Account = Mask(change.AccountLast4), Type = change.Kind.ToString() }, reason);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(BankFileSecurityEvents.VendorBankChangeRequested, $"vendor {vendor.Code}", SecurityOutcomes.Success,
            $"Routing {Mask(change.RoutingLast4)}, account {Mask(change.AccountLast4)}", cancellationToken: cancellationToken);
        await notifications.PublishAsync(route.NotifyEmails.Where(e => !PurchasingSame(e, access.Email)), "vendor_bank.change",
            $"{access.Email} asked to change {vendor.Name}'s bank details", $"New account ending {change.AccountLast4}. Confirm with the vendor before approving.",
            link: Link(vendor.Id));
        return await ToDtoAsync(db, change, vendor, cancellationToken);
    }

    public async Task<VendorBankChangeDto> ApproveChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default)
    {
        var comment = Optional(request?.Comment);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (change, vendor) = await LoadPendingAsync(db, changeId, cancellationToken);
        var check = await CheckAsync(db, change, vendor, cancellationToken);
        var outcome = await _approvals.DecideAsync(db, check, ApprovalDecisionKind.Approve, access.Email, comment, cancellationToken);
        if (!outcome.IsComplete)
        {
            audit.Record(db, ChangeEntity, change.Id, "StepApproved", after: new { Step = string.Join(", ", outcome.Decided.Select(t => t.StepName).Distinct()) }, reason: comment);
            await db.SaveChangesAsync(cancellationToken);
            await notifications.PublishAsync(outcome.NotifyEmails, "vendor_bank.change", $"{vendor.Name}'s bank details change is waiting on you", null, link: Link(vendor.Id));
            return await ToDtoAsync(db, change, vendor, cancellationToken);
        }

        var account = await db.Set<VendorBankAccount>().FirstOrDefaultAsync(a => a.VendorId == vendor.Id, cancellationToken);
        var before = account is null ? null : new { Routing = Mask(account.RoutingLast4), Account = Mask(account.AccountLast4), Type = account.Kind.ToString() };
        if (account is null)
        {
            account = new VendorBankAccount
            {
                VendorId = vendor.Id,
                AccountHolderName = change.AccountHolderName,
                ProtectedRoutingNumber = change.ProtectedRoutingNumber,
                ProtectedAccountNumber = change.ProtectedAccountNumber,
                RoutingLast4 = change.RoutingLast4,
                AccountLast4 = change.AccountLast4,
                ApprovedBy = access.Email,
            };
            db.Set<VendorBankAccount>().Add(account);
        }

        account.AccountHolderName = change.AccountHolderName;
        account.Kind = change.Kind;
        account.ProtectedRoutingNumber = change.ProtectedRoutingNumber;
        account.ProtectedAccountNumber = change.ProtectedAccountNumber;
        account.RoutingLast4 = change.RoutingLast4;
        account.AccountLast4 = change.AccountLast4;
        account.RemittanceEmail = change.RemittanceEmail;
        account.ChangeId = change.Id;
        account.ApprovedBy = access.Email;
        account.ApprovedAtUtc = UtcNow;
        Decide(change, VendorBankChangeStatus.Approved, comment);
        audit.Record(db, ChangeEntity, change.Id, "Approved", before,
            new { Routing = Mask(change.RoutingLast4), Account = Mask(change.AccountLast4), Type = change.Kind.ToString() }, comment);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(BankFileSecurityEvents.VendorBankChanged, $"vendor {vendor.Code}", SecurityOutcomes.Success,
            $"Account {Mask(change.AccountLast4)} approved; requested by {change.RequestedBy}", cancellationToken: cancellationToken);
        await notifications.PublishAsync(change.RequestedBy, "vendor_bank.change", $"{access.Email} approved {vendor.Name}'s new bank details", comment, link: Link(vendor.Id));
        return await ToDtoAsync(db, change, vendor, cancellationToken);
    }

    public async Task<VendorBankChangeDto> RejectChangeAsync(Guid changeId, DecideVendorBankChangeRequest request, CancellationToken cancellationToken = default)
    {
        var comment = Optional(request?.Comment) ?? throw new ArgumentException("Say why the change is rejected.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (change, vendor) = await LoadPendingAsync(db, changeId, cancellationToken);
        var check = await CheckAsync(db, change, vendor, cancellationToken);
        await _approvals.DecideAsync(db, check, ApprovalDecisionKind.Reject, access.Email, comment, cancellationToken);
        Decide(change, VendorBankChangeStatus.Rejected, comment);
        audit.Record(db, ChangeEntity, change.Id, "Rejected", reason: comment);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(BankFileSecurityEvents.VendorBankChangeRejected, $"vendor {vendor.Code}", SecurityOutcomes.Success, $"Account {Mask(change.AccountLast4)}", cancellationToken: cancellationToken);
        await notifications.PublishAsync(change.RequestedBy, "vendor_bank.change", $"{access.Email} rejected the bank details change for {vendor.Name}", comment, link: Link(vendor.Id));
        return await ToDtoAsync(db, change, vendor, cancellationToken);
    }

    public async Task<VendorBankChangeDto> CancelChangeAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (change, vendor) = await LoadPendingAsync(db, changeId, cancellationToken);
        if (!PurchasingSame(change.RequestedBy, access.Email) && !await access.HasAsync(Permission.VendorBankDetails, cancellationToken))
        {
            throw new WorkbenchForbiddenException("Only the person who asked for the change (or an administrator) can cancel it.");
        }

        await _approvals.CancelAsync(db, ApprovalDocumentTypes.VendorBankChange, change.Id, access.Email, "Cancelled", cancellationToken);
        Decide(change, VendorBankChangeStatus.Cancelled, null);
        audit.Record(db, ChangeEntity, change.Id, "Cancelled");
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, change, vendor, cancellationToken);
    }

    public async Task<VendorBankRevealDto> RevealAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == vendorId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Vendor", vendorId);
        var account = await db.Set<VendorBankAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.VendorId == vendorId, cancellationToken)
            ?? throw new InvalidOperationException($"{vendor.Name} has no bank details on file.");
        try
        {
            var result = new VendorBankRevealDto(vendor.Id, protector.UnprotectRouting(account.ProtectedRoutingNumber), protector.UnprotectAccount(account.ProtectedAccountNumber));
            await securityLog.RecordAsync(BankFileSecurityEvents.VendorBankRevealed, $"vendor {vendor.Code}", SecurityOutcomes.Success, $"Account {Mask(account.AccountLast4)}", cancellationToken: cancellationToken);
            return result;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            await securityLog.RecordAsync(BankFileSecurityEvents.VendorBankRevealed, $"vendor {vendor.Code}", SecurityOutcomes.Failure, "The bank details couldn't be decrypted (key unavailable)", cancellationToken: cancellationToken);
            throw new InvalidOperationException("The bank details couldn't be decrypted. Check the Data Protection keys (docs/operations/data-protection-keys.md).");
        }
    }

    /// <summary>The decrypted routing and account number for a payment file (callers log the download).</summary>
    internal (string Routing, string Account) Decrypt(VendorBankAccount account) =>
        (protector.UnprotectRouting(account.ProtectedRoutingNumber), protector.UnprotectAccount(account.ProtectedAccountNumber));

    // ---- Helpers ----

    private static ApprovalSubject Subject(VendorBankChange change, Vendor vendor) =>
        new(ApprovalDocumentTypes.VendorBankChange, change.Id, $"Bank details for {vendor.Code} {vendor.Name}", change.RequestedBy, Link(vendor.Id),
            Summary: $"New {change.Kind.ToString().ToLowerInvariant()} account ending {change.AccountLast4} (routing ending {change.RoutingLast4}). {change.Reason}");

    private static string Link(Guid vendorId) => $"purchasing/vendors/{vendorId}";

    private async Task<(VendorBankChange Change, Vendor Vendor)> LoadPendingAsync(IWorkbenchDbContext db, Guid changeId, CancellationToken ct)
    {
        var change = await db.Set<VendorBankChange>().FirstOrDefaultAsync(c => c.Id == changeId, ct) ?? throw WorkbenchNotFoundException.For("Bank details change", changeId);
        if (change.Status != VendorBankChangeStatus.Pending)
        {
            throw new InvalidOperationException($"This change was already {change.Status.ToString().ToLowerInvariant()}.");
        }

        var vendor = await db.Set<Vendor>().AsNoTracking().FirstAsync(v => v.Id == change.VendorId, ct);
        return (change, vendor);
    }

    private async Task<ApprovalCheck> CheckAsync(IWorkbenchDbContext db, VendorBankChange change, Vendor vendor, CancellationToken ct)
    {
        // Segregation of duties, whatever the workflow says: the requester never decides their own change.
        if (PurchasingSame(change.RequestedBy, access.Email))
        {
            throw new WorkbenchForbiddenException("You asked for this change, so someone else has to approve or reject it.");
        }

        var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.VendorBankChange, change.Id, access.Email, _ => Task.FromResult(Subject(change, vendor)), ct);
        return check.CanDecide ? check : throw new WorkbenchForbiddenException("This bank details change isn't waiting on you.");
    }

    private void Decide(VendorBankChange change, VendorBankChangeStatus status, string? comment)
    {
        change.Status = status;
        change.DecidedBy = access.Email;
        change.DecidedAtUtc = UtcNow;
        change.DecisionComment = comment;
    }

    private async Task<VendorBankChangeDto> ToDtoAsync(IWorkbenchDbContext db, VendorBankChange c, Vendor vendor, CancellationToken ct)
    {
        var canDecide = c.Status == VendorBankChangeStatus.Pending && !PurchasingSame(c.RequestedBy, access.Email)
            && await _approvals.CanDecideAsync(db, ApprovalDocumentTypes.VendorBankChange, c.Id, access.Email, _ => Task.FromResult(Subject(c, vendor)), ct);
        return new VendorBankChangeDto(c.Id, c.VendorId, vendor.Code, vendor.Name, c.Status.ToContract(), c.AccountHolderName, c.Kind.ToContract(),
            Mask(c.RoutingLast4), Mask(c.AccountLast4), c.RemittanceEmail, c.Reason, c.RequestedBy, c.RequestedAtUtc, c.DecidedBy, c.DecidedAtUtc, c.DecisionComment, canDecide);
    }

    private static bool PurchasingSame(string? a, string? b) => AccessControl.Normalize(a) == AccessControl.Normalize(b);

    private static string Clean(string? value, string what, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException($"{what} is required.");
        }

        return text.Length <= max ? text : throw new ArgumentException($"{what} can be at most {max} characters.");
    }

    private static string? Optional(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= 1000 ? text : text[..1000];
    }
}

/// <summary>Lets the approvals inbox decide vendor bank details changes through the service (so its checks run).</summary>
public sealed class VendorBankChangeApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.VendorBankChange;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<IVendorBankService>().ApproveChangeAsync(documentId, new DecideVendorBankChangeRequest(comment), ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        services.GetRequiredService<IVendorBankService>().RejectChangeAsync(documentId, new DecideVendorBankChangeRequest(reason), ct);
}

internal static class BankFileMapping
{
    public static BankAccountType ToContract(this BankAccountKind k) => k == BankAccountKind.Savings ? BankAccountType.Savings : BankAccountType.Checking;

    public static BankAccountKind ToDomain(this BankAccountType k) => k == BankAccountType.Savings ? BankAccountKind.Savings : BankAccountKind.Checking;

    public static BankChangeState ToContract(this VendorBankChangeStatus s) => (BankChangeState)(int)s;

    public static PaymentFileType ToContract(this Domain.Cash.PaymentFileKind k) => (PaymentFileType)(int)k;

    public static Domain.Cash.PaymentFileKind ToDomain(this PaymentFileType k) => (Domain.Cash.PaymentFileKind)(int)k;
}
