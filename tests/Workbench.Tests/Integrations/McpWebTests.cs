using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Workbench.Application.Integrations;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Security;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Integrations;

/// <summary>The MCP server at /api/v1/mcp against the real web host: the protocol, API key scopes per tool, and the audit trail.</summary>
public sealed class McpWebTests
{
    private const string Admin = "dev@workbench.local";

    private static async Task<string> CreateKeyAsync(HttpClient admin, OrgRoles roles, params string[] scopes)
    {
        var response = await admin.PostAsJsonAsync("api/v1/integrations/api-keys", new CreateApiKeyRequest($"MCP {Guid.NewGuid():N}", roles, scopes));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApiKeyCreatedDto>())!.Key;
    }

    private static HttpClient KeyClient(WorkbenchWebFactory factory, string key)
    {
        var client = factory.CreateClientAs(null);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string json) =>
        client.PostAsync("api/v1/mcp", new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<JsonObject> RpcAsync(HttpClient client, string method, JsonObject? parameters = null, int id = 1)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        using var response = await PostAsync(client, request.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("2.0", body["jsonrpc"]!.ToString());
        Assert.Equal(id, body["id"]!.GetValue<int>());
        return body;
    }

    private static JsonObject Call(string name, JsonObject? arguments = null) =>
        new() { ["name"] = name, ["arguments"] = arguments ?? new JsonObject() };

    [Fact]
    public async Task A_key_lists_and_calls_only_the_tools_its_scopes_allow()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        var admin = factory.CreateClientAs(Admin);
        var client = KeyClient(factory, await CreateKeyAsync(admin, OrgRoles.Admin, "mcp:read", "projects:read"));

        var init = await RpcAsync(client, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
        });
        Assert.Equal("2025-06-18", init["result"]!["protocolVersion"]!.ToString());
        Assert.Equal("workbench", init["result"]!["serverInfo"]!["name"]!.ToString());
        Assert.NotNull(init["result"]!["capabilities"]!["tools"]);

        // The initialized notification gets 202 and no body.
        using (var notified = await PostAsync(client, """{"jsonrpc":"2.0","method":"notifications/initialized"}"""))
        {
            Assert.Equal(HttpStatusCode.Accepted, notified.StatusCode);
        }

        var tools = (await RpcAsync(client, "tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.ToString()).ToList();
        Assert.Contains("list_projects", tools);
        Assert.Contains("get_project_financials", tools);
        Assert.DoesNotContain("read_sheet", tools); // needs sheets:read
        Assert.DoesNotContain("get_project_cost_ledger", tools); // needs accounting:read
        Assert.DoesNotContain("get_timesheet_flags", tools); // never offered to keys

        var projects = (await RpcAsync(client, "tools/call", Call("list_projects")))["result"]!;
        Assert.False(projects["isError"]!.GetValue<bool>());
        var data = JsonNode.Parse(projects["content"]![0]!["text"]!.ToString())!;
        Assert.NotEmpty(data["projects"]!.AsArray());
        Assert.Contains(projects["content"]!.AsArray(), c => c!["type"]!.ToString() == "resource_link"
            && c["uri"]!.ToString().StartsWith("http", StringComparison.Ordinal));

        // Out of scope: a tool error the model can read, not a protocol error.
        var sheet = (await RpcAsync(client, "tools/call", Call("read_sheet", new JsonObject { ["sheet"] = "Anything" })))["result"]!;
        Assert.True(sheet["isError"]!.GetValue<bool>());
        Assert.Contains("sheets:read", sheet["content"]![0]!["text"]!.ToString());

        // Bad arguments come back as a tool error too.
        var bad = (await RpcAsync(client, "tools/call", Call("get_project_financials", new JsonObject { ["nope"] = 1 })))["result"]!;
        Assert.True(bad["isError"]!.GetValue<bool>());

        // Unknown tools and methods are protocol errors.
        Assert.Equal(-32602, (await RpcAsync(client, "tools/call", Call("get_timesheet_flags")))["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, (await RpcAsync(client, "resources/list"))["error"]!["code"]!.GetValue<int>());
        Assert.NotNull((await RpcAsync(client, "ping"))["result"]);

        // Every call is in the security log, with the tool but not the data.
        using var search = await admin.PostAsJsonAsync("/api/v1/admin/security-events/search", new SecurityEventQuery(EventType: SecurityEventTypes.McpToolCall, Limit: 100));
        search.EnsureSuccessStatusCode();
        var events = (await search.Content.ReadFromJsonAsync<List<SecurityEventDto>>())!;
        Assert.Contains(events, e => e.Target == "list_projects" && e.Outcome == SecurityOutcomes.Success && e.Actor.Contains(ApiKeyPrincipal.EmailDomain, StringComparison.Ordinal));
        Assert.Contains(events, e => e.Target == "get_project_financials" && e.Outcome == SecurityOutcomes.Failure);
    }

    [Fact]
    public async Task Write_tools_need_write_access_and_change_sheets_as_the_key_user()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        var admin = factory.CreateClientAs(Admin);
        var reader = KeyClient(factory, await CreateKeyAsync(admin, OrgRoles.Admin, "mcp:read", "sheets:read"));
        var writer = KeyClient(factory, await CreateKeyAsync(admin, OrgRoles.Admin, "mcp:read", "sheets:write"));

        // A read-only key doesn't see the write tools, and can't call them.
        var readerTools = (await RpcAsync(reader, "tools/list"))["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.ToString()).ToList();
        Assert.Contains("read_sheet", readerTools);
        Assert.DoesNotContain("add_sheet_row", readerTools);
        var refused = (await RpcAsync(reader, "tools/call", Call("add_sheet_row", new JsonObject { ["sheet"] = "x", ["title"] = "x" })))["result"]!;
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("sheets:write", refused["content"]![0]!["text"]!.ToString());

        var writerTools = (await RpcAsync(writer, "tools/list"))["result"]!["tools"]!.AsArray();
        var add = writerTools.Single(t => t!["name"]!.ToString() == "add_sheet_row")!;
        Assert.False(add["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.True(writerTools.Single(t => t!["name"]!.ToString() == "update_sheet_row")!["annotations"]!["destructiveHint"]!.GetValue<bool>());

        var sheets = JsonNode.Parse((await RpcAsync(writer, "tools/call", Call("list_sheets")))["result"]!["content"]![0]!["text"]!.ToString())!;
        var sheet = sheets["sheets"]![0]!["name"]!.ToString();

        var added = (await RpcAsync(writer, "tools/call", Call("add_sheet_row", new JsonObject { ["sheet"] = sheet, ["title"] = "Added by an assistant", ["finish"] = "2026-12-01" })))["result"]!;
        Assert.False(added["isError"]!.GetValue<bool>(), added.ToJsonString());
        var rowId = JsonNode.Parse(added["content"]![0]!["text"]!.ToString())!["rowId"]!.ToString();

        var updated = (await RpcAsync(writer, "tools/call", Call("update_sheet_row", new JsonObject { ["sheet"] = sheet, ["row_id"] = rowId, ["title"] = "Renamed by an assistant" })))["result"]!;
        Assert.False(updated["isError"]!.GetValue<bool>(), updated.ToJsonString());
        var commented = (await RpcAsync(writer, "tools/call", Call("add_comment", new JsonObject { ["sheet"] = sheet, ["row_id"] = rowId, ["text"] = "Checked against the plan." })))["result"]!;
        Assert.False(commented["isError"]!.GetValue<bool>(), commented.ToJsonString());

        // The row is really there, with its id, finish date untouched by the rename.
        var read = (await RpcAsync(writer, "tools/call", Call("read_sheet", new JsonObject { ["sheet"] = sheet, ["query"] = "Renamed by an assistant" })))["result"]!;
        var row = JsonNode.Parse(read["content"]![0]!["text"]!.ToString())!["rows"]![0]!;
        Assert.Equal(rowId, row["Row id"]!.ToString());
        Assert.Equal("2026-12-01", row["Finish"]!.ToString());

        // Unknown columns, bad ids and formula columns come back as tool errors.
        Assert.True((await RpcAsync(writer, "tools/call", Call("update_sheet_row", new JsonObject { ["sheet"] = sheet, ["row_id"] = rowId, ["values"] = new JsonObject { ["No such column"] = "x" } })))["result"]!["isError"]!.GetValue<bool>());
        Assert.True((await RpcAsync(writer, "tools/call", Call("add_comment", new JsonObject { ["sheet"] = sheet, ["row_id"] = Guid.NewGuid().ToString(), ["text"] = "x" })))["result"]!["isError"]!.GetValue<bool>());

        using var search = await admin.PostAsJsonAsync("/api/v1/admin/security-events/search", new SecurityEventQuery(EventType: SecurityEventTypes.McpToolCall, Limit: 100));
        var events = (await search.Content.ReadFromJsonAsync<List<SecurityEventDto>>())!;
        Assert.Contains(events, e => e.Target == "add_sheet_row" && e.Outcome == SecurityOutcomes.Success);
    }

    [Fact]
    public async Task Only_api_keys_with_the_mcp_scope_and_no_browser_origin_may_connect()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        var admin = factory.CreateClientAs(Admin);
        const string ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";

        // A key without mcp:read is refused by the scope check.
        var noMcp = KeyClient(factory, await CreateKeyAsync(admin, OrgRoles.Admin, "projects:read"));
        using (var refused = await PostAsync(noMcp, ping))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("mcp:read", await refused.Content.ReadAsStringAsync());
        }

        // A signed-in person (not a key) is refused: MCP clients connect with keys.
        using (var person = await PostAsync(admin, ping))
        {
            Assert.Equal(HttpStatusCode.Forbidden, person.StatusCode);
        }

        // No key at all: 401.
        using (var anonymous = await PostAsync(factory.CreateClientAs(null), ping))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var client = KeyClient(factory, await CreateKeyAsync(admin, OrgRoles.Employee, "mcp:read"));

        // A web page's request (Origin header) is refused even with a good key.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/mcp") { Content = new StringContent(ping, Encoding.UTF8, "application/json") })
        {
            request.Headers.Add("Origin", "https://evil.example");
            using var fromPage = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, fromPage.StatusCode);
        }

        // No event stream or sessions.
        using (var get = await client.GetAsync("api/v1/mcp"))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        }

        // Malformed bodies get JSON-RPC errors.
        using (var garbage = await PostAsync(client, "{not json"))
        {
            Assert.Contains("-32700", await garbage.Content.ReadAsStringAsync());
        }

        using (var batch = await PostAsync(client, $"[{ping}]"))
        {
            Assert.Contains("-32600", await batch.Content.ReadAsStringAsync());
        }

        // With only mcp:read the key connects but has no tools.
        Assert.Empty((await RpcAsync(client, "tools/list"))["result"]!["tools"]!.AsArray());
    }

    [Fact]
    public async Task The_official_mcp_client_connects_lists_and_calls_tools()
    {
        // The same client and transport Hearth uses (ModelContextProtocol.Core, HttpClientTransport, the key as a header).
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        var key = await CreateKeyAsync(factory.CreateClientAs(Admin), OrgRoles.Admin, "mcp:read", "projects:read", "sheets:read");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(factory.Server.BaseAddress, "api/v1/mcp"),
                Name = "workbench",
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
            },
            factory.CreateClientAs(null), ownsHttpClient: false);

        await using var client = await McpClient.CreateAsync(transport);
        Assert.Equal("workbench", client.ServerInfo.Name);

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "list_sheets");
        Assert.DoesNotContain(tools, t => t.Name == "get_timesheet_status");
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));

        var result = await client.CallToolAsync("list_projects", new Dictionary<string, object?>());
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("projects", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
    }

    [Fact]
    public void Mcp_posts_count_as_reads_but_other_posts_do_not()
    {
        Assert.True(ApiKeyScopes.Check(["mcp:read"], "/api/v1/mcp", "POST").Allowed);
        Assert.False(ApiKeyScopes.Check(["sheets:read"], "/api/v1/mcp", "POST").Allowed);
        Assert.False(ApiKeyScopes.Check(["sheets:read"], "/api/v1/sheets", "POST").Allowed);
        Assert.True(ApiKeyScopes.CanRead(["projects:write"], "projects"));
        Assert.False(ApiKeyScopes.CanRead(["projects:read"], "time"));
    }
}
