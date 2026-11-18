using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>An item (a part) as the rest of the product reads it: enough to name it on a quote, an order or an EDI document.</summary>
public sealed record ItemRef(Guid Id, string Number, string Description, string UnitOfMeasure);

/// <summary>A vendor as the rest of the product reads it.</summary>
public sealed record VendorRef(Guid Id, string Code, string Name);

/// <summary>
/// <b>How core reads items.</b> Sourcing (requests for quote), the supplier portal, EDI and AP credits name the part a line is for; they ask
/// this reader instead of the parts table, so the materials module can own parts. The default knows no items (a host without the materials module
/// has no parts); the materials module registers its own after the host's (the last registration wins), exactly like <c>IInvoiceFinancing</c>.
/// Reads use the caller's unit of work.
/// </summary>
public interface IItemReader
{
    /// <summary>The items with these ids; an id with no item is simply not in the result.</summary>
    Task<IReadOnlyDictionary<Guid, ItemRef>> ReadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The item each purchase order line buys when the item is a part bought through the materials module's purchase links (a line with none is simply not in the result).</summary>
    Task<IReadOnlyDictionary<Guid, ItemRef>> ReadForPurchaseOrderLinesAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> purchaseOrderLineIds, CancellationToken cancellationToken);
}

/// <summary>
/// A document of another module that sits on an AP bill line and so stops the bill from being voided (for example a landed-cost charge the materials
/// module allocated from it). Modules register these; the bill's void check asks each one.
/// </summary>
public interface IBillLineGuard
{
    /// <summary>When something of the module depends on these bill lines, what to tell the user, completing "A line of {bill} ..."; otherwise null.</summary>
    Task<string?> BlockingReasonAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> billLineIds, CancellationToken cancellationToken);
}

/// <summary>
/// <b>How core reads vendors where it is not the purchasing code itself.</b> Card programs, for one, name a vendor. The default reads the
/// built-in vendors; an accounting module that owns them registers its own after the host's.
/// </summary>
public interface IVendorReader
{
    Task<IReadOnlyDictionary<Guid, VendorRef>> ReadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}

/// <summary>The default: no items (parts belong to the materials module).</summary>
public sealed class NoItemReader : IItemReader
{
    public static NoItemReader Instance { get; } = new();

    public Task<IReadOnlyDictionary<Guid, ItemRef>> ReadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ItemRef>>(new Dictionary<Guid, ItemRef>());

    public Task<IReadOnlyDictionary<Guid, ItemRef>> ReadForPurchaseOrderLinesAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> purchaseOrderLineIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ItemRef>>(new Dictionary<Guid, ItemRef>());
}

/// <summary>A return of rejected material to a vendor, as accounts payable reads it to credit the vendor (an AP debit memo).</summary>
public sealed record VendorReturn(
    Guid Id, string Number, Guid VendorId, DateOnly Date, decimal Amount, Guid? PurchaseOrderLineId, string? ReturnAuthorization,
    string Description, string ClearingAccountNumber, bool IsOpen, string Status);

/// <summary>
/// <b>Where payables finds returns to vendors.</b> A quality module that rejects material and returns it keeps the return (its debit memo);
/// "AP debit memo from a quality return" and voiding that memo read and mark it through this source. With none registered there are no returns.
/// </summary>
public interface IVendorReturnSource
{
    /// <summary>The return, or null when there is none with this id.</summary>
    Task<VendorReturn?> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken cancellationToken);

    /// <summary>The return is now credited on an AP debit memo (<paramref name="reference"/> names it).</summary>
    Task MarkAppliedAsync(IWorkbenchDbContext db, Guid id, string reference, string? actor, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>The AP debit memo was voided: the return is open again.</summary>
    Task ReopenAsync(IWorkbenchDbContext db, Guid id, string reason, CancellationToken cancellationToken);
}
/// <summary>The built-in vendors, read straight from the table.</summary>
public sealed class VendorTableReader : IVendorReader
{
    public static VendorTableReader Instance { get; } = new();

    public async Task<IReadOnlyDictionary<Guid, VendorRef>> ReadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? new Dictionary<Guid, VendorRef>()
            : await db.Set<Vendor>().AsNoTracking().Where(v => ids.Contains(v.Id))
                .Select(v => new VendorRef(v.Id, v.Code, v.Name)).ToDictionaryAsync(v => v.Id, cancellationToken);
}
