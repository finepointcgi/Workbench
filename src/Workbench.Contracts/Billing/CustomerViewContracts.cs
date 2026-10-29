namespace Workbench.Contracts.Billing;

/// <summary>A link to one contract's status for the customer. The token itself is never shown again after it is created.</summary>
public sealed record CustomerShareLinkDto(
    Guid Id,
    string Label,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    DateTime? LastViewedAtUtc,
    int ViewCount,
    bool IsActive);

/// <param name="Label">Who it's for, shown in the list ("Army CO, J. Rivera").</param>
/// <param name="ValidDays">How long the link works; 90 by default, at most 365.</param>
public sealed record CreateCustomerShareLinkRequest(string Label, int? ValidDays = null);

/// <param name="Path">The address to send, relative to the site ("c/{token}"); the only time it is returned.</param>
public sealed record CreatedCustomerShareLinkDto(CustomerShareLinkDto Link, string Path);

/// <summary>Finance makes, lists and ends the customer's links for a contract.</summary>
public interface ICustomerShareLinkService
{
    Task<IReadOnlyList<CustomerShareLinkDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<CreatedCustomerShareLinkDto> CreateAsync(Guid contractId, CreateCustomerShareLinkRequest request, CancellationToken cancellationToken = default);

    Task<CustomerShareLinkDto> RevokeAsync(Guid contractId, Guid linkId, CancellationToken cancellationToken = default);
}

public sealed record CustomerClinDto(string Number, string Description, decimal Ceiling, decimal Funded);

/// <summary>An invoice as the customer sees it: only sent and paid ones, never drafts or internal notes.</summary>
public sealed record CustomerInvoiceDto(string Number, DateOnly InvoiceDate, DateOnly PeriodFrom, DateOnly PeriodTo, DateOnly DueDate, decimal Total, decimal Paid, string Status, bool IsOverdue);

/// <summary>
/// What a customer sees through a link: the contract's value and funding, what has been billed and paid, and the invoices.
/// No costs, rates, people, margins or plan.
/// </summary>
public sealed record CustomerViewDto(
    string ContractNumber,
    string ContractName,
    string CustomerName,
    string CompanyName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal Ceiling,
    decimal Funded,
    decimal Billed,
    decimal Paid,
    decimal Outstanding,
    IReadOnlyList<CustomerClinDto> Clins,
    IReadOnlyList<CustomerInvoiceDto> Invoices,
    DateTime AsOfUtc);

/// <summary>The link-holder's view. Anonymous: it takes the token and nothing else, and answers null for any token that isn't a live link.</summary>
public interface ICustomerViewService
{
    Task<CustomerViewDto?> GetAsync(string token, CancellationToken cancellationToken = default);
}
