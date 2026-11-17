using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Workbench.Application.AccountingExport;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;

namespace Workbench.Application.AccountingLive;

/// <summary>
/// Pushes manual journals, invoices (ACCREC) and bills (ACCPAY) to Xero. Sources: Xero Developer, the Accounting API as published in Xero's
/// OpenAPI description (github.com/XeroAPI/Xero-OpenAPI, xero_accounting.yaml) and the OAuth 2.0 guides at developer.xero.com/documentation/guides/oauth2:
/// <list type="bullet">
/// <item>Base <c>https://api.xero.com/api.xro/2.0</c>, every call carrying <c>Authorization: Bearer</c> and <c>xero-tenant-id</c>
/// (auth-flow guide, step 6).</item>
/// <item>ManualJournals: <c>Narration</c> (required), <c>Date</c>, <c>Status</c> (DRAFT, POSTED, ...), <c>LineAmountTypes</c>, <c>JournalLines[]</c> with
/// <c>LineAmount</c> (debits positive, credits negative), <c>AccountCode</c>, <c>Description</c>, <c>Tracking[]</c> (at most two per line).</item>
/// <item><c>summarizeErrors=false</c>: answer 200 with a mix of created objects and objects with validation errors, so one bad record doesn't fail a batch;
/// <c>Idempotency-Key</c> header (128 characters) to retry safely.</item>
/// <item>Limits (OAuth 2.0 API limits page): 5 concurrent calls, 60 a minute and 1,000 (starter) or 5,000 (higher tiers) a day per tenant; a 429 carries
/// <c>Retry-After</c> and <c>X-Rate-Limit-Problem</c>; batch in bundles of up to 50; 10 MB request limit.</item>
/// <item>Scopes: accounting.invoices, accounting.manualjournals, accounting.contacts, accounting.settings (granular scopes, which Web apps hold since March 2026).</item>
/// </list>
/// Invoices and bills go in as drafts unless <c>Accounting:Xero:PostAsDraft</c> is false, journals likewise (the accountant approves them in Xero).
/// Not sent: credit notes and vendor credits, tax types (lines carry <c>LineAmountTypes: NoTax</c>), and journal lines on bank, receivable or payable accounts,
/// which Xero refuses in manual journals: such entries fail with Xero's own message and stay unexported.
/// The Invoice, Contact, Account and TrackingCategory field names were taken from the OpenAPI description, not from a live organisation.
/// </summary>
public sealed class XeroPublisher(IOptions<AccountingProviderOptions> options) : IAccountingPublisher
{
    public const int BatchSize = 50;
    private const string Api = "https://api.xero.com/api.xro/2.0";

    public AccountingTarget Target => AccountingTarget.Xero;

    public IReadOnlyList<AccountingExportKind> Kinds { get; } = [AccountingExportKind.JournalEntries, AccountingExportKind.Invoices, AccountingExportKind.Bills];

    private string DocStatus(string posted) => options.Value.Xero.PostAsDraft ? "DRAFT" : posted;

    private static string? FindIn(ProviderCatalog c, AccountingMapKind kind, Resolved r) => kind switch
    {
        AccountingMapKind.Account => (string.IsNullOrWhiteSpace(r.Code) ? null : c.Find(kind, r.Code)) ?? c.Find(kind, r.Name),
        AccountingMapKind.Class => string.IsNullOrWhiteSpace(r.Code) ? null : c.Find(kind, $"{r.Code}|{r.Name}"),
        _ => c.Find(kind, r.Name),
    };

    public async Task<IReadOnlyList<AccountingMissingDto>> FindMissingAsync(AccountingApi api, ExportInput input, bool createMissingContacts, CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync(api, input.Data, ct);
        return PublishSupport.Missing(input.Data, (k, r) => FindIn(catalog, k, r), createMissingContacts, items: false);
    }

    private static HttpRequestMessage Get(ProviderSession s, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
        request.Headers.TryAddWithoutValidation("xero-tenant-id", s.CompanyId);
        return request;
    }

