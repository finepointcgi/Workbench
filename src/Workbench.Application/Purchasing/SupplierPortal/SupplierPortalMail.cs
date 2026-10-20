using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Notifications;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// Emails to vendor contacts (sign-in links, RFQ invitations). Plain and self-contained like <see cref="NotificationEmails"/>.
/// Links point at the supplier portal under <see cref="SupplierPortalPaths.Base"/>: absolute when
/// <c>Notifications:PublicBaseUrl</c> is set (or the caller passes the site's address), relative otherwise.
/// Failures are logged, never thrown.
/// </summary>
public sealed class SupplierPortalMail(IEmailSender sender, NotificationOptions options, ILogger<SupplierPortalMail> logger)
{
    public async Task SignInLinkAsync(SupplierContact contact, string vendorName, string token, string? baseUrl, CancellationToken ct)
    {
        var link = Absolute($"{SupplierPortalPaths.LinkSignIn}?token={Uri.EscapeDataString(token)}", baseUrl);
        await SendAsync(contact.Email, $"Sign in to the {vendorName} supplier portal",
            $"Hello {contact.Name},",
            $"Use this link to sign in to the supplier portal for {vendorName}. It works once and expires in {SupplierSignIn.LinkLifetime.TotalMinutes:0} minutes. If you didn't ask for it, ignore this email.",
            link, "Sign in", ct);
    }

    /// <summary>Tells the active portal contacts of each vendor about an RFQ they're invited to.</summary>
    public async Task RfqInvitationAsync(IWorkbenchDbContext db, Rfq rfq, IReadOnlyCollection<Guid> vendorIds, CancellationToken ct)
    {
        var contacts = await db.Set<SupplierContact>().AsNoTracking().Where(c => vendorIds.Contains(c.VendorId) && c.IsActive).ToListAsync(ct);
        var link = Absolute($"{SupplierPortalPaths.Base}/rfqs/{rfq.Id}", null);
        foreach (var c in contacts)
        {
            await SendAsync(c.Email, $"Request for quotation {rfq.Number}: {rfq.Title}",
                $"Hello {c.Name},",
                $"You're invited to quote on {rfq.Number} ({rfq.Title}). Quotes are due by {rfq.DueDate:MMMM d, yyyy}. Sign in to the supplier portal to see the lines and send your quote.",
                link, "Open the RFQ", ct);
        }
    }

    private string Absolute(string path, string? baseUrl)
    {
        var root = baseUrl ?? options.PublicBaseUrl;
        return string.IsNullOrWhiteSpace(root) ? path : root.TrimEnd('/') + path;
    }

    private async Task SendAsync(string to, string subject, string greeting, string body, string link, string action, CancellationToken ct)
    {
        var text = new StringBuilder().AppendLine(greeting).AppendLine().AppendLine(body).AppendLine().AppendLine($"{action}: {link}").ToString();
        var html = $"""
            <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>{Encode(subject)}</title></head>
            <body style="font-family:Arial,sans-serif;color:#1f1f1f;line-height:1.5">
            <p style="margin:0 0 12px">{Encode(greeting)}</p>
            <p style="margin:0 0 16px">{Encode(body)}</p>
            <p style="margin:0 0 16px"><a href="{Encode(link)}" style="color:#0b57d0">{Encode(action)}</a></p>
            <p style="margin:0;font-size:13px;color:#444746">This message is about your company's work with us. Questions? Reply to your buyer.</p>
            </body></html>
            """;
        try
        {
            await sender.SendAsync(new EmailMessage(to, subject, text, html), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A supplier portal email couldn't be sent.");
        }
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

/// <summary>Where the supplier portal lives (the web host maps these).</summary>
public static class SupplierPortalPaths
{
    public const string Base = "/supplier";
    public const string SignIn = Base + "/signin";
    public const string LinkSignIn = Base + "/signin/link";
}
