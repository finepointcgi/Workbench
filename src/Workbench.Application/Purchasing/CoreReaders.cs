using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Materials;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>An item (a part) as the rest of the product reads it: enough to name it on a quote, an order or an EDI document.</summary>
public sealed record ItemRef(Guid Id, string Number, string Description, string UnitOfMeasure);

/// <summary>A vendor as the rest of the product reads it.</summary>
public sealed record VendorRef(Guid Id, string Code, string Name);

/// <summary>
/// <b>How core reads items.</b> Sourcing (requests for quote), the supplier portal, EDI and AP credits name the part a line is for; they ask
/// this reader instead of the parts table, so the materials module can own parts. The default reads the built-in parts; a materials module
/// registers its own after the host's (the last registration wins), exactly like <c>IInvoiceFinancing</c>. Reads use the caller's unit of work.
/// </summary>
public interface IItemReader
{
    /// <summary>The items with these ids; an id with no item is simply not in the result.</summary>
    Task<IReadOnlyDictionary<Guid, ItemRef>> ReadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}

/// <summary>
/// <b>How core reads vendors where it is not the purchasing code itself.</b> Card programs, for one, name a vendor. The default reads the
/// built-in vendors; an accounting module that owns them registers its own after the host's.
/// </summary>
public interface IVendorReader
{
    Task<IReadOnlyDictionary<Guid, VendorRef>> ReadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}

/// <summary>The built-in parts, read straight from the table.</summary>
public sealed class PartItemReader : IItemReader
{
    public static PartItemReader Instance { get; } = new();

    public async Task<IReadOnlyDictionary<Guid, ItemRef>> ReadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? new Dictionary<Guid, ItemRef>()
            : await db.Set<Part>().AsNoTracking().Where(p => ids.Contains(p.Id))
                .Select(p => new ItemRef(p.Id, p.Number, p.Description, p.UnitOfMeasure)).ToDictionaryAsync(p => p.Id, cancellationToken);
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
