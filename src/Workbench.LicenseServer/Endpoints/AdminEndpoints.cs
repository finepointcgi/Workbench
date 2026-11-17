using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.LicenseServer.Data;
using Workbench.LicenseServer.Licensing;
using Workbench.LicenseServer.Mail;

namespace Workbench.LicenseServer.Endpoints;

public sealed record CreateCustomerRequest(string Name, string Email, string? Notes);
public sealed record CreateSubscriptionRequest(string[] Modules, int Seats, int LightSeats = 0, DateOnly? TermStart = null, DateOnly? TermEnd = null, int? TermMonths = null, bool Trial = false);
public sealed record UpdateSubscriptionRequest(string[]? Modules, int? Seats, int? LightSeats);
public sealed record ExtendRequest(DateOnly? TermEnd = null, int? Months = null);
public sealed record OfflineKeyRequest(Guid CustomerId, string[] Modules, int Seats, int LightSeats = 0, DateOnly? Expires = null, int? Days = null, bool Trial = false, string? Licensee = null);
public sealed record RevokeRequest(string? Reason);
public sealed record EmailKeyRequest(bool Email = false);

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin/api").AddEndpointFilter(RequireAdminKey).AddEndpointFilter(MapErrors);

        admin.MapPost("/customers", async (CreateCustomerRequest r, LicenseDb db, TimeProvider clock) =>
        {
            if (string.IsNullOrWhiteSpace(r.Name)) throw new ArgumentException("A customer needs a name.");
            if (string.IsNullOrWhiteSpace(r.Email) || !r.Email.Contains('@')) throw new ArgumentException("A customer needs an email address.");
            var email = r.Email.Trim().ToLowerInvariant();
            if ((await db.Customers.Where(c => c.Email == email).ToListAsync()).Count > 0) throw new InvalidOperationException("A customer with that email already exists.");
            var c = new Customer { Name = r.Name.Trim(), Email = email, Notes = r.Notes, CreatedUtc = clock.GetUtcNow().UtcDateTime };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            return Results.Created($"/admin/api/customers/{c.Id}", c);
        });

        admin.MapGet("/customers", async (LicenseDb db) =>
            (await db.Customers.AsNoTracking().ToListAsync()).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase));

        admin.MapGet("/customers/{id:guid}", async (Guid id, LicenseDb db) =>
        {
            var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound();
            return Results.Ok(new
            {
                customer = c,
                subscriptions = (await db.Subscriptions.AsNoTracking().Where(s => s.CustomerId == id).ToListAsync()).OrderByDescending(s => s.CreatedUtc),
                licenses = (await db.Licenses.AsNoTracking().Where(l => l.CustomerId == id).ToListAsync()).OrderByDescending(l => l.CreatedUtc),
            });
        });

        admin.MapPost("/customers/{id:guid}/subscriptions", async (Guid id, CreateSubscriptionRequest r, LicenseDb db, LicenseService svc, TimeProvider clock) =>
        {
            if (!await db.Customers.AnyAsync(c => c.Id == id)) return Results.NotFound();
            var modules = Modules.Normalize(r.Modules);
            if (r.Seats < 0 || r.LightSeats < 0 || r.Seats + r.LightSeats == 0) throw new ArgumentException("Give the subscription at least one seat.");
            var start = r.TermStart ?? svc.Today;
            var end = r.TermEnd ?? start.AddMonths(r.TermMonths ?? 12);
            if (end <= start) throw new ArgumentException("The term must end after it starts.");
            var now = clock.GetUtcNow().UtcDateTime;
            var sub = new Subscription
            {
                CustomerId = id, Modules = Modules.Join(modules), Seats = r.Seats, LightSeats = r.LightSeats, IsTrial = r.Trial, Source = "Manual",
                Status = r.Trial ? SubscriptionStatus.Trialing : SubscriptionStatus.Active, CreatedUtc = now, UpdatedUtc = now,
                TermStartUtc = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TermEndUtc = end.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            };
            db.Subscriptions.Add(sub);
            await db.SaveChangesAsync();
            var license = await svc.EnsureSubscriptionLicenseAsync(sub);
            var key = await svc.IssueAsync(license, "Subscription created", "admin");
            return Results.Created($"/admin/api/subscriptions/{sub.Id}", new { subscription = sub, licenseId = license.Id, key = key.Key });
        });

        admin.MapPatch("/subscriptions/{id:guid}", async (Guid id, UpdateSubscriptionRequest r, LicenseDb db, LicenseService svc, TimeProvider clock) =>
        {
            var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id);
            if (sub is null) return Results.NotFound();
            if (r.Modules != null) sub.Modules = Modules.Join(r.Modules);
            if (r.Seats is { } seats) sub.Seats = seats >= 0 ? seats : throw new ArgumentException("Seats can't be negative.");
            if (r.LightSeats is { } light) sub.LightSeats = light >= 0 ? light : throw new ArgumentException("Light seats can't be negative.");
            sub.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
            return Results.Ok(await ReissueAsync(sub, "Entitlements changed", db, svc));
        });

        admin.MapPost("/subscriptions/{id:guid}/extend", async (Guid id, ExtendRequest r, LicenseDb db, LicenseService svc, TimeProvider clock) =>
        {
            var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id);
            if (sub is null) return Results.NotFound();
            var current = DateOnly.FromDateTime(sub.TermEndUtc);
            var end = r.TermEnd ?? current.AddMonths(r.Months ?? throw new ArgumentException("Give termEnd or months."));
            if (end <= current) throw new ArgumentException("Extending must move the end date later.");
            sub.TermEndUtc = end.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            if (sub.Status is SubscriptionStatus.PastDue or SubscriptionStatus.Canceled) sub.Status = sub.IsTrial ? SubscriptionStatus.Trialing : SubscriptionStatus.Active;
            sub.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
            return Results.Ok(await ReissueAsync(sub, $"Term extended to {end:yyyy-MM-dd}", db, svc));
        });

        admin.MapPost("/subscriptions/{id:guid}/cancel", async (Guid id, LicenseDb db, TimeProvider clock) =>
        {
            var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id);
            if (sub is null) return Results.NotFound();
            sub.Status = SubscriptionStatus.Canceled;
            sub.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
            return Results.Ok(sub);
        });

        admin.MapPost("/subscriptions/{id:guid}/reissue", async (Guid id, EmailKeyRequest r, LicenseDb db, LicenseService svc, IMailer mailer) =>
        {
            var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == id);
            if (sub is null) return Results.NotFound();
            var res = await ReissueAsync(sub, "Reissued by admin", db, svc);
            if (r.Email)
            {
                var to = (await db.Customers.AsNoTracking().FirstAsync(c => c.Id == sub.CustomerId)).Email;
                await mailer.SendAsync(to, "Your Workbench license key", $"Your current Workbench license key:\n\n{res.Key}");
            }
            return Results.Ok(res);
        });

        // An offline key for an air-gapped install: fixed terms, long-lived, never refreshed.
        admin.MapPost("/licenses/offline", async (OfflineKeyRequest r, LicenseDb db, LicenseService svc, IOptions<KeyOptions> opts, TimeProvider clock) =>
        {
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CustomerId);
            if (customer is null) return Results.NotFound();
            if (r.Seats < 0 || r.LightSeats < 0 || r.Seats + r.LightSeats == 0) throw new ArgumentException("Give the key at least one seat.");
            var expires = r.Expires ?? svc.Today.AddDays(r.Days ?? opts.Value.OfflineDays);
            if (expires <= svc.Today) throw new ArgumentException("The key must expire in the future.");
            var license = new License
            {
                CustomerId = customer.Id, Kind = LicenseKind.Offline, Licensee = string.IsNullOrWhiteSpace(r.Licensee) ? customer.Name : r.Licensee.Trim(),
                Modules = Modules.Join(r.Modules), Seats = r.Seats, LightSeats = r.LightSeats, IsTrial = r.Trial,
                ExpiresOn = expires.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), CreatedUtc = clock.GetUtcNow().UtcDateTime,
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            var key = await svc.IssueAsync(license, "Offline key issued by hand", "admin");
            return Results.Created($"/admin/api/licenses/{license.Id}", new { licenseId = license.Id, key = key.Key, expires = key.ExpiresOn });
        });

        admin.MapPost("/licenses/{id:guid}/revoke", async (Guid id, RevokeRequest r, LicenseDb db, TimeProvider clock) =>
        {
            var l = await db.Licenses.FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            l.RevokedUtc ??= clock.GetUtcNow().UtcDateTime;
            l.RevokedReason = r.Reason;
            await db.SaveChangesAsync();
            return Results.Ok(l);
        });

        admin.MapGet("/licenses/{id:guid}/history", async (Guid id, LicenseDb db) =>
        {
            var l = await db.Licenses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (l is null) return Results.NotFound();
            return Results.Ok(new
            {
                license = l,
                keys = (await db.IssuedKeys.AsNoTracking().Where(k => k.LicenseId == id).ToListAsync()).OrderByDescending(k => k.IssuedUtc),
                activations = (await db.Activations.AsNoTracking().Where(a => a.LicenseId == id).ToListAsync()).OrderByDescending(a => a.LastSeenUtc),
            });
        });

        admin.MapGet("/history", async (LicenseDb db) =>
            (await db.IssuedKeys.AsNoTracking().ToListAsync()).OrderByDescending(k => k.IssuedUtc).Take(200)
                .Select(k => new { k.Id, k.LicenseId, k.Reason, k.IssuedBy, k.Modules, k.Seats, k.LightSeats, k.IsTrial, k.ExpiresOn, k.IssuedUtc }));

        admin.MapGet("/revocations", async (LicenseDb db) =>
            (await db.Licenses.AsNoTracking().Where(l => l.RevokedUtc != null).ToListAsync()).Select(l => new { licenseId = l.Id, l.RevokedUtc, l.RevokedReason }));
    }

    private sealed record Reissued(Guid LicenseId, string Key, DateTime ExpiresOn);

    private static async Task<Reissued> ReissueAsync(Subscription sub, string reason, LicenseDb db, LicenseService svc)
    {
        var license = await svc.EnsureSubscriptionLicenseAsync(sub);
        var key = await svc.IssueAsync(license, reason, "admin");
        return new(license.Id, key.Key, key.ExpiresOn);
    }

    private static async ValueTask<object?> RequireAdminKey(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var configured = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value.ApiKey;
        if (string.IsNullOrEmpty(configured) || configured.Length < 24)
            return Results.Problem("The admin API is switched off: set Admin:ApiKey (at least 24 characters).", statusCode: 503);
        var h = ctx.HttpContext.Request.Headers;
        var given = h["X-Admin-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(given) && h.Authorization.FirstOrDefault() is { } auth && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            given = auth[7..].Trim();
        if (string.IsNullOrEmpty(given) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(configured))))
            return Results.Problem("Missing or wrong admin key.", statusCode: 401);
        return await next(ctx);
    }

    internal static async ValueTask<object?> MapErrors(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try { return await next(ctx); }
        catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 409); }
    }
}