    private static HttpRequestMessage Post(ProviderSession s, string path, JsonNode body, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Api + path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };
        request.Headers.TryAddWithoutValidation("xero-tenant-id", s.CompanyId);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
    }

    private async Task<ProviderCatalog> LoadCatalogAsync(AccountingApi api, ExportDataset data, CancellationToken ct)
    {
        var catalog = new ProviderCatalog();
        var used = PublishSupport.NamesUsed(data, false).Select(n => n.Kind).ToHashSet();
        if (used.Contains(AccountingMapKind.Account))
        {
            foreach (var a in await ListAsync(api, "/Accounts", "Accounts", false, ct))
            {
                var code = a["Code"]?.GetValue<string>();
                if (code is null || a["Status"]?.GetValue<string>() is "ARCHIVED")
                {
                    continue;
                }

                catalog.Add(AccountingMapKind.Account, code, code);
                catalog.Add(AccountingMapKind.Account, a["Name"]?.GetValue<string>(), code);
            }
        }

        if (used.Contains(AccountingMapKind.Customer) || used.Contains(AccountingMapKind.Vendor))
        {
            foreach (var c in await ListAsync(api, "/Contacts?summaryOnly=true", "Contacts", true, ct))
            {
                var id = c["ContactID"]?.GetValue<string>();
                var name = c["Name"]?.GetValue<string>();
                if (id is null)
                {
                    continue;
                }

                catalog.Add(AccountingMapKind.Customer, name, id);
                catalog.Add(AccountingMapKind.Vendor, name, id);
            }
        }

        if (used.Contains(AccountingMapKind.Class))
        {
            foreach (var t in await ListAsync(api, "/TrackingCategories", "TrackingCategories", false, ct))
            {
                var category = t["Name"]?.GetValue<string>();
                foreach (var o in t["Options"]?.AsArray() ?? [])
                {
                    catalog.Add(AccountingMapKind.Class, $"{category}|{o?["Name"]?.GetValue<string>()}", $"{category}|{o?["Name"]?.GetValue<string>()}");
                }
            }
        }

        return catalog;
    }

    private static async Task<List<JsonObject>> ListAsync(AccountingApi api, string path, string property, bool paged, CancellationToken ct)
    {
        var rows = new List<JsonObject>();
        for (var page = 1; ; page++)
        {
            var url = paged ? path + (path.Contains('?') ? "&" : "?") + "page=" + page : path;
            var response = await api.SendAsync(s => Get(s, url), ct);
            if (response.Status != 200)
            {
                throw new AccountingApiException(response.Status, $"Xero wouldn't list {property}: {ErrorText(response.Body)}", response.Body);
            }

            var items = JsonNode.Parse(response.Body)?[property]?.AsArray().Select(n => n!.AsObject()).ToList() ?? [];
            rows.AddRange(items);
            if (!paged || items.Count < 100)
            {
                return rows;
            }
        }
    }

    public async Task<PublishOutcome> PublishAsync(AccountingApi api, PublishRequest request, CancellationToken ct)
    {
        var data = request.Input.Data;
        var catalog = await LoadCatalogAsync(api, data, ct);
        var contacts = 0;
        var contactFailures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.CreateMissingContacts)
        {
            var names = PublishSupport.NamesUsed(data, false).Where(n => n.Kind is AccountingMapKind.Customer or AccountingMapKind.Vendor && FindIn(catalog, n.Kind, n.Name) is null).Select(n => n.Name.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var name in names)
            {
                var key = PublishSupport.Key("wb-c-", "contact" + name.ToLowerInvariant(), [], _ => 0, 128);
                var body = new JsonObject { ["Contacts"] = new JsonArray(new JsonObject { ["Name"] = PublishSupport.Clip(name, 255) }) };
                var response = await api.SendAsync(s => Post(s, "/Contacts?summarizeErrors=false", body, key), ct);
                var created = response.Status == 200 ? JsonNode.Parse(response.Body)?["Contacts"]?.AsArray().FirstOrDefault()?.AsObject() : null;
                if (created?["ContactID"]?.GetValue<string>() is { } id && !HasErrors(created))
                {
                    catalog.Add(AccountingMapKind.Customer, name, id);
                    catalog.Add(AccountingMapKind.Vendor, name, id);
                    contacts++;
                }
                else
                {
                    contactFailures[name] = $"Couldn't create contact \"{name}\" in Xero: {(created is null ? ErrorText(response.Body) : ValidationText(created))}";
                }
            }
        }

        var results = request.Input.Kind switch
        {
            AccountingExportKind.JournalEntries => await SendAsync(api, "ManualJournals", "ManualJournalID", "/ManualJournals?summarizeErrors=false",
                data.Journals.Select(j => (j.Id, j.Number, Build: Journal(j, catalog))).ToList(), request, ct),
            AccountingExportKind.Invoices => await SendAsync(api, "Invoices", "InvoiceID", "/Invoices?summarizeErrors=false",
                data.Invoices.Select(i => (i.Id, i.Number, Build: i.IsCreditMemo
                    ? (null, "NOTSENT: Xero takes credit notes as a separate entity, which Workbench doesn't push yet.")
                    : contactFailures.TryGetValue(i.Customer.Name, out var f) ? (null, f) : Invoice(i, catalog))).ToList(), request, ct),
            AccountingExportKind.Bills => await SendAsync(api, "Invoices", "InvoiceID", "/Invoices?summarizeErrors=false",
                data.Bills.Select(b => (b.Id, b.Number, Build: b.IsCredit
                    ? (null, "NOTSENT: Xero takes vendor credits as a separate entity, which Workbench doesn't push yet.")
                    : contactFailures.TryGetValue(b.Vendor.Name, out var f) ? (null, f) : Bill(b, catalog))).ToList(), request, ct),
            _ => throw new NotSupportedException($"Xero can't take {request.Input.Kind} by push."),
        };
        return new PublishOutcome(results, contacts);
    }

    private sealed class MissingException(string message) : Exception(message);

    private static (JsonObject?, string?) Wrap(Func<JsonObject> build)
    {
        try
        {
            return (build(), null);
        }
        catch (MissingException ex)
        {
            return (null, ex.Message);
        }
    }

    private static JsonArray Tracking(Resolved? cls, ProviderCatalog c) =>
        cls is { } r && !string.IsNullOrWhiteSpace(r.Code) && c.Find(AccountingMapKind.Class, $"{r.Code}|{r.Name}") is not null
            ? new JsonArray(new JsonObject { ["Name"] = r.Code, ["Option"] = r.Name })
            : [];

    private (JsonObject?, string?) Journal(JournalDoc j, ProviderCatalog c) => Wrap(() =>
    {
        var lines = new JsonArray();
        foreach (var l in j.Lines)
        {
            var code = FindIn(c, AccountingMapKind.Account, l.Account) ?? throw new MissingException($"Account \"{l.Account.Name}\" isn't in Xero.");
            var line = new JsonObject { ["LineAmount"] = l.Debit > 0 ? l.Debit : -l.Credit, ["AccountCode"] = code, ["Description"] = PublishSupport.Clip(string.IsNullOrWhiteSpace(l.Memo) ? j.Description : l.Memo, 4000) };
            var tracking = Tracking(l.Class, c);
            if (tracking.Count > 0)
            {
                line["Tracking"] = tracking;
            }

            lines.Add(line);
        }

        return new JsonObject
        {
            ["Narration"] = PublishSupport.Clip($"{j.Number}: {j.Description}", 3000),
            ["Date"] = PublishSupport.Date(j.Date),
            ["Status"] = DocStatus("POSTED"),
            ["LineAmountTypes"] = "NoTax",
            ["JournalLines"] = lines,
        };
    });

    private (JsonObject?, string?) Invoice(InvoiceDoc i, ProviderCatalog c) => Wrap(() =>
    {
        var contact = FindIn(c, AccountingMapKind.Customer, i.Customer) ?? throw new MissingException($"Customer \"{i.Customer.Name}\" isn't in Xero.");
        var lines = new JsonArray();
        foreach (var l in i.Lines)
        {
            var code = FindIn(c, AccountingMapKind.Account, l.Account) ?? throw new MissingException($"Account \"{l.Account.Name}\" isn't in Xero.");
            var (qty, rate) = PublishSupport.QuantityAndRate(l.Quantity, l.Rate, l.Amount);
            var line = new JsonObject { ["Description"] = PublishSupport.Clip(l.Description, 4000), ["Quantity"] = qty, ["UnitAmount"] = rate, ["AccountCode"] = code };
            var tracking = Tracking(l.Class, c);
            if (tracking.Count > 0)
            {
                line["Tracking"] = tracking;
            }

            lines.Add(line);
        }

        return new JsonObject
        {
            ["Type"] = "ACCREC",
            ["Contact"] = new JsonObject { ["ContactID"] = contact },
            ["InvoiceNumber"] = PublishSupport.Clip(i.Number, 255),
            ["Reference"] = PublishSupport.Clip(i.Reference ?? "", 255),
            ["Date"] = PublishSupport.Date(i.Date),
            ["DueDate"] = PublishSupport.Date(i.DueDate),
            ["LineAmountTypes"] = "NoTax",
            ["Status"] = DocStatus("AUTHORISED"),
            ["LineItems"] = lines,
        };
    });

    private (JsonObject?, string?) Bill(BillDoc b, ProviderCatalog c) => Wrap(() =>
    {
        var contact = FindIn(c, AccountingMapKind.Vendor, b.Vendor) ?? throw new MissingException($"Vendor \"{b.Vendor.Name}\" isn't in Xero.");
        var lines = new JsonArray();
        foreach (var l in b.Lines)
        {
            var code = FindIn(c, AccountingMapKind.Account, l.Account) ?? throw new MissingException($"Account \"{l.Account.Name}\" isn't in Xero.");
            var line = new JsonObject { ["Description"] = PublishSupport.Clip(l.Description, 4000), ["Quantity"] = 1, ["UnitAmount"] = l.Amount, ["AccountCode"] = code };
            var tracking = Tracking(l.Class, c);
            if (tracking.Count > 0)
            {
                line["Tracking"] = tracking;
            }

            lines.Add(line);
        }

        return new JsonObject
        {
            ["Type"] = "ACCPAY",
            ["Contact"] = new JsonObject { ["ContactID"] = contact },
            ["InvoiceNumber"] = PublishSupport.Clip(string.IsNullOrWhiteSpace(b.VendorInvoiceNumber) ? b.Number : b.VendorInvoiceNumber, 255),
            ["Reference"] = PublishSupport.Clip(b.Number, 255),
            ["Date"] = PublishSupport.Date(b.Date),
            ["DueDate"] = PublishSupport.Date(b.DueDate),
            ["LineAmountTypes"] = "NoTax",
            ["Status"] = DocStatus("AUTHORISED"),
            ["LineItems"] = lines,
        };
    });

    private async Task<List<PublishRecordResult>> SendAsync(
        AccountingApi api, string collection, string idProperty, string path, List<(Guid Id, string Number, (JsonObject? Body, string? Error) Build)> items, PublishRequest request, CancellationToken ct)
    {
        var results = new List<PublishRecordResult>();
        var ready = new List<(Guid Id, string Number, JsonObject Body)>();
        foreach (var (id, number, build) in items)
        {
            if (build.Body is null)
            {
                var error = build.Error ?? "Couldn't build the record.";
                results.Add(error.StartsWith("NOTSENT: ", StringComparison.Ordinal) ? PublishSupport.NotSent(id, number, error["NOTSENT: ".Length..]) : PublishSupport.Failed(id, number, error));
            }
            else
            {
                ready.Add((id, number, build.Body));
            }
        }

        foreach (var chunk in ready.Chunk(BatchSize))
        {
            var key = PublishSupport.Key("wb-", collection + idProperty, chunk.Select(c => c.Id), request.AttemptOf, 128);
            var payload = new JsonObject { [collection] = new JsonArray(chunk.Select(c => (JsonNode)c.Body.DeepClone()).ToArray()) };
            var response = await api.SendAsync(s => Post(s, path, payload, key), ct);
            if (response.Status != 200)
            {
                var text = ErrorText(response.Body);
                results.AddRange(chunk.Select(c => PublishSupport.Failed(c.Id, c.Number, $"Xero refused the batch ({response.Status}): {text}")));
                continue;
            }

            var answers = JsonNode.Parse(response.Body)?[collection]?.AsArray().Select(n => n!.AsObject()).ToList() ?? [];
            for (var n = 0; n < chunk.Length; n++)
            {
                var c = chunk[n];
                if (answers.Count != chunk.Length)
                {
                    results.Add(PublishSupport.Failed(c.Id, c.Number, "Xero's answer didn't match what was sent. Check the books for this record before retrying."));
                }
                else if (answers[n][idProperty]?.GetValue<string>() is { } id && !HasErrors(answers[n]))
                {
                    results.Add(new PublishRecordResult(c.Id, c.Number, AccountingPushItemStatus.Created, id, answers[n]["InvoiceNumber"]?.GetValue<string>(), null));
                }
                else
                {
                    results.Add(PublishSupport.Failed(c.Id, c.Number, ValidationText(answers[n])));
                }
            }
        }

        return results;
    }

    private static bool HasErrors(JsonObject item) =>
        item["HasErrors"]?.GetValue<bool>() == true || item["ValidationErrors"]?.AsArray().Count > 0 || item["StatusAttributeString"]?.GetValue<string>() == "ERROR";

    private static string ValidationText(JsonObject item)
    {
        var messages = item["ValidationErrors"]?.AsArray().Select(e => e?["Message"]?.GetValue<string>()).Where(m => !string.IsNullOrWhiteSpace(m)).ToList() ?? [];
        return messages.Count > 0 ? string.Join(" ", messages) : "Xero rejected the record without saying why.";
    }

    /// <summary>A Xero error body: <c>Message</c> and <c>Elements[].ValidationErrors[]</c>, or the raw text.</summary>
    internal static string ErrorText(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            var messages = new List<string?> { root?["Message"]?.GetValue<string>() };
            messages.AddRange(root?["Elements"]?.AsArray().SelectMany(e => e?["ValidationErrors"]?.AsArray().Select(v => v?["Message"]?.GetValue<string>()) ?? []) ?? []);
            var text = string.Join(" ", messages.Where(m => !string.IsNullOrWhiteSpace(m)));
            if (text.Length > 0)
            {
                return PublishSupport.Clip(text, 500);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            // Not JSON.
        }

        return PublishSupport.Clip(body, 500);
    }
}
