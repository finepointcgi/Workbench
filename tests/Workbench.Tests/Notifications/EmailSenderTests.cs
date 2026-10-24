using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Notifications;
using Workbench.Infrastructure.Notifications;

namespace Workbench.Tests.Notifications;

public sealed class EmailSenderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "workbench-mail-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Pickup_sender_writes_an_eml_with_text_and_html_parts()
    {
        var sender = new PickupFolderEmailSender(new EmailSettings { PickupFolder = _folder, From = "workbench@contoso.test" });

        await sender.SendAsync(new EmailMessage("pm@example.com", "Status changed to Blocked", "Plain body", "<p>Html body</p>"));

        var file = Assert.Single(Directory.GetFiles(_folder, "*.eml"));
        var content = await File.ReadAllTextAsync(file);
        Assert.Contains("To: pm@example.com", content);
        Assert.Contains("workbench@contoso.test", content);
        Assert.Contains("Subject: Status changed to Blocked", content);
        Assert.Contains("multipart/alternative", content);
        Assert.Contains("text/plain", content);
        Assert.Contains("text/html", content);
    }

    [Theory]
    [InlineData(null, null, typeof(PickupFolderEmailSender))]
    [InlineData(null, "smtp.contoso.test", typeof(SmtpEmailSender))]
    [InlineData("Pickup", "smtp.contoso.test", typeof(PickupFolderEmailSender))]
    [InlineData("Smtp", null, typeof(SmtpEmailSender))]
    [InlineData("SendGrid", null, typeof(SendGridEmailSender))]
    [InlineData("ses", null, typeof(SesEmailSender))]
    public void Mode_selects_the_sender(string? mode, string? host, Type expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Mode"] = mode,
            ["Email:Smtp:Host"] = host,
            ["Email:PickupFolder"] = _folder,
            ["Email:Ses:Region"] = "us-east-1",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IEmailSender, NullEmailSender>();

        services.AddNotificationInfrastructure(configuration);

        var outer = Assert.IsType<SuppressingEmailSender>(services.BuildServiceProvider().GetRequiredService<IEmailSender>());
        var sender = Assert.IsType<InstrumentedEmailSender>(outer.Inner);
        Assert.IsType(expected, sender.Inner);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
