using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Workbench.Application.Notifications;

namespace Workbench.Infrastructure.Notifications;

/// <summary>Builds a multipart/alternative message: plain text first, HTML second (clients show the last they support).</summary>
internal static class MailMessages
{
    public static MailMessage Build(EmailMessage message, string from)
    {
        var mail = new MailMessage(new MailAddress(from, "Workbench"), new MailAddress(message.To))
        {
            Subject = message.Subject,
            SubjectEncoding = Encoding.UTF8,
            HeadersEncoding = Encoding.UTF8,
        };
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.TextBody, Encoding.UTF8, MediaTypeNames.Text.Plain));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.HtmlBody, Encoding.UTF8, MediaTypeNames.Text.Html));
        foreach (var file in message.Attachments ?? [])
        {
            mail.Attachments.Add(new Attachment(new MemoryStream(file.Content), file.FileName, file.ContentType));
        }

        return mail;
    }
}

/// <summary>
/// Development sender: writes each email as an <c>.eml</c> file into a folder (config <c>Email:PickupFolder</c>,
/// default <c>App_Data/mail</c>) instead of sending it. Open the files with any mail client.
/// </summary>
public sealed class PickupFolderEmailSender : IEmailSender
{
    private readonly EmailSettings _settings;

    public PickupFolderEmailSender(EmailSettings settings)
    {
        _settings = settings;
        Folder = Path.GetFullPath(settings.PickupFolder);
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var mail = MailMessages.Build(message, _settings.From);
        using var client = new SmtpClient
        {
            DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
            PickupDirectoryLocation = Folder,
        };
        await client.SendMailAsync(mail, cancellationToken);
    }
}

