using System.Net;
using System.Text.Json.Nodes;

namespace Workbench.Tests.AccountingLive;

/// <summary>Recorded-shape answers for QuickBooks Online: query results by entity, and /batch creating (or faulting) each item.</summary>
internal sealed class QboScript
{
    private int _next = 100;

    public QboScript(FakeProvider provider, Dictionary<string, string[]> names)
    {
        Names = names;
        provider.On(c => c.Method == "GET" && c.Uri.AbsolutePath.EndsWith("/query"), c =>
        {
            var q = c.Query("query")!;
            var entity = q.Split(" from ", StringSplitOptions.None)[1].Split(' ')[0];
            var rows = new JsonArray();
            var n = 0;
            foreach (var name in Names.GetValueOrDefault(entity, []))
            {
                var row = new JsonObject { ["Id"] = $"{entity[0]}{++n}", ["Name"] = name, ["FullyQualifiedName"] = name, ["DisplayName"] = name };
                rows.Add(row);
            }

            return FakeProvider.Json(HttpStatusCode.OK, new JsonObject { ["QueryResponse"] = new JsonObject { [entity] = rows, ["startPosition"] = 1, ["maxResults"] = rows.Count } }.ToJsonString());
        });
        provider.On(c => c.Method == "POST" && c.Uri.AbsolutePath.EndsWith("/batch"), c =>
        {
            var answers = new JsonArray();
            foreach (var item in c.Json!["BatchItemRequest"]!.AsArray())
            {
                var entity = item!.AsObject().Select(p => p.Key).First(k => k is not ("bId" or "operation"));
                var doc = item[entity]!["DocNumber"]!.GetValue<string>();
                if (FailDocNumbers.Contains(doc))
                {
                    answers.Add(new JsonObject
                    {
                        ["bId"] = item["bId"]!.GetValue<string>(),
                        ["Fault"] = new JsonObject { ["type"] = "ValidationFault", ["Error"] = new JsonArray(new JsonObject { ["Message"] = "Object Not Found", ["Detail"] = "Something you're trying to use has been made inactive.", ["code"] = "610" }) },
                    });
                }
                else
                {
                    answers.Add(new JsonObject { ["bId"] = item["bId"]!.GetValue<string>(), [entity] = new JsonObject { ["Id"] = $"{++_next}", ["DocNumber"] = doc } });
                }
            }

            return FakeProvider.Json(HttpStatusCode.OK, new JsonObject { ["BatchItemResponse"] = answers }.ToJsonString());
        });
        provider.On(c => c.Method == "POST" && (c.Uri.AbsolutePath.EndsWith("/customer") || c.Uri.AbsolutePath.EndsWith("/vendor")), c =>
        {
            var entity = c.Uri.AbsolutePath.EndsWith("/customer") ? "Customer" : "Vendor";
            return FakeProvider.Json(HttpStatusCode.OK, new JsonObject { [entity] = new JsonObject { ["Id"] = $"new-{entity}", ["DisplayName"] = c.Json!["DisplayName"]!.GetValue<string>() } }.ToJsonString());
        });
    }

    public Dictionary<string, string[]> Names { get; }

    public HashSet<string> FailDocNumbers { get; } = [];
}

/// <summary>Recorded-shape answers for Xero: accounts, contacts, tracking categories, and POST with per-item validation errors (summarizeErrors=false).</summary>
internal sealed class XeroScript
{
    private int _next = 100;

    public XeroScript(FakeProvider provider, (string Code, string Name)[] accounts, string[] contacts, (string Category, string Option)[] tracking)
    {
        provider.On(c => c.Method == "GET" && c.Uri.AbsolutePath.EndsWith("/Accounts"), _ => FakeProvider.Json(HttpStatusCode.OK,
            new JsonObject { ["Accounts"] = new JsonArray(accounts.Select(a => (JsonNode)new JsonObject { ["AccountID"] = Guid.NewGuid().ToString(), ["Code"] = a.Code, ["Name"] = a.Name, ["Status"] = "ACTIVE" }).ToArray()) }.ToJsonString()));
        provider.On(c => c.Method == "GET" && c.Uri.AbsolutePath.EndsWith("/Contacts"), c => FakeProvider.Json(HttpStatusCode.OK,
            new JsonObject { ["Contacts"] = c.Query("page") == "1" ? new JsonArray(contacts.Select((n, i) => (JsonNode)new JsonObject { ["ContactID"] = $"contact-{i}", ["Name"] = n }).ToArray()) : new JsonArray() }.ToJsonString()));
        provider.On(c => c.Method == "GET" && c.Uri.AbsolutePath.EndsWith("/TrackingCategories"), _ => FakeProvider.Json(HttpStatusCode.OK,
            new JsonObject
            {
                ["TrackingCategories"] = new JsonArray(tracking.GroupBy(t => t.Category).Select(g => (JsonNode)new JsonObject
                {
                    ["Name"] = g.Key, ["Options"] = new JsonArray(g.Select(o => (JsonNode)new JsonObject { ["Name"] = o.Option }).ToArray()),
                }).ToArray()),
            }.ToJsonString()));
        provider.On(c => c.Method == "POST" && c.Uri.AbsolutePath.EndsWith("/Contacts"), c => FakeProvider.Json(HttpStatusCode.OK,
            new JsonObject { ["Contacts"] = new JsonArray(new JsonObject { ["ContactID"] = "contact-new", ["Name"] = c.Json!["Contacts"]![0]!["Name"]!.GetValue<string>() }) }.ToJsonString()));
        provider.On(c => c.Method == "POST" && (c.Uri.AbsolutePath.EndsWith("/Invoices") || c.Uri.AbsolutePath.EndsWith("/ManualJournals")), c =>
        {
            var collection = c.Uri.AbsolutePath.EndsWith("/Invoices") ? "Invoices" : "ManualJournals";
            var idName = collection == "Invoices" ? "InvoiceID" : "ManualJournalID";
            var answers = new JsonArray();
            foreach (var item in c.Json![collection]!.AsArray())
            {
                var key = item!["InvoiceNumber"]?.GetValue<string>() ?? item["Narration"]!.GetValue<string>().Split(':')[0];
                if (FailNumbers.Contains(key))
                {
                    answers.Add(new JsonObject
                    {
                        ["HasErrors"] = true, ["StatusAttributeString"] = "ERROR",
                        ["ValidationErrors"] = new JsonArray(new JsonObject { ["Message"] = "Account code '999' is not a valid code for this organisation" }),
                    });
                }
                else
                {
                    answers.Add(new JsonObject { [idName] = $"x-{++_next}", ["InvoiceNumber"] = key, ["StatusAttributeString"] = "OK" });
                }
            }

            return FakeProvider.Json(HttpStatusCode.OK, new JsonObject { [collection] = answers }.ToJsonString());
        });
    }

    public HashSet<string> FailNumbers { get; } = [];
}
