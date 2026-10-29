using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// Finance's side of the customer view: make a link for a contracting officer, see who looked and when, end it. The token is
/// 256 random bits; only its hash is kept, so the address can be shown once, at creation, and never again.
/// </summary>
public sealed class CustomerShareLinkService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : ICustomerShareLinkService
{
    public const int DefaultValidDays = 90;
    public const int MaxValidDays = 365;
    public const string AuditEntityType = nameof(CustomerShareLink);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    internal static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<IReadOnlyList<CustomerShareLinkDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await RequireContractAsync(db, contractId, cancellationToken);
        var links = await db.Set<CustomerShareLink>().AsNoTracking().Where(l => l.ContractId == contractId).OrderByDescending(l => l.CreatedAtUtc).ToListAsync(cancellationToken);
        return links.Select(l => ToDto(l, UtcNow)).ToList();
    }

    public async Task<CreatedCustomerShareLinkDto> CreateAsync(Guid contractId, CreateCustomerShareLinkRequest request, CancellationToken cancellationToken = default)
    {
        var label = request.Label?.Trim() ?? "";
        if (label.Length == 0)
        {
            throw new ArgumentException("Say who the link is for, for example \"Army CO, J. Rivera\".");
        }

        if (label.Length > 200)
        {
            throw new ArgumentException("Keep the name under 200 characters.");
        }

        var days = request.ValidDays ?? DefaultValidDays;
        if (days is < 1 or > MaxValidDays)
        {
            throw new ArgumentException($"A link can work for 1 to {MaxValidDays} days.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await RequireContractAsync(db, contractId, cancellationToken);
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = UtcNow;
        var link = new CustomerShareLink
        {
            ContractId = contractId,
            TokenHash = HashOf(token),
            Label = label,
            CreatedBy = audit.Actor,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(days),
        };
        db.Set<CustomerShareLink>().Add(link);
        audit.Record(db, AuditEntityType, link.Id, "Created", after: new { link.ContractId, link.Label, link.ExpiresAtUtc });
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedCustomerShareLinkDto(ToDto(link, now), $"c/{token}");
    }

    public async Task<CustomerShareLinkDto> RevokeAsync(Guid contractId, Guid linkId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var link = await db.Set<CustomerShareLink>().FirstOrDefaultAsync(l => l.Id == linkId && l.ContractId == contractId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Customer link", linkId);
        if (link.RevokedAtUtc is null)
        {
            link.RevokedAtUtc = UtcNow;
            link.RevokedBy = audit.Actor;
            audit.Record(db, AuditEntityType, link.Id, "Revoked", after: new { link.ContractId, link.Label });
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToDto(link, UtcNow);
    }

    private static async Task RequireContractAsync(IWorkbenchDbContext db, Guid contractId, CancellationToken ct)
    {
        if (!await db.Set<Contract>().AsNoTracking().AnyAsync(c => c.Id == contractId, ct))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }
    }

    private static CustomerShareLinkDto ToDto(CustomerShareLink l, DateTime now) =>
        new(l.Id, l.Label, l.CreatedBy, l.CreatedAtUtc, l.ExpiresAtUtc, l.RevokedAtUtc, l.LastViewedAtUtc, l.ViewCount, l.RevokedAtUtc is null && l.ExpiresAtUtc > now);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// The customer's page: found by the token alone. A token that is unknown, revoked or expired answers null, all the same, so
/// nothing says which. Only sent and paid invoices are shown, and nothing about cost, rates or people.
/// </summary>
public sealed class CustomerViewService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock) : ICustomerViewService
{
    public async Task<CustomerViewDto?> GetAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var hash = CustomerShareLinkService.HashOf(token);
        var link = await db.Set<CustomerShareLink>().FirstOrDefaultAsync(l => l.TokenHash == hash, cancellationToken);
        if (link is null || link.RevokedAtUtc is not null || link.ExpiresAtUtc <= now)
        {
            return null;
        }

        var contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == link.ContractId, cancellationToken);
        if (contract is null)
        {
            return null;
        }

        link.LastViewedAtUtc = now;
        link.ViewCount++;
        await db.SaveChangesAsync(cancellationToken);

        var customer = await db.Set<Customer>().AsNoTracking().Where(c => c.Id == contract.CustomerId).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken) ?? "";
        var company = await db.Set<Workbench.Domain.Companies.Company>().AsNoTracking().Where(c => c.Id == contract.CompanyId).Select(c => c.LegalName).FirstOrDefaultAsync(cancellationToken) ?? "";
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contract.Id && c.IsExercised).OrderBy(c => c.Order).ThenBy(c => c.Number).ToListAsync(cancellationToken);
        var invoices = (await db.Set<Invoice>().AsNoTracking()
                .Where(i => i.ContractId == contract.Id && (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.PartiallyPaid || i.Status == InvoiceStatus.Paid))
                .ToListAsync(cancellationToken))
            .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Sequence).ToList();

        decimal Open(Invoice i) => i.Total - i.AmountPaid - i.AmountCredited;
        var rows = invoices.Select(i => new CustomerInvoiceDto(i.Number, i.InvoiceDate, i.PeriodFrom, i.PeriodTo, i.DueDate, i.Total, i.AmountPaid,
            i.Status switch { InvoiceStatus.Paid => "Paid", InvoiceStatus.PartiallyPaid => "Partially paid", _ => "Awaiting payment" },
            i.Status != InvoiceStatus.Paid && i.DueDate < today && Open(i) > 0)).ToList();

        return new CustomerViewDto(contract.ContractNumber, contract.Name, customer, company, contract.PeriodStart, contract.PeriodEnd,
            contract.CeilingValue, contract.FundedValue, invoices.Sum(i => i.Total), invoices.Sum(i => i.AmountPaid), invoices.Sum(Open),
            clins.Select(c => new CustomerClinDto(c.Number, c.Description, c.CeilingValue, c.FundedValue)).ToList(), rows, now);
    }
}
