using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tasks;
using Workbench.Domain.Assistant;

namespace Workbench.Application.Assistant;

/// <summary>
/// Tools that change Workbench, offered only to outside AI clients over MCP (the in-app assistant stays read-only). Each one
/// calls the same permission-checked service the pages use, as the caller (for an API key, its service user), so it can
/// change only what that user could change by hand. They cover sheet rows, comments and task assignments; nothing here
/// approves, signs, submits or pays.
/// </summary>
public sealed class AssistantWriteTools(IServiceProvider services)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<AssistantToolSpec> Specs { get; } =
    [
        Spec("add_sheet_row", "Adds a row to a sheet. Values are keyed by column name (as read_sheet shows them); dates are yyyy-MM-dd.", "Adding a row",
            ("sheet", "string", "Sheet name", true),
            ("title", "string", "The row's title", true),
            ("values", "object", "Column name → value, e.g. {\"Status\": \"In progress\", \"Owner\": \"pat@example.com\"}", false),
            ("start", "string", "Start date yyyy-MM-dd", false),
            ("finish", "string", "Finish date yyyy-MM-dd", false),
            ("parent_row_id", "string", "Put the row under this row (its Row id from read_sheet)", false)),
        Spec("update_sheet_row", "Changes a row: only the fields you send change. Get the Row id from read_sheet first.", "Updating a row",
            ("sheet", "string", "Sheet name", true),
            ("row_id", "string", "The row's Row id from read_sheet", true),
            ("title", "string", "New title", false),
            ("values", "object", "Column name → new value; an empty string clears a cell", false),
            ("start", "string", "New start date yyyy-MM-dd", false),
            ("finish", "string", "New finish date yyyy-MM-dd", false)),
        Spec("add_comment", "Adds a comment to a sheet row (people following the row are notified).", "Commenting",
            ("sheet", "string", "Sheet name", true),
            ("row_id", "string", "The row's Row id from read_sheet", true),
            ("text", "string", "The comment", true)),
        Spec("assign_task", "Assigns a sheet row as a task to people (by email), optionally with a due date. Replaces the current assignees.", "Assigning a task",
            ("sheet", "string", "Sheet name", true),
            ("row_id", "string", "The row's Row id from read_sheet", true),
            ("emails", "array", "Email addresses of the assignees", true),
            ("due", "string", "Due date yyyy-MM-dd", false)),
        Spec("set_task_status", "Sets a task row's status and/or percent complete.", "Updating a task",
            ("sheet", "string", "Sheet name", true),
            ("row_id", "string", "The row's Row id from read_sheet", true),
            ("status", "string", "Status, e.g. Not started, In progress, Done", false),
            ("percent_complete", "number", "0 to 100", false)),
    ];

    /// <summary>Tools that overwrite what is there (MCP's destructiveHint).</summary>
    public static readonly IReadOnlySet<string> Overwrites = new HashSet<string>(StringComparer.Ordinal) { "update_sheet_row", "assign_task", "set_task_status" };

    public static AssistantToolSpec? Find(string name) => Specs.FirstOrDefault(s => s.Name == name);

    /// <summary>Runs a write tool. Permission, validation and not-found problems come back as an error result.</summary>
    public async Task<AssistantToolResult> RunAsync(string name, string inputJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            var input = JsonNode.Parse(doc.RootElement.GetRawText()) as JsonObject ?? throw new ArgumentException("Tool input must be a JSON object.");
            var spec = Find(name) ?? throw new ArgumentException("Unknown tool.");
            var known = spec.Schema["properties"]!.AsObject().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            if (input.Select(p => p.Key).FirstOrDefault(k => !known.Contains(k)) is { } unknown) throw new ArgumentException($"Unknown argument '{AssistantToolbox.Clip(unknown, 40)}'.");

            var (sheet, columns) = await SheetAsync(Text(input, "sheet") ?? throw new ArgumentException("'sheet' is required."), ct);
            return name switch
            {
                "add_sheet_row" => await AddRowAsync(sheet, columns, input, ct),
                "update_sheet_row" => await UpdateRowAsync(sheet, columns, input, ct),
                "add_comment" => await CommentAsync(sheet, input, ct),
                "assign_task" => await AssignAsync(sheet, input, ct),
                "set_task_status" => await StatusAsync(sheet, input, ct),
                _ => AssistantToolResult.Error(AssistantToolOutcome.Invalid, "Unknown tool."),
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WorkbenchForbiddenException)
        {
            return AssistantToolResult.Error(AssistantToolOutcome.Denied, $"Not allowed: {ex.Message}");
        }
        catch (WorkbenchNotFoundException ex)
        {
            return AssistantToolResult.Error(AssistantToolOutcome.NotFound, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or InvalidOperationException)
        {
            return AssistantToolResult.Error(AssistantToolOutcome.Invalid, ex.Message);
        }
    }

    private async Task<AssistantToolResult> AddRowAsync(SheetSummary sheet, IReadOnlyList<ColumnDto> columns, JsonObject input, CancellationToken ct)
    {
        var title = Text(input, "title") ?? throw new ArgumentException("'title' is required.");
        var values = Values(columns, input["values"], new Dictionary<string, string?>());
        var item = await Get<IItemService>().CreateAsync(sheet.Id, new CreateItemRequest(title, OptionalId(input, "parent_row_id"), null, Date(input, "start"), Date(input, "finish"), values), ct);
        return Done($"Added “{AssistantToolbox.Clip(item.Title, 80)}” to {sheet.Name}.", sheet, item.Id);
    }

    private async Task<AssistantToolResult> UpdateRowAsync(SheetSummary sheet, IReadOnlyList<ColumnDto> columns, JsonObject input, CancellationToken ct)
    {
        var items = Get<IItemService>();
        var id = RequiredId(input, "row_id");
        var current = (await items.ListAsync(sheet.Id, ct)).FirstOrDefault(i => i.Id == id) ?? throw new WorkbenchNotFoundException("No row with that id on this sheet.");
        var values = Values(columns, input["values"], new Dictionary<string, string?>(current.Values));
        var updated = await items.UpdateAsync(sheet.Id, id, new UpdateItemRequest(
            Text(input, "title") ?? current.Title,
            input.ContainsKey("start") ? Date(input, "start") : current.Start,
            input.ContainsKey("finish") ? Date(input, "finish") : current.Finish,
            current.CostpointWbsId, values), ct);
        return Done($"Updated “{AssistantToolbox.Clip(updated.Title, 80)}” on {sheet.Name}.", sheet, updated.Id);
    }

    private async Task<AssistantToolResult> CommentAsync(SheetSummary sheet, JsonObject input, CancellationToken ct)
    {
        var text = Text(input, "text", 4000) ?? throw new ArgumentException("'text' is required.");
        var id = RequiredId(input, "row_id");
        await Get<ICommentService>().CreateAsync(sheet.Id, id, new CreateCommentRequest(text), ct);
        return Done($"Commented on a row of {sheet.Name}.", sheet, id);
    }

    private async Task<AssistantToolResult> AssignAsync(SheetSummary sheet, JsonObject input, CancellationToken ct)
    {
        var emails = (input["emails"] as JsonArray ?? throw new ArgumentException("'emails' must be a list of email addresses."))
            .Select(e => e?.ToString().Trim() ?? "").Where(e => e.Length > 0).ToList();
        var id = RequiredId(input, "row_id");
        var assignees = await Get<ITaskAssignmentService>().SetAssigneesAsync(sheet.Id, id, new SetAssigneesRequest(emails, Date(input, "due"), ClearDue: false), ct);
        return Done($"Assigned to {(assignees.Count == 0 ? "nobody" : string.Join(", ", assignees.Select(a => a.DisplayName)))}.", sheet, id);
    }

    private async Task<AssistantToolResult> StatusAsync(SheetSummary sheet, JsonObject input, CancellationToken ct)
    {
        decimal? percent = input["percent_complete"] is JsonValue v && v.TryGetValue<decimal>(out var p) ? Math.Clamp(p, 0, 100) : null;
        var status = Text(input, "status");
        if (status is null && percent is null) throw new ArgumentException("Send a status, a percent_complete, or both.");
        var item = await Get<ITaskAssignmentService>().UpdateStatusAsync(sheet.Id, RequiredId(input, "row_id"), new UpdateTaskStatusRequest(status, percent), ct);
        return Done($"Updated the task “{AssistantToolbox.Clip(item.Title, 80)}”.", sheet, item.Id);
    }

    // ---- Helpers ----

    private async Task<(SheetSummary Sheet, IReadOnlyList<ColumnDto> Columns)> SheetAsync(string name, CancellationToken ct)
    {
        var sheets = Get<ISheetService>();
        var all = await sheets.ListAsync(ct);
        var match = all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? all.SingleOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkbenchNotFoundException($"No sheet you can open is named “{AssistantToolbox.Clip(name, 60)}”.");
        var detail = await sheets.GetAsync(match.Id, ct) ?? throw new WorkbenchNotFoundException("That sheet couldn't be opened.");
        return (match, detail.Columns);
    }

    /// <summary>Merges column-name values into <paramref name="into"/> (keyed by column id). Formula and unknown columns are refused.</summary>
    private static Dictionary<string, string?> Values(IReadOnlyList<ColumnDto> columns, JsonNode? node, Dictionary<string, string?> into)
    {
        if (node is null) return into;
        if (node is not JsonObject obj) throw new ArgumentException("'values' must be an object of column name → value.");
        foreach (var (name, value) in obj)
        {
            var column = columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"The sheet has no column “{AssistantToolbox.Clip(name, 40)}” you can edit. Columns: {string.Join(", ", columns.Select(c => c.Name))}.");
            if (!string.IsNullOrEmpty(column.Formula)) throw new ArgumentException($"“{column.Name}” is calculated by a formula and can't be set.");
            var text = value switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
                JsonValue v when v.TryGetValue<decimal>(out var d) => d.ToString(Invariant),
                _ => throw new ArgumentException($"“{column.Name}” needs a text, number or true/false value."),
            };
            into[column.Id.ToString()] = string.IsNullOrEmpty(text) ? null : AssistantToolbox.Clip(text, 4000);
        }
        return into;
    }

    private static AssistantToolResult Done(string summary, SheetSummary sheet, Guid itemId) =>
        new(AssistantToolOutcome.Ok, new { summary, sheet = sheet.Name, rowId = itemId }, summary,
            [new(sheet.Name, $"sheets/{sheet.Id}?item={itemId}")], [$"Sheet:{sheet.Id}", $"Item:{itemId}"]);

    private static string? Text(JsonObject input, string name, int max = 300)
    {
        var text = input[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
        if (text?.Length > max) throw new ArgumentException($"'{name}' is too long.");
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static DateOnly? Date(JsonObject input, string name) => Text(input, name) is { } t
        ? DateOnly.TryParseExact(t, "yyyy-MM-dd", Invariant, DateTimeStyles.None, out var d) ? d : throw new ArgumentException($"'{name}' must be a date like 2026-11-30.")
        : null;

    private static Guid RequiredId(JsonObject input, string name) =>
        OptionalId(input, name) ?? throw new ArgumentException($"'{name}' is required (the Row id from read_sheet).");

    private static Guid? OptionalId(JsonObject input, string name) => Text(input, name) is { } t
        ? Guid.TryParse(t.Split(':')[^1], out var g) ? g : throw new ArgumentException($"'{name}' must be a Row id from read_sheet.")
        : null;

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();

    private static AssistantToolSpec Spec(string name, string description, string activity, params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        foreach (var p in parameters)
        {
            var prop = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Type == "array") prop["items"] = new JsonObject { ["type"] = "string" };
            if (p.Type == "object") prop["additionalProperties"] = new JsonObject { ["type"] = new JsonArray("string", "number", "boolean", "null") };
            properties[p.Name] = prop;
        }

        return new AssistantToolSpec(name, description, activity, new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray()),
            ["additionalProperties"] = false,
        });
    }
}
