using System.Net;
using System.Text;
using Workbench.Domain.Notifications;

namespace Workbench.Application.Notifications;

/// <summary>Plain, self-contained email templates (HTML and text). No images, fonts or external styles.</summary>
public static class NotificationEmails
{
    public static EmailMessage Single(Notification notification, NotificationOptions options)
    {
        var link = AbsoluteLink(notification.Link, options);
        var text = new StringBuilder()
            .AppendLine(notification.Title)
            .AppendLine();
        if (notification.Body.Length > 0)
        {
            text.AppendLine(notification.Body).AppendLine();
        }

        if (link is not null)
        {
            text.AppendLine($"Open: {link}").AppendLine();
        }

        text.Append(Footer);

        var html = new StringBuilder()
            .Append("<h1 style=\"font-size:20px;margin:0 0 12px\">").Append(Encode(notification.Title)).Append("</h1>");
        if (notification.Body.Length > 0)
        {
            html.Append("<p style=\"margin:0 0 16px;white-space:pre-wrap\">").Append(Encode(notification.Body)).Append("</p>");
        }

        if (link is not null)
        {
            html.Append("<p style=\"margin:0 0 16px\"><a href=\"").Append(Encode(link)).Append("\" style=\"color:#0b57d0\">Open in Workbench</a></p>");
        }

        return new EmailMessage(notification.RecipientEmail, notification.Title, text.ToString(), Wrap(notification.Title, html.ToString()));
    }

    public static EmailMessage Digest(string recipient, IReadOnlyList<Notification> notifications, NotificationOptions options)
    {
        var subject = notifications.Count == 1
            ? "Workbench: 1 unread notification"
            : $"Workbench: {notifications.Count} unread notifications";

        var text = new StringBuilder().AppendLine(subject).AppendLine();
        var html = new StringBuilder()
            .Append("<h1 style=\"font-size:20px;margin:0 0 12px\">").Append(Encode(subject)).Append("</h1>")
            .Append("<ul style=\"padding-left:20px;margin:0 0 16px\">");
        foreach (var n in notifications)
        {
            var link = AbsoluteLink(n.Link, options);
            text.Append("- ").AppendLine(n.Title);
            if (n.Body.Length > 0)
            {
                text.Append("  ").AppendLine(n.Body.ReplaceLineEndings(" "));
            }

            if (link is not null)
            {
                text.Append("  ").AppendLine(link);
            }

            html.Append("<li style=\"margin:0 0 12px\">");
            html.Append(link is null
                ? $"<strong>{Encode(n.Title)}</strong>"
                : $"<a href=\"{Encode(link)}\" style=\"color:#0b57d0\"><strong>{Encode(n.Title)}</strong></a>");
            if (n.Body.Length > 0)
            {
                html.Append("<br>").Append(Encode(n.Body));
            }

            html.Append("</li>");
        }

        html.Append("</ul>");
        text.AppendLine().Append(Footer);
        return new EmailMessage(recipient, subject, text.ToString(), Wrap(subject, html.ToString()));
    }

    private const string Footer = "You get this email because of your Workbench notification settings. Change them on the Notifications page.";

    private static string Wrap(string title, string content) =>
        $"""
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>{Encode(title)}</title></head>
        <body style="margin:0;padding:24px;background:#ffffff;color:#1f1f1f;font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.5">
        <main style="max-width:600px">
        {content}
        <hr style="border:0;border-top:1px solid #c4c7c5;margin:24px 0 12px">
        <p style="margin:0;font-size:13px;color:#444746">{Encode(Footer)}</p>
        </main>
        </body>
        </html>
        """;

    private static string? AbsoluteLink(string? link, NotificationOptions options)
    {
        if (link is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(options.PublicBaseUrl) ? link : options.PublicBaseUrl.TrimEnd('/') + "/" + link.TrimStart('/');
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
