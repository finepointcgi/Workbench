using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Application.Notifications;

namespace Workbench.Infrastructure.Notifications;

public static class NotificationInfrastructure
{
    /// <summary>
    /// Registers the email sender. <c>Email:Mode</c> is <c>Pickup</c> (write .eml files to <c>Email:PickupFolder</c>),
    /// <c>Smtp</c> (<c>Email:Smtp:Host</c>, <c>Port</c>, <c>Security</c>, <c>UserName</c>, <c>Password</c>), <c>SendGrid</c>
    /// (<c>Email:SendGrid:ApiKey</c>) or <c>Ses</c> (<c>Email:Ses:Region</c>, an IAM role). When unset it's
    /// <c>Smtp</c> if a host is configured, otherwise <c>Pickup</c>. Also binds <c>Notifications:PublicBaseUrl</c> and
    /// <c>Notifications:DigestHourLocal</c>.
    /// </summary>
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var email = configuration.GetSection("Email");
        var settings = new EmailSettings
        {
            From = email["From"] is { Length: > 0 } from ? from : "workbench@localhost",
            FromName = email["FromName"] is { Length: > 0 } fromName ? fromName : "Workbench",
            ReplyTo = email["ReplyTo"] is { Length: > 0 } replyTo ? replyTo : null,
            PickupFolder = email["PickupFolder"] is { Length: > 0 } folder ? folder : EmailSettings.DefaultPickupFolder,
            SmtpHost = email["Smtp:Host"],
            SmtpPort = int.TryParse(email["Smtp:Port"], out var port) ? port : 25,
            SmtpEnableSsl = !bool.TryParse(email["Smtp:EnableSsl"], out var ssl) || ssl,
            SmtpSecurity = email["Smtp:Security"] is { Length: > 0 } security ? security : "Auto",
            SmtpUserName = email["Smtp:UserName"],
            SmtpPassword = email["Smtp:Password"],
            SendGridApiKey = email["SendGrid:ApiKey"],
            SendGridWebhookVerificationKey = email["SendGrid:WebhookVerificationKey"],
            SesRegion = email["Ses:Region"],
            SesConfigurationSet = email["Ses:ConfigurationSet"],
            SesAccessKeyId = email["Ses:AccessKeyId"],
            SesSecretAccessKey = email["Ses:SecretAccessKey"],
            SesBounceTopicArns = ListOf(email.GetSection("Ses:BounceTopicArns")),
        };
        settings.Mode = email["Mode"] is { Length: > 0 } mode ? mode : string.IsNullOrWhiteSpace(settings.SmtpHost) ? "Pickup" : "Smtp";
        services.AddSingleton(settings);
        services.TryAddSingleton<EmailDeliveryStatus>();
        services.TryAddSingleton(TimeProvider.System);

        services.RemoveAll<IEmailSender>();
        Func<IServiceProvider, IEmailSender> sender = settings.Mode.ToLowerInvariant() switch
        {
            "pickup" => sp => ActivatorUtilities.CreateInstance<PickupFolderEmailSender>(sp),
            "smtp" => sp => ActivatorUtilities.CreateInstance<SmtpEmailSender>(sp),
            "sendgrid" => _ => new SendGridEmailSender(settings),
            "ses" => _ => new SesEmailSender(settings),
            _ => throw new InvalidOperationException($"Unsupported Email:Mode '{settings.Mode}'. Use Smtp, SendGrid, Ses or Pickup."),
        };
        // Outermost: hold back non-essential mail to bouncing addresses (and record SMTP refusals) before counting a send.
        var provider = settings.Mode.ToLowerInvariant();
        services.AddSingleton<IEmailSender>(sp => new SuppressingEmailSender(
            new InstrumentedEmailSender(sender(sp), provider, sp.GetRequiredService<EmailDeliveryStatus>(), sp.GetRequiredService<TimeProvider>()),
            provider, sp, sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<SuppressingEmailSender>() ?? NullLogger<SuppressingEmailSender>.Instance));

        // Bounce and complaint webhooks (docs/operations/email.md).
        services.TryAddSingleton<ISnsCertificateSource, HttpSnsCertificateSource>();
        services.TryAddSingleton<SnsMessageVerifier>();
        services.TryAddSingleton<EmailEventRecorder>();

        var notifications = configuration.GetSection("Notifications");
        services.RemoveAll<NotificationOptions>();
        services.AddSingleton(new NotificationOptions
        {
            PublicBaseUrl = notifications["PublicBaseUrl"],
            DigestHourLocal = Math.Clamp(int.TryParse(notifications["DigestHourLocal"], out var hour) ? hour : 7, 0, 23),
        });
        return services;
    }

    /// <summary>A list from configuration: array entries (<c>X__0</c>, <c>X__1</c>) or one comma-separated value.</summary>
    private static IReadOnlyList<string> ListOf(IConfigurationSection section)
    {
        var values = section.GetChildren().Select(c => c.Value).Append(section.Value)
            .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return values.Distinct(StringComparer.Ordinal).ToList();
    }
}
