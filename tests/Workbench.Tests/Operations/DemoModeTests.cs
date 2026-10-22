using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Workbench.Application.Identity;
using Workbench.Contracts.Assistant;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;
using Workbench.Infrastructure.Persistence;
using Workbench.Tests.Identity;
using Workbench.Web.Identity;
using Workbench.Web.Security;

namespace Workbench.Tests.Operations;

/// <summary>
/// The public demo's guard (docs/demo-hosting.md): the dev sign-in page and the fake assistant without an identity
/// provider, but only with the Demo environment and Demo:Enabled together, never in Production, and only on a demo database.
/// </summary>
public sealed partial class DemoModeTests
{
    private const string HarperEmail = "hr@example.com";

    // What the demo image sets on top of appsettings.Demo.json, minus the paths.
    private static Dictionary<string, string?> DemoSettings(params (string Key, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            [DemoMode.EnabledKey] = "true",
            ["Security:TlsTerminatedByProxy"] = "true",
            ["Scim:Enabled"] = "false",
            ["Email:Mode"] = "Pickup",
            ["Assistant:EnableFakeProvider"] = "true",
            ["Database:Provider"] = "Sqlite",
        };
        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return values;
    }

    [Fact]
    public void The_demo_environment_with_demo_enabled_starts_without_an_identity_provider()
    {
        Assert.Empty(Problems(DemoMode.EnvironmentName, DemoSettings()));
        Assert.True(DemoMode.IsActive(Config(DemoSettings()), new FakeEnvironment(DemoMode.EnvironmentName)));
        Assert.True(DevSignIn.IsEnabled(new FakeEnvironment(DemoMode.EnvironmentName), new OidcSettings(), Config(DemoSettings())));
    }

    [Fact]
    public void Production_refuses_demo_mode_and_keeps_the_dev_sign_in_and_fake_assistant_off()
    {
        var problems = Problems(Environments.Production, DemoSettings());

        Assert.Contains(problems, p => p.Contains(DemoMode.EnabledKey) && p.Contains("Production"));
        Assert.Contains(problems, p => p.Contains("dev sign-in page is enabled") || p.Contains("Authentication:Oidc:Authority"));
        Assert.Contains(problems, p => p.Contains("EnableFakeProvider"));
        Assert.False(DemoMode.IsActive(Config(DemoSettings()), new FakeEnvironment(Environments.Production)));
        Assert.False(DevSignIn.IsEnabled(new FakeEnvironment(Environments.Production), new OidcSettings(), Config(DemoSettings())));
    }

    [Fact]
    public void The_demo_environment_alone_is_not_demo_mode()
    {
        var problems = Problems(DemoMode.EnvironmentName, DemoSettings((DemoMode.EnabledKey, null)));

        Assert.Contains(problems, p => p.Contains("Authentication:Oidc:Authority"));
        Assert.Contains(problems, p => p.Contains("EnableFakeProvider"));
        Assert.False(DevSignIn.IsEnabled(new FakeEnvironment(DemoMode.EnvironmentName), new OidcSettings(), Config(DemoSettings((DemoMode.EnabledKey, "false")))));
    }

    [Fact]
    public void Demo_enabled_in_any_other_environment_is_refused()
    {
        Assert.Contains(Problems(Environments.Staging, DemoSettings()), p => p.Contains(DemoMode.EnabledKey));
        // Development has its own dev sign-in and is never checked.
        Assert.Empty(Problems(Environments.Development, DemoSettings()));
    }

    [Fact]
    public void The_fake_assistant_is_only_allowed_in_demo_mode()
    {
        var fake = Config(new() { ["Assistant:EnableFakeProvider"] = "true" });

        Assert.Contains(ProductionConfigurationCheck.AssistantProblems(fake), p => p.Contains("EnableFakeProvider"));
        Assert.Empty(ProductionConfigurationCheck.AssistantProblems(fake, allowFakeProvider: true));
        Assert.Contains(Problems(Environments.Staging, new() { ["Assistant:EnableFakeProvider"] = "true" }), p => p.Contains("EnableFakeProvider"));
    }

    [Fact]
    public void The_demo_is_single_tenant_and_sends_no_email()
    {
        Assert.Contains(Problems(DemoMode.EnvironmentName, DemoSettings(("Tenancy:Mode", "MultiTenant"))), p => p.Contains("SingleTenant"));
        Assert.Contains(Problems(DemoMode.EnvironmentName, DemoSettings(("Email:Mode", "Smtp"), ("Email:Smtp:Host", "smtp.example.com"))), p => p.Contains("Email:Mode"));
        Assert.Contains(Problems(DemoMode.EnvironmentName, DemoSettings(("Email:Mode", null), ("Email:Smtp:Host", "smtp.example.com"))), p => p.Contains("Email:Mode"));
    }

    [Fact]
    public async Task Demo_mode_refuses_a_database_with_real_users()
    {
        await using var db = new WorkbenchDbContext(TestDb.Options(DatabaseProvider.Sqlite, SqliteTemplates.NewDatabase()));
        await DemoMode.EnsureDemoDatabaseAsync(db); // Empty: fine.

        await RoleStore.AddUserAsync(db, new AppUser { Email = "cfo@realcompany.com", DisplayName = "Real CFO", CreatedAtUtc = DateTime.UtcNow }, OrgRoles.Admin);
        await db.SaveChangesAsync();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DemoMode.EnsureDemoDatabaseAsync(db));
        Assert.Contains("isn't a demo database", refused.Message);

