using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Application.Notifications;
using Workbench.Contracts.Integrations;
using Workbench.Domain.Integrations;

namespace Workbench.Application.Integrations.Connectors;

/// <summary>A chat message about an event: a title, a few facts and a link back into Workbench.</summary>
public sealed record ChatMessage(string Title, IReadOnlyList<(string Label, string Value)> Facts, string? Link);

/// <summary>Turns an event payload into a short message. Uses only the payload, so it shows nothing the webhook wouldn't.</summary>
public static class ChatMessages
{
    public static ChatMessage Describe(IntegrationOutboxEvent e, string? publicBaseUrl)
    {
        using var doc = JsonDocument.Parse(e.PayloadJson);
        var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
        string S(string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.ValueKind == JsonValueKind.Number ? v.GetDecimal().ToString("#,##0.##", CultureInfo.InvariantCulture) : v.ToString()
            : "";
        string Money(string name) => decimal.TryParse(S(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m.ToString("#,##0.00", CultureInfo.InvariantCulture) : S(name);
        var label = WebhookEventCatalog.All.FirstOrDefault(x => x.Key == e.EventType)?.Label ?? e.EventType;
        var id = S(SubjectIdName(e.Subject));

        var (title, facts, link) = e.EventType switch
        {
            "timesheet.submitted" or "timesheet.approved" => ($"{label}: {S("employeeName")}",
                F(("Period", $"{S("periodStart")} to {S("periodEnd")}"), ("Hours", S("totalHours"))), "time/approvals"),
            "invoice.approved" or "invoice.sent" or "invoice.paid" => ($"{label}: {S("number")}",
                F(("Total", $"{Money("total")} {S("currencyCode")}".Trim()), ("Balance", Money("balance")), ("Due", S("dueDate"))), $"billing/invoices/{id}"),
            "bill.approved" or "bill.paid" => ($"{label}: {S("number")}",
                F(("Vendor invoice", S("vendorInvoiceNumber")), ("Amount", $"{Money("amount")} {S("currencyCode")}".Trim()), ("Due", S("dueDate"))), $"purchasing/bills/{id}"),
            "purchase_order.approved" => ($"{label}: {S("number")}", F(("Total", Money("total")), ("Order date", S("orderDate"))), $"purchasing/orders/{id}"),
            "project.created" => ($"{label}: {S("code")} {S("name")}", F(("Status", S("status"))), $"projects/{id}"),
            "employee.hired" => ($"{label}: {S("displayName")}", F(("Hire date", S("hireDate"))), $"hr/employees/{id}"),
            "employee.terminated" => ($"{label}: {S("displayName")}", F(("Last day", S("terminationDate"))), $"hr/employees/{id}"),
            "opportunity.stage_changed" => ($"{label}: {S("number")} {S("name")}", F(("From", S("fromStage")), ("To", S("toStage"))), $"bd/opportunities/{id}"),
            "period.closed" => ($"{label}: {S("name")} FY{S("fiscalYear")}", F(("Dates", $"{S("start")} to {S("end")}")), "accounting/close"),
            "automation.message" => (S("title"), F(("Message", S("text")), ("Record", S("record")), ("Automation", S("automation"))), S("link") is { Length: > 0 } l ? l : null),
            _ => (label, F(), (string?)null),
        };

        if (e.Actor is { } actor)
        {
            facts.Add(("By", actor));
        }

        return new ChatMessage(title, facts, link is null || string.IsNullOrWhiteSpace(publicBaseUrl) ? null : $"{publicBaseUrl.TrimEnd('/')}/{link}");
    }

    public static ChatMessage Test(string connectorName) =>
        new("Workbench test message", F(("Connector", connectorName), ("Result", "This channel will receive the events you chose.")), null);

    private static List<(string Label, string Value)> F(params (string, string)[] items) => [.. items];

    private static string SubjectIdName(string subject) => subject switch
    {
        "PurchaseOrder" => "purchaseOrderId",
        "FiscalPeriod" => "fiscalPeriodId",
        _ => char.ToLowerInvariant(subject[0]) + subject[1..] + "Id",
    };
}

/// <summary>Shared by Teams and Slack: an incoming webhook URL (a secret), chosen events, and posting through the guarded transport.</summary>
public abstract class ChatConnector(IOutboundTransport transport, OutboundUrlGuard guard, NotificationOptions? notifications) : IConnector
{
    public const string WebhookUrl = "webhookUrl";

    public abstract string Kind { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public ConnectorMode Mode => ConnectorMode.Events;
    protected abstract string UrlHelp { get; }

    public Task<IReadOnlyList<ConnectorFieldDto>> GetFieldsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConnectorFieldDto>>([new(WebhookUrl, "Incoming webhook URL", ConnectorFieldKind.Secret, true, UrlHelp)]);

    public async Task ValidateAsync(ConnectorConfig config, CancellationToken cancellationToken) =>
        await guard.ValidateAsync(config.Required(WebhookUrl, "The incoming webhook URL"), "The incoming webhook URL", cancellationToken);

    public async Task<ConnectorResult> RunAsync(ConnectorContext context, CancellationToken cancellationToken)
    {
        var url = await guard.ValidateAsync(context.Config.Required(WebhookUrl, "The incoming webhook URL"), "The incoming webhook URL", cancellationToken);
        var message = context.Event is null ? ChatMessages.Test(context.Connector.Name) : ChatMessages.Describe(context.Event, notifications?.PublicBaseUrl);
        var request = new OutboundRequest(url, Format(message).ToJsonString(), "application/json", []);
        var response = await transport.SendAsync(request, cancellationToken);
        var shown = request with { Url = new Uri(url.GetLeftPart(UriPartial.Authority) + "/…") }; // The path is the secret; the log shows the host only.
        return response.IsSuccess
            ? new ConnectorResult(true, $"Posted \"{message.Title}\".", 1, Request: shown, Response: response)
            : new ConnectorResult(false, response.Error ?? $"The channel answered {response.StatusCode}.", 0, Request: shown, Response: response);
    }

    protected abstract JsonObject Format(ChatMessage message);
}

/// <summary>Microsoft Teams: an adaptive card posted to a Workflows (or legacy connector) incoming webhook.</summary>
public sealed class TeamsConnector(IOutboundTransport transport, OutboundUrlGuard guard, NotificationOptions? notifications = null)
    : ChatConnector(transport, guard, notifications)
{
    public override string Kind => "teams";
    public override string Name => "Microsoft Teams";
    public override string Description => "Posts the events you choose (approvals, payments, period close) to a Teams channel.";
    protected override string UrlHelp => "In Teams, add the Workflows app's \"Post to a channel when a webhook request is received\" to the channel and paste its URL.";

    protected override JsonObject Format(ChatMessage message) => Card(message);

    /// <summary>The Teams message for <paramref name="message"/>: an adaptive card with its facts and an "Open in Workbench" button.</summary>
    public static JsonObject Card(ChatMessage message)
    {
        var body = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = message.Title, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
            new JsonObject
            {
                ["type"] = "FactSet",
                ["facts"] = new JsonArray(message.Facts.Select(f => (JsonNode)new JsonObject { ["title"] = f.Label, ["value"] = f.Value }).ToArray()),
            },
        };
        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
        };
        if (message.Link is { } link)
        {
            card["actions"] = new JsonArray { new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open in Workbench", ["url"] = link } };
        }

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray { new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["content"] = card } },
        };
    }
}

/// <summary>Slack: a message with blocks posted to an incoming webhook.</summary>
public sealed class SlackConnector(IOutboundTransport transport, OutboundUrlGuard guard, NotificationOptions? notifications = null)
    : ChatConnector(transport, guard, notifications)
{
    public override string Kind => "slack";
    public override string Name => "Slack";
    public override string Description => "Posts the events you choose (approvals, payments, period close) to a Slack channel.";
    protected override string UrlHelp => "In Slack, create an app with Incoming Webhooks turned on, add a webhook for the channel and paste its URL.";

    protected override JsonObject Format(ChatMessage message) => Message(message);

    /// <summary>The Slack message for <paramref name="message"/>: a Block Kit section with its facts and an "Open in Workbench" button. User text is escaped, so it can't make links or mentions.</summary>
    public static JsonObject Message(ChatMessage message)
    {
        var facts = string.Join("\n", message.Facts.Select(f => $"*{Escape(f.Label)}:* {Escape(f.Value)}"));
        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = $"*{Escape(message.Title)}*\n{facts}".TrimEnd() } },
        };
        if (message.Link is { } link)
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "actions",
                ["elements"] = new JsonArray { new JsonObject { ["type"] = "button", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = "Open in Workbench" }, ["url"] = link } },
            });
        }

        return new JsonObject { ["text"] = Escape(message.Title), ["blocks"] = blocks };
    }

    // Slack mrkdwn: &, < and > must be escaped (they start links and <!channel>-style mentions); everything else is literal.
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
