using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.LicenseServer.Data;
using Workbench.LicenseServer.Licensing;

namespace Workbench.LicenseServer.Endpoints;

public sealed record ConnectedRequest(Guid LicenseId, string InstallId, int SeatsInUse = 0, int LightSeatsInUse = 0, string? Version = null);

/// <summary>What a connected install gets back: a fresh short-lived key and when to ask again.</summary>
public sealed record ConnectedResponse(string Key, DateOnly Expires, DateOnly RefreshAfter, string[] Modules, int Seats, int LightSeats, bool Trial, string[] Warnings);

public static class ConnectedEndpoints
{
    public static void MapConnectedEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1").RequireRateLimiting("connected").AddEndpointFilter(AdminEndpoints.MapErrors);

        v1.MapPost("/activate", (ConnectedRequest r, LicenseDb db, LicenseService svc, IOptions<KeyOptions> keys, TimeProvider clock) =>
            HandleAsync(r, activate: true, db, svc, keys.Value, clock));
        v1.MapPost("/refresh", (ConnectedRequest r, LicenseDb db, LicenseService svc, IOptions<KeyOptions> keys, TimeProvider clock) =>
            HandleAsync(r, activate: false, db, svc, keys.Value, clock));

        // The revocation list, so an install can check without a refresh. Ids only; nothing identifying.
        app.MapGet("/v1/revocations", async (LicenseDb db) =>
            (await db.Licenses.AsNoTracking().Where(l => l.RevokedUtc != null).ToListAsync()).Select(l => l.Id)).RequireRateLimiting("connected");

        app.MapGet("/v1/public-key", (ILicenseSigner s) => Results.Text(s.PublicKeyPem, "application/x-pem-file"));
    }

    private static async Task<IResult> HandleAsync(ConnectedRequest r, bool activate, LicenseDb db, LicenseService svc, KeyOptions keys, TimeProvider clock)
    {
        if (r.LicenseId == Guid.Empty) throw new ArgumentException("licenseId is required.");
        var install = (r.InstallId ?? "").Trim();
        if (install.Length is 0 or > 100) throw new ArgumentException("installId is required (at most 100 characters).");
        if (r.SeatsInUse < 0 || r.LightSeatsInUse < 0) throw new ArgumentException("Seat counts can't be negative.");

        var license = await db.Licenses.FirstOrDefaultAsync(l => l.Id == r.LicenseId);
        if (license is null) return Results.Json(new { reason = "unknown_license", message = "No such license." }, statusCode: 404);
        if (license.Kind != LicenseKind.Subscription)
            return Results.Json(new { reason = "offline_license", message = "This is an offline key; it does not connect. Enter the key you were given." }, statusCode: 409);

        var e = await svc.EntitlementAsync(license, forConnected: true);
        if (e.Denied is { } d) return Results.Json(new { reason = d.Reason, message = d.Message }, statusCode: d.StatusCode);

        var now = clock.GetUtcNow().UtcDateTime;
        var act = await db.Activations.FirstOrDefaultAsync(a => a.LicenseId == license.Id && a.InstallId == install);
        if (act is null)
        {
            if (!activate) return Results.Json(new { reason = "not_activated", message = "This install has not activated. Call /v1/activate first." }, statusCode: 404);
            if (await db.Activations.CountAsync(a => a.LicenseId == license.Id) >= keys.MaxInstalls)
                return Results.Json(new { reason = "too_many_installs", message = $"This license is already activated on {keys.MaxInstalls} installs. Ask support to release one." }, statusCode: 409);
            act = new Activation { LicenseId = license.Id, InstallId = install, FirstSeenUtc = now };
            db.Activations.Add(act);
        }

        act.LastSeenUtc = now;
        act.SeatsReported = r.SeatsInUse;
        act.LightSeatsReported = r.LightSeatsInUse;
        if (!string.IsNullOrWhiteSpace(r.Version)) act.Version = r.Version.Length > 50 ? r.Version[..50] : r.Version;
        act.LastKeyExpiresOn = e.Expires.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var warnings = new List<string>();
        if (r.SeatsInUse > e.Seats) warnings.Add($"{r.SeatsInUse} full seats are in use but the license covers {e.Seats}.");
        if (r.LightSeatsInUse > e.LightSeats) warnings.Add($"{r.LightSeatsInUse} light seats are in use but the license covers {e.LightSeats}.");
        var refreshAfter = svc.Today.AddDays(Math.Max(1, keys.RefreshDays / 2));
        return Results.Ok(new ConnectedResponse(svc.Sign(license, e), e.Expires, refreshAfter < e.Expires ? refreshAfter : e.Expires, e.Modules, e.Seats, e.LightSeats, e.Trial, [.. warnings]));
    }
}
