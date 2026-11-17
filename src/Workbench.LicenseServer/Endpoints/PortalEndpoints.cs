using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.LicenseServer.Data;
using Workbench.LicenseServer.Licensing;
using Workbench.LicenseServer.Mail;
using Workbench.LicenseServer.Stripe;

namespace Workbench.LicenseServer.Endpoints;

/// <summary>The customer portal: sign in with an emailed link, see the subscription, download the current key, open Stripe's customer portal.</summary>
public static class PortalEndpoints
{
    private const string Cookie = "wb_portal";
    private const string Purpose = "Workbench.LicenseServer.Portal";

    public static void MapPortalEndpoints(this WebApplication app)
    {
        app.MapGet("/portal", async (HttpContext http, LicenseDb db, LicenseService svc, ILicenseSigner _) =>
        {
            var customer = await CurrentAsync(http, db);
            if (customer is null) return Html(LoginPage(null));
            var subs = (await db.Subscriptions.AsNoTracking().Where(s => s.CustomerId == customer.Id).ToListAsync()).OrderByDescending(s => s.CreatedUtc).ToList();
            var licenses = (await db.Licenses.AsNoTracking().Where(l => l.CustomerId == customer.Id).ToListAsync()).OrderByDescending(l => l.CreatedUtc).ToList();
            var sb = new StringBuilder($"<h1>{E(customer.Name)}</h1><p class=muted>Signed in as {E(customer.Email)}</p>");
            sb.Append("<h2>Subscriptions</h2>");
            if (subs.Count == 0) sb.Append("<p>No subscriptions yet.</p>");
            foreach (var s in subs)
            {
                var lic = licenses.FirstOrDefault(l => l.SubscriptionId == s.Id);
                sb.Append($"<div class=card><b>{E(string.IsNullOrEmpty(s.Modules) ? "Core" : "Core + " + s.Modules.Replace(",", ", "))}</b> <span class=pill>{s.Status}{(s.IsTrial ? " (trial)" : "")}</span>");
                sb.Append($"<br>{s.Seats} full seats, {s.LightSeats} light seats<br>Paid through {s.TermEndUtc:yyyy-MM-dd}{(s.CancelAtPeriodEnd ? " (ends then)" : "")}");
                if (lic != null && lic.RevokedUtc == null && s.Status != SubscriptionStatus.Canceled)
                    sb.Append($"<br><a class=btn href=\"/portal/key/{lic.Id}\">Download current key</a>");
                sb.Append("</div>");
            }
            var offline = licenses.Where(l => l.Kind == LicenseKind.Offline).ToList();
            if (offline.Count > 0)
            {
                sb.Append("<h2>Offline keys</h2>");
                foreach (var l in offline)
                    sb.Append($"<div class=card>{E(l.Licensee)}: {l.Seats} seats, expires {l.ExpiresOn:yyyy-MM-dd} {(l.RevokedUtc != null ? "<span class=pill>revoked</span>" : $"<a class=btn href=\"/portal/key/{l.Id}\">Download key</a>")}</div>");
            }
            if (customer.StripeCustomerId != null)
                sb.Append("<form method=post action=/portal/manage><p>Change seats, update payment details or cancel:</p><button class=btn>Manage billing in Stripe</button></form>");
            sb.Append("<form method=post action=/portal/logout><button class=link>Sign out</button></form>");
            return Html(Page("Your Workbench license", sb.ToString()));
        });

        app.MapPost("/portal/login", async (HttpContext http, LicenseDb db, IMailer mailer, IOptions<PortalOptions> options, TimeProvider clock) =>
        {
            var form = await http.Request.ReadFormAsync();
            var email = (form["email"].FirstOrDefault() ?? "").Trim().ToLowerInvariant();
            var customer = email.Length == 0 ? null : (await db.Customers.Where(c => c.Email == email).ToListAsync()).FirstOrDefault();
            if (customer != null)
            {
                var token = Base64Url(RandomNumberGenerator.GetBytes(32));
                var now = clock.GetUtcNow().UtcDateTime;
                db.LoginTokens.Add(new LoginToken { CustomerId = customer.Id, TokenHash = Hash(token), ExpiresUtc = now.AddMinutes(options.Value.LoginLinkMinutes) });
                await db.SaveChangesAsync();
                var baseUrl = (options.Value.PublicBaseUrl ?? $"{http.Request.Scheme}://{http.Request.Host}").TrimEnd('/');
                await mailer.SendAsync(customer.Email, "Sign in to your Workbench license portal",
                    $"Use this link to sign in (valid {options.Value.LoginLinkMinutes} minutes, one use):\n\n{baseUrl}/portal/verify?token={token}\n\nIf you didn't ask for it, ignore this email.");
            }
            // The same answer whether or not the address is known, so the form can't be used to find customers.
            return Html(LoginPage("If that address belongs to a customer, a sign-in link is on its way."));
        }).RequireRateLimiting("login");

