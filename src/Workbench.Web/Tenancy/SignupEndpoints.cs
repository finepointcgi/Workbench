using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Tenancy;
using Workbench.Contracts;
using Workbench.Contracts.Tenancy;
using Workbench.Infrastructure.Tenancy;
using static Workbench.Web.Identity.AccountPages;

namespace Workbench.Web.Tenancy;

/// <summary>
/// The hosted front door, on the base domain only (MultiTenant mode with <c>Tenancy:Signup:Enabled</c>):
/// <c>GET/POST /signup</c> (the self-service signup page), and <c>/api/v1/host/entitlements</c> (the license server
/// sets a tenant's entitlement from Stripe events, authenticated by <c>X-Host-Key</c>). docs/admin-guide/hosted-signup.md.
/// </summary>
public static class SignupEndpoints
{
    public const string SignupPath = "/signup";
    public const string EntitlementsPath = "/api/v1/host/entitlements";
    public const string KeyHeader = "X-Host-Key";

    private const string SubdomainPattern = @"[a-z][a-z0-9\-]{1,38}[a-z0-9]";
    private static readonly string[] CompanyTypes = ["Small business", "Government contractor", "Defense contractor", "Professional services", "Other"];

    public static IEndpointRouteBuilder MapWorkbenchSignup(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<TenancyOptions>().IsMultiTenant)
        {
            return app;
        }

        app.MapGet(SignupPath, (HttpContext http, TenancyOptions tenancy, IAntiforgery antiforgery, ISignupBotProtection bots, string? source, string? utm_source) =>
            Offered(http, tenancy) ? FormPage(http, tenancy, antiforgery, bots, null, new Form(source ?? utm_source), null) : Results.NotFound())
            .AllowAnonymous().ExcludeFromDescription();

        app.MapPost(SignupPath, async (HttpContext http, TenancyOptions tenancy, IAntiforgery antiforgery, ISignupBotProtection bots, SignupService signup,
            [FromForm] string? company, [FromForm] string? subdomain, [FromForm] string? name, [FromForm] string? email, [FromForm] string? companyType,
            [FromForm] string? terms, [FromForm] string? website, [FromForm] string? ts, [FromForm] string? source) =>
        {
            if (!Offered(http, tenancy))
            {
                return Results.NotFound();
            }

            var form = new Form(source, company, subdomain, name, email, companyType, terms is not null);
            var address = http.Connection.RemoteIpAddress?.ToString();
            if (!await bots.VerifyAsync(new SignupBotCheck(website, ts, address, http.Request.Form.ToDictionary(f => f.Key, f => (string?)f.Value.ToString())), http.RequestAborted))
            {
                // Look like a success: a bot learns nothing from the refusal.
                return DonePage(form.Email, tenancy);
            }

            try
            {
                var result = await signup.SignUpAsync(new SignupRequest(company ?? "", subdomain ?? "", name ?? "", email ?? "", companyType, form.Terms, source, address), http.RequestAborted);
                return DonePage(form.Email, tenancy, result);
            }
            catch (SignupRefusedException ex)
            {
                var page = FormPage(http, tenancy, antiforgery, bots, ex.Message, form, null, ex.RateLimited ? StatusCodes.Status429TooManyRequests : StatusCodes.Status400BadRequest);
                return page;
            }
        }).AllowAnonymous().ExcludeFromDescription();

