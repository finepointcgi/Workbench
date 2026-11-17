using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Application.AccountingExport;
using Workbench.Contracts.AccountingExport;
using Workbench.Contracts.AccountingLive;

namespace Workbench.Application.AccountingLive;

/// <summary>
/// Pushes journal entries, invoices and bills to QuickBooks Online. Sources (Intuit Developer, read when this was written):
/// <list type="bullet">
/// <item>Base URL, <c>sandbox-quickbooks.api.intuit.com</c> or <c>quickbooks.api.intuit.com</c>, path <c>/v3/company/{realmId}/{entity}</c>
/// (https://developer.intuit.com/app/developer/qbo/docs/get-started/create-a-request); <c>minorversion=75</c>, the version older ones are served as
/// (https://developer.intuit.com/app/developer/qbo/docs/learn/explore-the-quickbooks-online-api/minor-versions).</item>
/// <item>Batch (https://developer.intuit.com/app/developer/qbo/docs/api/accounting/all-entities/batch): at most 30 BatchItemRequest per call, at
/// most 40 batch calls a minute per realm, execution order not guaranteed, items independent, each answered as an entity or a Fault, matched by bId.</item>
/// <item>JournalEntry (https://developer.intuit.com/app/developer/qbo/docs/api/accounting/all-entities/journalentry): Line[] with DetailType
/// JournalEntryLineDetail, debits equal credits, A/R lines need a customer and A/P lines a vendor in the line's Entity, DocNumber up to 21 characters.</item>
/// <item><c>requestid</c> query parameter makes writes idempotent (50 characters, 36 on batch), and limits of 500 requests a minute and 10 a second
/// per realm, 429 on excess (https://developer.intuit.com/app/developer/qbo/docs/learn/rest-api-features; Intuit's "API call limits and throttles" page).</item>
/// <item>Entity shapes for Invoice (SalesItemLineDetail with ItemRef, Qty, UnitPrice, ClassRef), Bill (AccountBasedExpenseLineDetail with AccountRef, ClassRef),
/// Customer and Vendor (DisplayName, duplicate-name Fault code 6240) and the <c>select ... STARTPOSITION n MAXRESULTS 1000</c> query follow the
/// QuickBooks Online Accounting API reference. Those entity pages were not all re-read (the page text for Invoice, Bill, Customer and Vendor was not
/// fetched), so they are the first thing to check against a sandbox company.</item>
/// </list>
/// Not sent: credit memos and vendor credits (QuickBooks has CreditMemo and VendorCredit entities; a later change), tax codes (QuickBooks applies the item's
/// default), and journal lines on Accounts Receivable or Payable, which QuickBooks refuses without a customer or vendor: those entries fail with
/// QuickBooks's own message and stay unexported.
/// </summary>
public sealed class QuickBooksPublisher : IAccountingPublisher
{
    public const int BatchSize = 30;
    public const int MinorVersion = 75;
    public const int QueryPage = 1000;

    public AccountingTarget Target => AccountingTarget.QuickBooks;

    public IReadOnlyList<AccountingExportKind> Kinds { get; } = [AccountingExportKind.JournalEntries, AccountingExportKind.Invoices, AccountingExportKind.Bills];

    private static string Base(ProviderSession s) =>
        (s.IsSandbox ? "https://sandbox-quickbooks.api.intuit.com" : "https://quickbooks.api.intuit.com") + "/v3/company/" + Uri.EscapeDataString(s.CompanyId);

    private static string Find(ProviderCatalog c, AccountingMapKind kind, Resolved r) => c.Find(kind, r.Name) ?? "";

    public async Task<IReadOnlyList<AccountingMissingDto>> FindMissingAsync(AccountingApi api, ExportInput input, bool createMissingContacts, CancellationToken ct)
    {
        var catalog = await LoadCatalogAsync(api, input.Data, ct);
        return PublishSupport.Missing(input.Data, (k, r) => catalog.Find(k, r.Name), createMissingContacts);
    }