        app.MapGet("/portal/verify", async (HttpContext http, string? token, LicenseDb db, IDataProtectionProvider dp, IOptions<PortalOptions> options, TimeProvider clock) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var hash = Hash(token ?? "");
            var row = await db.LoginTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (row is null || row.UsedUtc != null || row.ExpiresUtc < now)
                return Html(LoginPage("That sign-in link has expired or was already used. Enter your email for a new one."), 400);
            row.UsedUtc = now;
            await db.SaveChangesAsync();
            var expires = now.AddHours(options.Value.SessionHours);
            var protector = dp.CreateProtector(Purpose).ToTimeLimitedDataProtector();
            http.Response.Cookies.Append(Cookie, protector.Protect(row.CustomerId.ToString(), new DateTimeOffset(expires)),
                new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps, Expires = expires, Path = "/" });
            return Results.Redirect("/portal");
        });

        app.MapPost("/portal/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
            return Results.Redirect("/portal");
        });

        app.MapGet("/portal/key/{id:guid}", async (Guid id, HttpContext http, LicenseDb db, LicenseService svc) =>
        {
            var customer = await CurrentAsync(http, db);
            if (customer is null) return Results.Redirect("/portal");
            var license = await db.Licenses.FirstOrDefaultAsync(l => l.Id == id && l.CustomerId == customer.Id);
            if (license is null) return Results.NotFound();
            IssuedKey? key;
            if (license.Kind == LicenseKind.Offline)
                key = license.RevokedUtc != null ? null : (await db.IssuedKeys.AsNoTracking().Where(k => k.LicenseId == id).ToListAsync()).OrderByDescending(k => k.IssuedUtc).FirstOrDefault();
            else
                key = await svc.CurrentKeyAsync(license);
            if (key is null) return Results.Problem("There is no current key for this license (canceled, lapsed or revoked).", statusCode: 402);
            return Results.File(Encoding.UTF8.GetBytes(key.Key + "\n"), "text/plain", "workbench-license-key.txt");
        });

        app.MapPost("/portal/manage", async (HttpContext http, LicenseDb db, IStripeApi stripe, IOptions<StripeOptions> options, IOptions<PortalOptions> portal) =>
        {
            var customer = await CurrentAsync(http, db);
            if (customer is null) return Results.Redirect("/portal");
            if (customer.StripeCustomerId is null) return Results.Problem("Billing for this account is handled by your account manager.", statusCode: 400);
            var back = options.Value.PortalReturnUrl ?? ((portal.Value.PublicBaseUrl ?? $"{http.Request.Scheme}://{http.Request.Host}").TrimEnd('/') + "/portal");
            try { return Results.Redirect(await stripe.CreatePortalSessionAsync(customer.StripeCustomerId, back)); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                http.RequestServices.GetRequiredService<ILogger<LicenseDb>>().LogError(ex, "Stripe customer portal session failed");
                return Results.Problem("Billing is temporarily unavailable. Try again shortly.", statusCode: 502);
            }
        });
    }

    private static async Task<Customer?> CurrentAsync(HttpContext http, LicenseDb db)
    {
        if (!http.Request.Cookies.TryGetValue(Cookie, out var cookie)) return null;
        try
        {
            var protector = http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).ToTimeLimitedDataProtector();
            return Guid.TryParse(protector.Unprotect(cookie), out var id) ? await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id) : null;
        }
        catch (CryptographicException) { return null; }
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static IResult Html(string html, int status = 200) => Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);

    private static string LoginPage(string? message) => Page("Workbench license portal",
        $"<h1>Workbench license portal</h1>{(message is null ? "" : $"<p class=card>{E(message)}</p>")}"
        + "<p>Enter the email address on your account and we'll send you a sign-in link.</p>"
        + "<form method=post action=/portal/login><input type=email name=email required placeholder=\"you@company.com\" autofocus> <button class=btn>Email me a link</button></form>");

    private static string Page(string title, string body) => $$$"""
        <!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1"><title>{{{E(title)}}}</title>
        <style>
        :root{--bg:#fff;--fg:#1c2530;--muted:#5d6b7a;--card:#f4f6f9;--accent:#1f5fbf}
        @media(prefers-color-scheme:dark){:root{--bg:#14181d;--fg:#e6ebf0;--muted:#9aa7b5;--card:#1e252d;--accent:#6aa5ff}}
        body{font:16px/1.5 system-ui,sans-serif;background:var(--bg);color:var(--fg);max-width:42rem;margin:2rem auto;padding:0 1rem}
        .muted{color:var(--muted)}.card{background:var(--card);padding:1rem;border-radius:8px;margin:1rem 0}
        .pill{background:var(--accent);color:#fff;border-radius:99px;padding:0 .6rem;font-size:.8rem}
        .btn{background:var(--accent);color:#fff;border:0;border-radius:6px;padding:.5rem 1rem;text-decoration:none;display:inline-block;cursor:pointer;font:inherit}
        .link{background:none;border:0;color:var(--accent);cursor:pointer;font:inherit;padding:0;margin-top:1rem}
        input{padding:.5rem;font:inherit;border-radius:6px;border:1px solid var(--muted);min-width:16rem}
        </style></head><body>{{{body}}}</body></html>
        """;
}