        MapEntitlements(app);
        return app;
    }

    private static bool Offered(HttpContext http, TenancyOptions tenancy) =>
        tenancy.IsMultiTenant && tenancy.Signup.Enabled && TenantResolutionMiddleware.IsHostSite(http);

    private sealed record Form(string? Source, string? Company = null, string? Subdomain = null, string? Name = null, string? Email = null, string? CompanyType = null, bool Terms = false);

    private static IResult FormPage(HttpContext http, TenancyOptions tenancy, IAntiforgery antiforgery, ISignupBotProtection bots, string? error, Form form, string? notice, int status = StatusCodes.Status200OK)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        var options = tenancy.Signup;
        var terms = string.IsNullOrWhiteSpace(options.TermsUrl)
            ? "I accept the terms of service"
            : $"""I accept the <a href="{E(options.TermsUrl)}" target="_blank" rel="noopener">terms of service</a>""";
        var types = string.Concat(CompanyTypes.Select(t => $"""<option value="{E(t)}"{(t == form.CompanyType ? " selected" : "")}>{E(t)}</option>"""));
        return Page("Start your free trial", $"""
            <h1>Start your {options.TrialDays}-day free trial</h1>
            <p class="muted">Every module, no card needed. We'll email you a link to sign in: there's no password to choose.</p>
            {Error(error)}{Notice(notice)}
            {Form(tokens, SignupPath, Field("Company name", "company", "text", "organization", form.Company, autofocus: error is null, extra: "maxlength=\"200\"")
                + $"""
                <label for="f-subdomain">Your address</label>
                <input id="f-subdomain" name="subdomain" type="text" autocomplete="off" required minlength="3" maxlength="40" pattern="{SubdomainPattern}" value="{E(form.Subdomain)}" aria-describedby="f-subdomain-hint" autocapitalize="none" spellcheck="false" />
                <small id="f-subdomain-hint" class="hint">Lowercase letters, digits and hyphens. You'll use <b>your-address.{E(tenancy.BaseDomain)}</b>.</small>
                """
                + Field("Your name", "name", "text", "name", form.Name, extra: "maxlength=\"200\"")
                + Field("Work email", "email", "email", "email", form.Email, extra: "maxlength=\"320\"")
                + $"""
                <label for="f-companyType">Company type</label>
                <select id="f-companyType" name="companyType" style="display:block;width:100%;padding:10px 12px;border:1px solid var(--line);border-radius:6px;font:inherit;background:var(--card);color:var(--text);margin-bottom:10px">{types}</select>
                <label class="check" style="font-weight:400"><input type="checkbox" name="terms" value="yes" required style="display:inline-block;width:auto;margin:0 6px 0 0"{(form.Terms ? " checked" : "")} /> {terms}</label>
                <div class="hp" aria-hidden="true" style="position:absolute;left:-10000px;width:1px;height:1px;overflow:hidden"><label>Leave this empty<input type="text" name="{HoneypotBotProtection.HoneypotField}" tabindex="-1" autocomplete="off" /></label></div>
                <input type="hidden" name="{HoneypotBotProtection.TokenField}" value="{E(bots.IssueToken())}" />
                <input type="hidden" name="source" value="{E(form.Source)}" />
                <button type="submit">Start free trial</button>
                """)}
            """, status);
    }

    private static IResult DonePage(string? email, TenancyOptions tenancy, SignupResult? result = null) =>
        Page("Check your email", $"""
            <h1>Check your email</h1>
            <p>We sent a sign-in link to <b>{E(email)}</b>. It works once and expires in {tenancy.Signup.MagicLinkMinutes} minutes.</p>
            {(result?.Outcome == SignupOutcome.InProgress ? "<p>Your organization is still being set up; the email follows in a minute.</p>" : "")}
            <p class="muted">No email? Check your spam folder, or submit this form again with the same address and email to get a new link.</p>
            """);

    // ----- The license server's entitlement endpoint -----

    private static void MapEntitlements(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(EntitlementsPath).AllowAnonymous().ExcludeFromDescription().DisableAntiforgery();
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var tenancy = http.RequestServices.GetRequiredService<TenancyOptions>();
            var expected = tenancy.Signup.EntitlementApiKey;
            if (!tenancy.IsMultiTenant || !TenantResolutionMiddleware.IsHostSite(http) || string.IsNullOrWhiteSpace(expected) || expected.Length < 32)
            {
                return Results.NotFound();
            }

            var given = http.Request.Headers[KeyHeader].ToString();
            if (given.Length == 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SignupEndpoints)).LogWarning("Rejected an entitlement call with a missing or wrong {Header}.", KeyHeader);
                return Results.Json(new { error = "A valid " + KeyHeader + " header is required." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return await next(context);
        });

        group.MapGet("", async (IDbContextFactory<CatalogDbContext> catalog, TenantEntitlements entitlements, CancellationToken ct) =>
        {
            await using var db = await catalog.CreateDbContextAsync(ct);
            var tenants = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Slug, ct);
            return (await entitlements.ListAsync(ct)).Where(e => tenants.ContainsKey(e.Key))
                .Select(e => new HostTenantEntitlementDto(e.Key, tenants[e.Key], entitlements.ToDto(e.Value))).OrderBy(e => e.Slug).ToList();
        });

        group.MapGet("/{slug}", async (string slug, IDbContextFactory<CatalogDbContext> catalog, TenantEntitlements entitlements, CancellationToken ct) =>
        {
            var tenant = await FindAsync(catalog, slug, ct);
            if (tenant is null)
            {
                return Results.NotFound(new { error = "No such tenant." });
            }

            return await entitlements.FindAsync(tenant.Id, ct) is { } row
                ? Results.Ok(new HostTenantEntitlementDto(tenant.Id, tenant.Slug, entitlements.ToDto(row)))
                : Results.NotFound(new { error = "The tenant has no entitlement yet." });
        });

        // Idempotent: the same body twice leaves the same state, and a body whose eventAtUtc is older than the last applied is ignored.
        group.MapPut("/{slug}", async (string slug, SetEntitlementRequest request, IDbContextFactory<CatalogDbContext> catalog, TenantEntitlements entitlements, CancellationToken ct) =>
        {
            var tenant = await FindAsync(catalog, slug, ct);
            if (tenant is null)
            {
                return Results.NotFound(new { error = "No such tenant." });
            }

            try
            {
                var row = await entitlements.ApplyAsync(tenant.Id, request, "license-server", "license-server", ct);
                return Results.Ok(new HostTenantEntitlementDto(tenant.Id, tenant.Slug, entitlements.ToDto(row)));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static async Task<Tenant?> FindAsync(IDbContextFactory<CatalogDbContext> catalog, string slug, CancellationToken ct)
    {
        await using var db = await catalog.CreateDbContextAsync(ct);
        var value = (slug ?? "").Trim().ToLowerInvariant();
        return await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == value, ct);
    }
}
