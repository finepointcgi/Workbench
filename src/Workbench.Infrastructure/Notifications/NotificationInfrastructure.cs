using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Notifications;

namespace Workbench.Infrastructure.Notifications;

public static class NotificationInfrastructure
{
    /// <summary>
    /// Registers the email sender. <c>Email:Mode</c> is <c>Pickup</c> (write .eml files to <c>Email:PickupFolder</c>) or
    /// <c>Smtp</c> (<c>Email:Smtp:Host</c>, <c>Port</c>, <c>EnableSsl</c>, <c>UserName</c>, <c>Password</c>). When unset it's
    /// <c>Smtp</c> if a host is configured, otherwise <c>Pickup</c>. Also binds <c>Notifications:PublicBaseUrl</c> and
    /// <c>Notifications:DigestHourLocal</c>.
    /// </summary>
    public static IServiceCollection AddNotificationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var email = configuration.GetSection("Email");
        var settings = new EmailSettings
        {
            From = email["From"] is { Length: > 0 } from ? from : "workbench@localhost",
            PickupFolder = email["PickupFolder"] is { Length: > 0 } folder ? folder : EmailSettings.DefaultPickupFolder,
            SmtpHost = email["Smtp:Host"],
            SmtpPort = int.TryParse(email["Smtp:Port"], out var port) ? port : 25,
            SmtpEnableSsl = !bool.TryParse(email["Smtp:EnableSsl"], out var ssl) || ssl,
            SmtpUserName = email["Smtp:UserName"],
            SmtpPassword = email["Smtp:Password"],
        };
        settings.Mode = email["Mode"] is { Length: > 0 } mode ? mode : string.IsNullOrWhiteSpace(settings.SmtpHost) ? "Pickup" : "Smtp";
        services.AddSingleton(settings);

        services.RemoveAll<IEmailSender>();
        switch (settings.Mode.ToLowerInvariant())
        {
            case "pickup":
                services.AddSingleton<IEmailSender, PickupFolderEmailSender>();
                break;
            case "smtp":
                services.AddSingleton<IEmailSender, SmtpEmailSender>();
                break;
            default:
                throw new InvalidOperationException($"Unsupported Email:Mode '{settings.Mode}'. Use Smtp or Pickup.");
        }

        var notifications = configuration.GetSection("Notifications");
        services.RemoveAll<NotificationOptions>();
        services.AddSingleton(new NotificationOptions
        {
            PublicBaseUrl = notifications["PublicBaseUrl"],
            DigestHourLocal = Math.Clamp(int.TryParse(notifications["DigestHourLocal"], out var hour) ? hour : 7, 0, 23),
        });
        return services;
    }
}
