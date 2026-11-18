using Workbench.Domain.Billing;

namespace Workbench.Application.Billing;

/// <summary>
/// The small, public part of invoicing that other features build on and that stays in core: numbering, input cleaning and the
/// live-invoices query. The government contracting module's progress payments use it, and so does invoice generation.
/// </summary>
public static class BillingKernel
{
    /// <summary>The audit entity type invoice events are recorded under.</summary>
    public const string AuditEntityType = nameof(Invoice);

    public static async Task<(int Sequence, string Number)> NextNumberAsync(IWorkbenchDbContext db, BillingContext ctx, CancellationToken ct, DateOnly? date = null)
    {
        // In the invoice's own unit of work, so invoice numbers are gapless (a failed save allocates nothing).
        var prefix = ctx.Settings?.InvoicePrefix ?? DefaultPrefix(ctx.Contract.ContractNumber);
        var allocated = await Customization.DocumentNumbers.NextInUnitOfWorkAsync(db, Customization.DocumentNumberTypes.Invoice, new(ctx.Contract.CompanyId, date, prefix), date ?? DateOnly.FromDateTime(DateTime.UtcNow), ct);
        return ((int)allocated.Sequence, allocated.Number);
    }

    /// <summary>The contract number without punctuation, at most 16 characters (so the invoice number fits WAWF's 22).</summary>
    public static string DefaultPrefix(string contractNumber)
    {
        var compact = new string(contractNumber.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return compact.Length == 0 ? "INV" : compact[..Math.Min(16, compact.Length)];
    }

    public static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > max ? throw new ArgumentException($"Keep it to {max} characters.") : trimmed;
    }
}
