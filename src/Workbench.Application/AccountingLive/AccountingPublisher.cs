using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Application.AccountingExport;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;

namespace Workbench.Application.AccountingLive;

/// <param name="AttemptOf">How many earlier attempts a record has had. It goes into the idempotency key, so a replay of a lost response is deduplicated by the provider but a retry after a fix is a new request.</param>
public sealed record PublishRequest(ExportInput Input, bool CreateMissingContacts, Func<Guid, int> AttemptOf);

public sealed record PublishRecordResult(Guid RecordId, string Number, string Status, string? ProviderId, string? ProviderNumber, string? Message);

public sealed record PublishOutcome(IReadOnlyList<PublishRecordResult> Records, int ContactsCreated);

/// <summary>
/// Sends an <see cref="ExportInput"/> straight to a package's API instead of writing a file: the live counterpart of
/// <see cref="IAccountingExporter"/>, taking the same resolved dataset. It only talks to the provider; the run, the exported-record marks,
/// the provider ids and the history are written by <see cref="AccountingPushService"/>.
/// </summary>
public interface IAccountingPublisher
{
    AccountingTarget Target { get; }
    IReadOnlyList<AccountingExportKind> Kinds { get; }

    /// <summary>Names the dataset uses that don't exist in the provider (customers and vendors may be created on request).</summary>
    Task<IReadOnlyList<AccountingMissingDto>> FindMissingAsync(AccountingApi api, ExportInput input, bool createMissingContacts, CancellationToken ct);

    Task<PublishOutcome> PublishAsync(AccountingApi api, PublishRequest request, CancellationToken ct);
}

/// <summary>What the provider already has, by kind and lower-cased name.</summary>
public sealed class ProviderCatalog
{
    private readonly Dictionary<(AccountingMapKind, string), string> _ids = [];

    public void Add(AccountingMapKind kind, string? name, string id)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _ids[(kind, name.Trim().ToLowerInvariant())] = id;
        }
    }

    public string? Find(AccountingMapKind kind, string? name) =>
        !string.IsNullOrWhiteSpace(name) && _ids.TryGetValue((kind, name.Trim().ToLowerInvariant()), out var id) ? id : null;
}

internal static class PublishSupport
{
    public static string Key(string prefix, string kind, IEnumerable<Guid> ids, Func<Guid, int> attemptOf, int max)
    {
        var text = kind + "|" + string.Join(",", ids.OrderBy(i => i).Select(i => $"{i:N}:{attemptOf(i)}"));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return (prefix + hash)[..Math.Min(max, prefix.Length + hash.Length)];
    }

    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Clip(string? text, int max) => text is null ? "" : text.Length > max ? text[..max] : text;

    public static PublishRecordResult Failed(Guid id, string number, string message) => new(id, number, AccountingPushItemStatus.Failed, null, null, Clip(message, 1900));

    public static PublishRecordResult NotSent(Guid id, string number, string message) => new(id, number, AccountingPushItemStatus.NotSent, null, null, message);

    /// <summary>Amount and quantity × rate agree to the cent, so quantity and rate may be sent; otherwise one unit at the amount.</summary>
    public static (decimal Quantity, decimal Rate) QuantityAndRate(decimal quantity, decimal rate, decimal amount) =>
        quantity != 0 && Math.Abs(Math.Round(quantity * rate, 2) - amount) <= 0.005m ? (quantity, rate) : (1m, amount);

    public static IEnumerable<(AccountingMapKind Kind, Resolved Name)> NamesUsed(ExportDataset data, bool items = true)
    {
        foreach (var j in data.Journals)
        {
            foreach (var l in j.Lines)
            {
                yield return (AccountingMapKind.Account, l.Account);
                if (l.Class is { } c)
                {
                    yield return (AccountingMapKind.Class, c);
                }
            }
        }

        foreach (var i in data.Invoices.Where(i => !i.IsCreditMemo))
        {
            yield return (AccountingMapKind.Customer, i.Customer);
            foreach (var l in i.Lines)
            {
                if (items)
                {
                    yield return (AccountingMapKind.Item, l.Item);
                }
                else
                {
                    yield return (AccountingMapKind.Account, l.Account); // Xero lines carry the revenue account instead of a product
                }

                if (l.Class is { } c)
                {
                    yield return (AccountingMapKind.Class, c);
                }
            }
        }

        foreach (var b in data.Bills.Where(b => !b.IsCredit))
        {
            yield return (AccountingMapKind.Vendor, b.Vendor);
            foreach (var l in b.Lines)
            {
                yield return (AccountingMapKind.Account, l.Account);
                if (l.Class is { } c)
                {
                    yield return (AccountingMapKind.Class, c);
                }
            }
        }
    }

    /// <summary>The shared part of the preview: each used name the provider doesn't know, with whether it will be created.</summary>
    public static IReadOnlyList<AccountingMissingDto> Missing(ExportDataset data, Func<AccountingMapKind, Resolved, string?> find, bool createContacts, bool items = true) =>
        NamesUsed(data, items).Where(n => find(n.Kind, n.Name) is null)
            .Select(n => (n.Kind, n.Name.Name))
            .Distinct()
            .OrderBy(n => n.Kind).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => new AccountingMissingDto(n.Kind, n.Name, createContacts && n.Kind is AccountingMapKind.Customer or AccountingMapKind.Vendor))
            .ToList();

    public static string Content(JsonNode node) => node.ToJsonString();
}