    private async Task<ProviderCatalog> LoadCatalogAsync(AccountingApi api, ExportDataset data, CancellationToken ct)
    {
        var catalog = new ProviderCatalog();
        var used = PublishSupport.NamesUsed(data).Select(n => n.Kind).ToHashSet();
        if (used.Contains(AccountingMapKind.Account))
        {
            foreach (var a in await QueryAllAsync(api, "Account", "Id, Name, FullyQualifiedName", ct))
            {
                var id = a["Id"]!.GetValue<string>();
                catalog.Add(AccountingMapKind.Account, a["FullyQualifiedName"]?.GetValue<string>(), id);
                catalog.Add(AccountingMapKind.Account, a["Name"]?.GetValue<string>(), id);
            }
        }

        if (used.Contains(AccountingMapKind.Customer))
        {
            foreach (var c in await QueryAllAsync(api, "Customer", "Id, DisplayName", ct))
            {
                catalog.Add(AccountingMapKind.Customer, c["DisplayName"]?.GetValue<string>(), c["Id"]!.GetValue<string>());
            }
        }

        if (used.Contains(AccountingMapKind.Vendor))
        {
            foreach (var v in await QueryAllAsync(api, "Vendor", "Id, DisplayName", ct))
            {
                catalog.Add(AccountingMapKind.Vendor, v["DisplayName"]?.GetValue<string>(), v["Id"]!.GetValue<string>());
            }
        }

        if (used.Contains(AccountingMapKind.Item))
        {
            foreach (var i in await QueryAllAsync(api, "Item", "Id, Name, FullyQualifiedName", ct))
            {
                var id = i["Id"]!.GetValue<string>();
                catalog.Add(AccountingMapKind.Item, i["FullyQualifiedName"]?.GetValue<string>(), id);
                catalog.Add(AccountingMapKind.Item, i["Name"]?.GetValue<string>(), id);
            }
        }

        if (used.Contains(AccountingMapKind.Class))
        {
            foreach (var c in await QueryAllAsync(api, "Class", "Id, Name, FullyQualifiedName", ct))
            {
                var id = c["Id"]!.GetValue<string>();
                catalog.Add(AccountingMapKind.Class, c["FullyQualifiedName"]?.GetValue<string>(), id);
                catalog.Add(AccountingMapKind.Class, c["Name"]?.GetValue<string>(), id);
            }
        }

        return catalog;
    }

    private static async Task<List<JsonObject>> QueryAllAsync(AccountingApi api, string entity, string fields, CancellationToken ct)
    {
        var rows = new List<JsonObject>();
        for (var position = 1; ; position += QueryPage)
        {
            var q = $"select {fields} from {entity} STARTPOSITION {position} MAXRESULTS {QueryPage}";
            var response = await api.SendAsync(s => new HttpRequestMessage(HttpMethod.Get, $"{Base(s)}/query?minorversion={MinorVersion}&query={Uri.EscapeDataString(q)}"), ct);
            if (response.Status != 200)
            {
                throw new AccountingApiException(response.Status, $"QuickBooks wouldn't list {entity} records: {FaultText(response.Body)}", response.Body);
            }

            var page = JsonNode.Parse(response.Body)?["QueryResponse"]?[entity]?.AsArray().Select(n => n!.AsObject()).ToList() ?? [];
            rows.AddRange(page);
            if (page.Count < QueryPage)
            {
                return rows;
            }
        }
    }

    public async Task<PublishOutcome> PublishAsync(AccountingApi api, PublishRequest request, CancellationToken ct)
    {
        var data = request.Input.Data;
        var catalog = await LoadCatalogAsync(api, data, ct);
        var results = new List<PublishRecordResult>();
        var contacts = 0;
        var contactFailures = new Dictionary<(AccountingMapKind, string), string>();
        if (request.CreateMissingContacts)
        {
            foreach (var (kind, name) in PublishSupport.NamesUsed(data).Where(n => n.Kind is AccountingMapKind.Customer or AccountingMapKind.Vendor && catalog.Find(n.Kind, n.Name.Name) is null).Select(n => (n.Kind, n.Name.Name)).Distinct())
            {
                var entity = kind == AccountingMapKind.Customer ? "customer" : "vendor";
                var key = PublishSupport.Key("wb-c-", entity + name, [], _ => 0, 50);
                var response = await api.SendAsync(s => Post($"{Base(s)}/{entity}?minorversion={MinorVersion}&requestid={key}", new JsonObject { ["DisplayName"] = PublishSupport.Clip(name, 100) }), ct);
                var id = response.Status is 200 ? JsonNode.Parse(response.Body)?[entity == "customer" ? "Customer" : "Vendor"]?["Id"]?.GetValue<string>() : null;
                if (id is null)
                {
                    contactFailures[(kind, name)] = $"Couldn't create {entity} \"{name}\" in QuickBooks: {FaultText(response.Body)}";
                    continue;
                }

                catalog.Add(kind, name, id);
                contacts++;
            }
        }

        switch (request.Input.Kind)
        {
            case AccountingExportKind.JournalEntries:
                results.AddRange(await SendAsync(api, "JournalEntry", data.Journals.Select(j => (j.Id, j.Number, Build: Journal(j, catalog))).ToList(), request, ct));
                break;
            case AccountingExportKind.Invoices:
            {
                var items = new List<(Guid, string, (JsonObject?, string?))>();
                foreach (var i in data.Invoices)
                {
                    items.Add((i.Id, i.Number, i.IsCreditMemo ? (null, "NOTSENT: QuickBooks takes credit memos as a separate entity, which Workbench doesn't push yet.")
                        : contactFailures.TryGetValue((AccountingMapKind.Customer, i.Customer.Name), out var cf) ? (null, cf) : Invoice(i, catalog)));
                }

                results.AddRange(await SendAsync(api, "Invoice", items, request, ct));
                break;
            }

            case AccountingExportKind.Bills:
            {
                var items = new List<(Guid, string, (JsonObject?, string?))>();
                foreach (var b in data.Bills)
                {
                    items.Add((b.Id, b.Number, b.IsCredit ? (null, "NOTSENT: QuickBooks takes vendor credits as a separate entity, which Workbench doesn't push yet.")
                        : contactFailures.TryGetValue((AccountingMapKind.Vendor, b.Vendor.Name), out var cf) ? (null, cf) : Bill(b, catalog)));
                }

                results.AddRange(await SendAsync(api, "Bill", items, request, ct));
                break;
            }

            default:
                throw new NotSupportedException($"QuickBooks can't take {request.Input.Kind} by push.");
        }

        return new PublishOutcome(results, contacts);
    }

