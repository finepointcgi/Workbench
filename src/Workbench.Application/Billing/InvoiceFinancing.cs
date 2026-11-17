using Workbench.Domain.Billing;

namespace Workbench.Application.Billing;

/// <summary>
/// The invoice-financing hook: everything invoicing needs from contract financing (government progress payments and
/// performance-based payments) without knowing how it works. The contract lives here, in core; the government contracting
/// module (<c>govcon</c>) registers the implementation. A host without that module gets <see cref="NoInvoiceFinancing"/>,
/// so invoices are generated, approved and voided exactly as for a contract with no financing.
/// </summary>
public interface IInvoiceFinancing
{
    /// <summary>The contracts that bill performance-based payment events (offered as an invoice type on the contract).</summary>
    Task<IReadOnlySet<Guid>> PerformanceBasedContractsAsync(IWorkbenchDbContext db, CancellationToken ct);

    /// <summary>The lines of a performance-based payment invoice: completed, unbilled events through <paramref name="to"/>. Financing lines carry no CLIN.</summary>
    Task<Generation> GeneratePerformanceEventsAsync(IWorkbenchDbContext db, BillingContext ctx, DateOnly to, Guid? excludeInvoiceId, List<string> warnings, CancellationToken ct);

    /// <summary>
    /// Adds the liquidation line to a delivery invoice's generated lines (and a note), or returns <paramref name="gen"/> unchanged.
    /// <paramref name="manual"/> are the draft's manual lines, which count toward the delivery amount.
    /// </summary>
    Task<Generation> AddLiquidationAsync(IWorkbenchDbContext db, BillingContext ctx, InvoiceType type, Generation gen, IEnumerable<InvoiceLine> manual, Guid? excludeInvoiceId, CancellationToken ct);

    /// <summary>Refuses approving a delivery invoice whose liquidation is more than what is outstanding now. Throws <see cref="InvalidOperationException"/>.</summary>
    Task EnsureCanApproveAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct);

    /// <summary>Refuses voiding a financing invoice that deliveries have already liquidated against. Throws <see cref="InvalidOperationException"/>.</summary>
    Task EnsureCanVoidAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct);

    /// <summary>Links performance-based payment events to the invoice that bills them (<paramref name="invoiceId"/>), or releases them (null) so they can be billed again.</summary>
    Task BindEventsAsync(IWorkbenchDbContext db, Guid? invoiceId, IReadOnlyCollection<Guid> eventIds, CancellationToken ct);

    /// <summary>The printed form the contract's invoices default to (<c>"sf1034"</c> for a government voucher), or null for the standard invoice layouts.</summary>
    Task<string?> PreferredPrintFormAsync(IWorkbenchDbContext db, Guid contractId, CancellationToken ct);

    /// <summary>A draft is being deleted: remove what the financing side keeps about it (the SF 1443 request, the PIEE submission) in the same unit of work.</summary>
    Task OnDraftDeletedAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct);
}

/// <summary>The default: no contract financing. Nothing is added, refused or removed.</summary>
public sealed class NoInvoiceFinancing : IInvoiceFinancing
{
    public static NoInvoiceFinancing Instance { get; } = new();

    public Task<IReadOnlySet<Guid>> PerformanceBasedContractsAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

    public Task<Generation> GeneratePerformanceEventsAsync(IWorkbenchDbContext db, BillingContext ctx, DateOnly to, Guid? excludeInvoiceId, List<string> warnings, CancellationToken ct)
    {
        warnings.Add($"Contract {ctx.Contract.ContractNumber} doesn't use performance-based payments. Turn them on in its billing settings.");
        return Task.FromResult(new Generation([], warnings, null));
    }

    public Task<Generation> AddLiquidationAsync(IWorkbenchDbContext db, BillingContext ctx, InvoiceType type, Generation gen, IEnumerable<InvoiceLine> manual, Guid? excludeInvoiceId, CancellationToken ct) =>
        Task.FromResult(gen);

    public Task EnsureCanApproveAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct) => Task.CompletedTask;

    public Task EnsureCanVoidAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct) => Task.CompletedTask;

    public Task BindEventsAsync(IWorkbenchDbContext db, Guid? invoiceId, IReadOnlyCollection<Guid> eventIds, CancellationToken ct) => Task.CompletedTask;

    public Task<string?> PreferredPrintFormAsync(IWorkbenchDbContext db, Guid contractId, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task OnDraftDeletedAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct) => Task.CompletedTask;
}
