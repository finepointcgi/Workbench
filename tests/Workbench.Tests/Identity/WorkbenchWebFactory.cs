using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Web.Identity;

namespace Workbench.Tests.Identity;

/// <summary>
/// The real web host. Development runs on a temporary SQLite file with the seeded data. With
/// <c>testAuth</c>, requests carrying <c>X-Test-User: email</c> are signed in as that user, standing in for the
/// cookie or bearer token.
/// </summary>
internal sealed class WorkbenchWebFactory(string environment, bool testAuth = false, string? oidcAuthority = null)
    : WebApplicationFactory<Program>
{
    public const string UserHeader = "X-Test-User";
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"workbench-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("ConnectionStrings:Workbench", $"Data Source={_database};Pooling=False");
        builder.UseSetting("Costpoint:Source", "None");
        builder.UseSetting("Storage:Root", Path.Combine(Path.GetTempPath(), $"workbench-blobs-{Guid.NewGuid():N}"));
        if (oidcAuthority is not null)
        {
            builder.UseSetting("Authentication:Oidc:Authority", oidcAuthority);
            builder.UseSetting("Authentication:Oidc:ClientId", "workbench");
        }

        if (testAuth)
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
                services.PostConfigure<PolicySchemeOptions>(AuthenticationSetup.SelectorScheme, o =>
                    o.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey(UserHeader) ? "Test" : "Cookies");
            });
        }
    }

    public HttpClient CreateClientAs(string? email)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (email is not null)
        {
            client.DefaultRequestHeaders.Add(UserHeader, email);
        }

        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        File.Delete(_database);
    }

    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var email))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("email", email.ToString()), new Claim("name", email.ToString())], "Test", "name", "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