    private static HttpRequestMessage Post(string url, JsonNode body) =>
        new(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")) };

    private static (JsonObject?, string?) Journal(JournalDoc j, ProviderCatalog c) => Wrap(j.Id, () =>
    {
        var lines = new JsonArray();
        foreach (var l in j.Lines)
        {
            var account = c.Find(AccountingMapKind.Account, l.Account.Name) ?? throw new MissingException($"Account \"{l.Account.Name}\" isn't in QuickBooks.");
            var detail = new JsonObject
            {
                ["PostingType"] = l.Debit > 0 ? "Debit" : "Credit",
                ["AccountRef"] = new JsonObject { ["value"] = account },
            };
            if (l.Class is { } cls && c.Find(AccountingMapKind.Class, cls.Name) is { } classId)
            {
                detail["ClassRef"] = new JsonObject { ["value"] = classId };
            }

            var line = new JsonObject { ["Amount"] = l.Debit > 0 ? l.Debit : l.Credit, ["DetailType"] = "JournalEntryLineDetail", ["JournalEntryLineDetail"] = detail };
            if (!string.IsNullOrWhiteSpace(l.Memo))
            {
                line["Description"] = PublishSupport.Clip(l.Memo, 4000);
            }

            lines.Add(line);
        }

        return new JsonObject
        {
            ["DocNumber"] = PublishSupport.Clip(j.Number, 21),
            ["TxnDate"] = PublishSupport.Date(j.Date),
            ["PrivateNote"] = PublishSupport.Clip(j.Description, 4000),
            ["Line"] = lines,
        };
    });

    private static (JsonObject?, string?) Invoice(InvoiceDoc i, ProviderCatalog c) => Wrap(i.Id, () =>
    {
        var customer = c.Find(AccountingMapKind.Customer, i.Customer.Name) ?? throw new MissingException($"Customer \"{i.Customer.Name}\" isn't in QuickBooks.");
        var lines = new JsonArray();
        foreach (var l in i.Lines)
        {
            var item = c.Find(AccountingMapKind.Item, l.Item.Name) ?? throw new MissingException($"Product or service \"{l.Item.Name}\" isn't in QuickBooks.");
            if (l.Amount < 0)
            {
                throw new MissingException("QuickBooks doesn't take negative invoice lines.");
            }

            var (qty, rate) = PublishSupport.QuantityAndRate(l.Quantity, l.Rate, l.Amount);
            var detail = new JsonObject { ["ItemRef"] = new JsonObject { ["value"] = item }, ["Qty"] = qty, ["UnitPrice"] = rate };
            if (l.Class is { } cls && c.Find(AccountingMapKind.Class, cls.Name) is { } classId)
            {
                detail["ClassRef"] = new JsonObject { ["value"] = classId };
            }

            lines.Add(new JsonObject { ["Amount"] = l.Amount, ["Description"] = PublishSupport.Clip(l.Description, 4000), ["DetailType"] = "SalesItemLineDetail", ["SalesItemLineDetail"] = detail });
        }

        var body = new JsonObject
        {
            ["DocNumber"] = PublishSupport.Clip(i.Number, 21),
            ["TxnDate"] = PublishSupport.Date(i.Date),
            ["DueDate"] = PublishSupport.Date(i.DueDate),
            ["CustomerRef"] = new JsonObject { ["value"] = customer },
            ["Line"] = lines,
        };
        if (!string.IsNullOrWhiteSpace(i.Notes))
        {
            body["CustomerMemo"] = new JsonObject { ["value"] = PublishSupport.Clip(i.Notes, 1000) };
        }

        if (!string.IsNullOrWhiteSpace(i.Reference))
        {
            body["PrivateNote"] = PublishSupport.Clip(i.Reference, 4000);
        }

        return body;
    });

    private static (JsonObject?, string?) Bill(BillDoc b, ProviderCatalog c) => Wrap(b.Id, () =>
    {
        var vendor = c.Find(AccountingMapKind.Vendor, b.Vendor.Name) ?? throw new MissingException($"Vendor \"{b.Vendor.Name}\" isn't in QuickBooks.");
        var lines = new JsonArray();
        foreach (var l in b.Lines)
        {
            var account = c.Find(AccountingMapKind.Account, l.Account.Name) ?? throw new MissingException($"Account \"{l.Account.Name}\" isn't in QuickBooks.");
            var detail = new JsonObject { ["AccountRef"] = new JsonObject { ["value"] = account } };
            if (l.Class is { } cls && c.Find(AccountingMapKind.Class, cls.Name) is { } classId)
            {
                detail["ClassRef"] = new JsonObject { ["value"] = classId };
            }

            lines.Add(new JsonObject { ["Amount"] = l.Amount, ["Description"] = PublishSupport.Clip(l.Description, 4000), ["DetailType"] = "AccountBasedExpenseLineDetail", ["AccountBasedExpenseLineDetail"] = detail });
        }

        return new JsonObject
        {
            ["DocNumber"] = PublishSupport.Clip(string.IsNullOrWhiteSpace(b.VendorInvoiceNumber) ? b.Number : b.VendorInvoiceNumber, 21),
            ["TxnDate"] = PublishSupport.Date(b.Date),
            ["DueDate"] = PublishSupport.Date(b.DueDate),
            ["VendorRef"] = new JsonObject { ["value"] = vendor },
            ["PrivateNote"] = PublishSupport.Clip($"Workbench {b.Number}{(string.IsNullOrWhiteSpace(b.Description) ? "" : ": " + b.Description)}", 4000),
            ["Line"] = lines,
        };
    });

    private sealed class MissingException(string message) : Exception(message);

    private static (JsonObject?, string?) Wrap(Guid id, Func<JsonObject> build)
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

    private async Task<List<PublishRecordResult>> SendAsync(AccountingApi api, string entity, List<(Guid Id, string Number, (JsonObject? Body, string? Error) Build)> items, PublishRequest request, CancellationToken ct)
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
            var key = PublishSupport.Key("wb-", entity, chunk.Select(c => c.Id), request.AttemptOf, 36);
            var payload = new JsonObject
            {
                ["BatchItemRequest"] = new JsonArray(chunk.Select((c, n) => (JsonNode)new JsonObject { ["bId"] = n.ToString(), ["operation"] = "create", [entity] = c.Body.DeepClone() }).ToArray()),
            };
            var response = await api.SendAsync(s => Post($"{Base(s)}/batch?minorversion={MinorVersion}&requestid={key}", payload), ct);
            if (response.Status != 200)
            {
                var text = FaultText(response.Body);
                results.AddRange(chunk.Select(c => PublishSupport.Failed(c.Id, c.Number, $"QuickBooks refused the batch ({response.Status}): {text}")));
                continue;
            }

            var answers = (JsonNode.Parse(response.Body)?["BatchItemResponse"]?.AsArray() ?? []).Select(n => n!.AsObject()).ToDictionary(n => n["bId"]?.GetValue<string>() ?? "");
            for (var n = 0; n < chunk.Length; n++)
            {
                var c = chunk[n];
                if (!answers.TryGetValue(n.ToString(), out var answer))
                {
                    results.Add(PublishSupport.Failed(c.Id, c.Number, "QuickBooks gave no answer for this record. Retry it."));
                }
                else if (answer[entity] is JsonObject created && created["Id"] is { } createdId)
                {
                    results.Add(new PublishRecordResult(c.Id, c.Number, AccountingPushItemStatus.Created, createdId.GetValue<string>(), created["DocNumber"]?.GetValue<string>(), null));
                }
                else
                {
                    results.Add(PublishSupport.Failed(c.Id, c.Number, FaultText(answer.ToJsonString())));
                }
            }
        }

        return results;
    }

    /// <summary>Intuit's error body: <c>Fault.Error[].Message</c> and <c>Detail</c> (their "Fault" shape, see the Batch page's sample).</summary>
    internal static string FaultText(string body)
    {
        try
        {
            var errors = JsonNode.Parse(body)?["Fault"]?["Error"]?.AsArray();
            if (errors is { Count: > 0 })
            {
                return string.Join(" ", errors.Select(e => $"{e?["Message"]?.GetValue<string>()} {e?["Detail"]?.GetValue<string>()}".Trim()));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON: fall through to the raw text.
        }

        return PublishSupport.Clip(body, 500);
    }
}