        await RoleStore.AddUserAsync(db, new AppUser { Email = DemoMode.DemoAdminEmail, DisplayName = "Dana Lee", CreatedAtUtc = DateTime.UtcNow }, OrgRoles.Admin);
        await db.SaveChangesAsync();
        await DemoMode.EnsureDemoDatabaseAsync(db);
    }

    [Fact]
    public async Task The_demo_host_seeds_the_sample_people_and_lets_testers_pick_one()
    {
        await using var factory = DemoHost();
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost") });

        using var home = await browser.GetAsync("/");
        var page = await browser.GetStringAsync(DevSignIn.Path);
        using var signIn = await SignInAsync(browser, page, HarperEmail);
        var me = await browser.GetFromJsonAsync<Workbench.Contracts.Identity.CurrentUserInfo>("/api/v1/users/me/context");

        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Contains(DevSignIn.Path, home.Headers.Location!.OriginalString);
        Assert.Contains("Workbench demo", page);
        Assert.Contains("reset every night", page);
        foreach (var email in new[] { DemoMode.DemoAdminEmail, HarperEmail, "mia.materials@example.com", "bd@example.com", "pricing@example.com" })
        {
            Assert.Contains(email, page);
        }

        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal(HarperEmail, me!.Email);

        // Invites stay required: an email that isn't a demo user can't sign itself up.
        using var stranger = await SignInAsync(browser, await browser.GetStringAsync(DevSignIn.Path), "someone@elsewhere.com");
        Assert.Contains(AccountEndpoints.AccessDeniedPath, stranger.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_demo_assistant_runs_on_the_fake_provider()
    {
        await using var factory = DemoHost();
        using var pm = factory.CreateClientAs("pm@example.com");

        var status = await pm.GetFromJsonAsync<AssistantStatusDto>("/api/v1/assistant/status");

        using var answer = await pm.PostAsJsonAsync("/api/v1/assistant/ask", new AskAssistantRequest("What projects am I managing?"));
        var events = await answer.Content.ReadAsStringAsync();

        Assert.NotNull(status);
        Assert.True(status.CanAsk, status.Reason);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.DoesNotContain("turned off", events);
        Assert.Contains("\"kind\"", events, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reset_deletes_the_demo_data_and_refuses_outside_demo_mode()
    {
        await using (var development = new WorkbenchWebFactory(Environments.Development))
        {
            _ = development.Server;
            await Assert.ThrowsAsync<InvalidOperationException>(() => DemoReset.DeleteDemoDataAsync(development.Services));
        }

        await using var factory = DemoHost();
        _ = factory.Server;
        var blobs = factory.Services.GetRequiredService<IConfiguration>()["Storage:Root"]!;
        Directory.CreateDirectory(Path.Combine(blobs, "attachments"));
        await File.WriteAllTextAsync(Path.Combine(blobs, "attachments", "note.txt"), "demo");

        await DemoReset.DeleteDemoDataAsync(factory.Services);

        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync();
        Assert.False(await db.Database.CanConnectAsync() && await db.Set<AppUser>().AnyAsync());
        Assert.True(Directory.Exists(blobs));
        Assert.Empty(Directory.EnumerateFileSystemEntries(blobs));
    }

    [Fact]
    public async Task The_nightly_job_deletes_the_demo_data_at_the_reset_hour_and_stops_the_host()
    {
        await using var factory = DemoHost();
        _ = factory.Server;
        var lifetime = new RecordingLifetime();
        var justBefore = new DateTimeOffset(2026, 10, 20, 7, 59, 59, 900, TimeSpan.Zero);
        using var job = new DemoResetBackgroundService(factory.Services, factory.Services.GetRequiredService<IConfiguration>(), lifetime,
            new ClockAt(justBefore), Microsoft.Extensions.Logging.Abstractions.NullLogger<DemoResetBackgroundService>.Instance);

        await job.StartAsync(CancellationToken.None);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync();
        Assert.False(await db.Database.CanConnectAsync() && await db.Set<AppUser>().AnyAsync());
    }

    private sealed class ClockAt(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped.TrySetResult();
    }

    private static WorkbenchWebFactory DemoHost() => new(DemoMode.EnvironmentName, testAuth: true, settings: new Dictionary<string, string>
    {
        [DemoMode.EnabledKey] = "true",
        ["Demo:NightlyReset"] = "false",
        ["Email:PickupFolder"] = Path.Combine(Path.GetTempPath(), $"workbench-demo-mail-{Guid.NewGuid():N}"),
    }, seededTemplate: true); // the demo seeds the Development sample data, then its own settings

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string page, string email) =>
        client.PostAsync(DevSignIn.Path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = TokenRegex().Match(page).Groups[1].Value,
            ["email"] = email,
            ["returnUrl"] = "/",
        }));

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IReadOnlyList<string> Problems(string environment, Dictionary<string, string?> values) =>
        ProductionConfigurationCheck.FindProblems(Config(values), new FakeEnvironment(environment));

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Workbench.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
