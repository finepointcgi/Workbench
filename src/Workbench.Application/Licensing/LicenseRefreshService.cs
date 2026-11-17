using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Contracts;
using Workbench.Contracts.Licensing;
using Workbench.Domain.Licensing;

namespace Workbench.Application.Licensing;

/// <summary>What the license server answers on <c>/v1/activate</c> and <c>/v1/refresh</c> (a subset of its response; unknown fields are ignored).</summary>
internal sealed record ServerLicenseResponse(string? Key, string[]? Warnings);

/// <summary>The server's refusal body: <c>{ "reason": "subscription_lapsed", "message": "..." }</c>.</summary>
internal sealed record ServerRefusal(string? Reason, string? Message);

/// <summary>
/// The connected-install side of the license server: activates once, then refreshes the key on a schedule. Offline installs
/// (no <see cref="LicenseOptions.ServerUrl"/>) never reach this code's network calls. A refreshed key is stored only after it
/// passes the same offline signature check as a key typed by hand, so the server's answer is never trusted blindly; on any
/// problem the key in use is kept, and the key's own expiry (and grace period) keeps governing entitlements.
/// </summary>
public sealed class LicenseRefreshService(
    IWorkbenchDbContextFactory dbFactory,
    EditionService editions,
    LicenseOptions options,
    IHttpClientFactory http,
    TimeProvider clock,
    ILogger<LicenseRefreshService> logger)
{
    public const string HttpClientName = "workbench-license-server";

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(5);

    public bool Enabled => !string.IsNullOrWhiteSpace(options.ServerUrl);

    /// <summary>The wait after <paramref name="failures"/> failed attempts in a row: 5 minutes, doubling, never longer than the normal interval.</summary>
    public static TimeSpan Backoff(int failures, TimeSpan interval)
    {
        if (failures <= 0)
        {
            return interval;
        }

        var wait = FirstBackoff * Math.Pow(2, Math.Min(failures - 1, 20));
        return wait < interval ? wait : interval;
    }

    private TimeSpan Interval => TimeSpan.FromHours(Math.Max(1, options.RefreshIntervalHours));

    /// <summary>
    /// Refreshes when an attempt is due (at startup after a restart too, but never sooner than the backoff since the last
    /// attempt), and returns how long to wait before looking again. Never throws for a network or server problem.
    /// </summary>
    public async Task<TimeSpan> RunIfDueAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return Interval;
        }

        LicenseSettings? row;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            row = await db.Set<LicenseSettings>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        }

        if (row is { LastRefreshAttemptUtc: { } last })
        {
            var wait = row.ConsecutiveRefreshFailures > 0 ? Backoff(row.ConsecutiveRefreshFailures, Interval) : Interval;
            var remaining = last + wait - clock.GetUtcNow().UtcDateTime;
            if (remaining > TimeSpan.Zero)
            {
                return remaining;
            }
        }

        await RefreshAsync(ct);
        await using var after = await dbFactory.CreateAsync(ct);
        var failures = await after.Set<LicenseSettings>().AsNoTracking().OrderBy(s => s.Id).Select(s => s.ConsecutiveRefreshFailures).FirstOrDefaultAsync(ct);
        return Backoff(failures, Interval);
    }

    /// <summary>One attempt: activate (first time, or for a different license) or refresh. Records the result for Setup &gt; License.</summary>
    public async Task<LicenseRefreshDto?> RefreshAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return null;
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var row = await db.Set<LicenseSettings>().OrderBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (row is null || string.IsNullOrWhiteSpace(row.Key))
        {
            return null; // No key, nothing to refresh: a connected install still starts from a key it was given.
        }

        if (!LicenseKeys.TryVerify(row.Key, options.PublicKeyPem, out var current, out _) || current?.LicenseId is not { } licenseId)
        {
            var offline = Record(row, RefreshOutcomeNames.Offline, "The key in use is not a connected-license key (WB2), so there is nothing to refresh. It runs until it expires.", failed: false);
            await db.SaveChangesAsync(ct);
            return offline;
        }

        row.InstallId ??= Guid.NewGuid().ToString("N");
        row.LastRefreshAttemptUtc = clock.GetUtcNow().UtcDateTime;

        var state = await editions.GetAsync(ct);
        var usage = await editions.UsageAsync(db, state, includePending: false, ct);
        var body = new { licenseId, installId = row.InstallId, seatsInUse = usage.FullSeatsInUse, lightSeatsInUse = usage.LightSeatsInUse, version = AppVersion };

        try
        {
            var activate = row.ServerLicenseId != licenseId;
            var (status, text) = await PostAsync(activate ? "v1/activate" : "v1/refresh", body, ct);
            if (!activate && status == HttpStatusCode.NotFound && Reason(text) == "not_activated")
            {
                (status, text) = await PostAsync("v1/activate", body, ct);
                activate = true;
            }

            if (status == HttpStatusCode.OK)
            {
                return await AcceptAsync(db, row, current, text, activate, ct);
            }

            return Handle(row, status, text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning("License refresh could not reach the license server: {Reason}", ex.Message);
            return Record(row, RefreshOutcomeNames.Failed, "Could not reach the license server. Nothing changed: the key in use keeps working until it expires.", failed: true);
        }
        finally
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task<LicenseRefreshDto?> AcceptAsync(IWorkbenchDbContext db, LicenseSettings row, LicenseClaims current, string text, bool activated, CancellationToken ct)
    {
        ServerLicenseResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<ServerLicenseResponse>(text, Json);
        }
        catch (JsonException)
        {
            response = null;
        }

        // Never trust the answer: the key must verify offline against the built-in public key, be for this very license,
        // and not be older than the one in use (a replayed old response).
        LicenseClaims? fresh = null;
        string? problem = null;
        var key = response?.Key;
        if (key is null || !LicenseKeys.TryVerify(key, options.PublicKeyPem, out fresh, out problem) || fresh is null)
        {
            return Record(row, RefreshOutcomeNames.Failed, "The license server's answer was not a valid license key, so it was ignored. " + (problem ?? "").Trim(), failed: true);
        }

        if (fresh.LicenseId != current.LicenseId)
        {
            return Record(row, RefreshOutcomeNames.Failed, "The license server sent a key for a different license, so it was ignored.", failed: true);
        }

        if (fresh.IssuedOn is { } issued && current.IssuedOn is { } had && issued < had)
        {
            return Record(row, RefreshOutcomeNames.Failed, "The license server sent an older key than the one in use, so it was ignored.", failed: true);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        // The same store a manually entered key uses. The seat check at entry is skipped on purpose: the server already told us
        // about any over-use (the warnings), and a refresh must never leave an install without its key.
        (row.Key, row.ActivatedAtUtc, row.ActivatedBy) = (key.Trim(), now, "License server");
        row.ServerLicenseId = fresh.LicenseId;
        row.LastRefreshSuccessUtc = now;
        row.ConsecutiveRefreshFailures = 0;
        row.LastRefreshOutcome = RefreshOutcomeNames.Refreshed;
        var warnings = response!.Warnings is { Length: > 0 } w ? " " + string.Join(" ", w) : "";
        row.LastRefreshMessage = Trim($"{(activated ? "Activated" : "Refreshed")}; the key now runs to {fresh.ExpiresOn:d MMM yyyy}.{warnings}");
        return ToDto(row);
    }

    private LicenseRefreshDto? Handle(LicenseSettings row, HttpStatusCode status, string text)
    {
        var reason = Reason(text);
        var message = Message(text);
        switch ((int)status)
        {
            case 409 when reason == "offline_license":
                return Record(row, RefreshOutcomeNames.Offline, "This is an offline key; the license server does not refresh it. It runs until it expires.", failed: false);
            case 402 or 403 or 404 or 409:
                // Answered, and not entitled: the reason is shown as the server gave it. The key in use is kept, so the grace period
                // (14 days, then Community) follows the key's own expiry. Keep asking on the normal schedule in case it is renewed.
                return Record(row, RefreshOutcomeNames.Refused, message ?? $"The license server refused the refresh ({reason ?? ((int)status).ToString()}).", failed: false);
            default: // 429, 5xx, anything unexpected: the server is the one with the problem.
                return Record(row, RefreshOutcomeNames.Failed, $"The license server answered {(int)status}. It will be tried again; nothing changed.", failed: true);
        }
    }

    private LicenseRefreshDto Record(LicenseSettings row, string outcome, string message, bool failed)
    {
        row.LastRefreshAttemptUtc = clock.GetUtcNow().UtcDateTime;
        row.LastRefreshOutcome = outcome;
        row.LastRefreshMessage = Trim(message);
        row.ConsecutiveRefreshFailures = failed ? row.ConsecutiveRefreshFailures + 1 : 0;
        return ToDto(row);
    }

    /// <summary>The refresh state for the license page and the administrator banner; null when this install is offline.</summary>
    public LicenseRefreshDto? Describe(LicenseSettings? row)
    {
        if (!Enabled)
        {
            return null;
        }

        return row is null
            ? new LicenseRefreshDto(options.ServerUrl!, null, null, null, null, 0, false, null)
            : ToDto(row);
    }

    private LicenseRefreshDto ToDto(LicenseSettings row)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var since = row.LastRefreshSuccessUtc ?? row.ActivatedAtUtc;
        int? days = row.LastRefreshSuccessUtc is { } ok ? (int)(now - ok).TotalDays : null;
        var stale = row.LastRefreshOutcome is RefreshOutcomeNames.Failed or RefreshOutcomeNames.Refused
            && since is { } from && (now - from).TotalDays >= Math.Max(1, options.RefreshStaleDays);
        return new LicenseRefreshDto(options.ServerUrl ?? "", row.LastRefreshAttemptUtc, row.LastRefreshSuccessUtc, row.LastRefreshOutcome, row.LastRefreshMessage, row.ConsecutiveRefreshFailures, stale, days);
    }

    private async Task<(HttpStatusCode Status, string Text)> PostAsync(string path, object body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.RefreshTimeoutSeconds)));
        var client = http.CreateClient(HttpClientName);
        var baseUrl = options.ServerUrl!.Trim().TrimEnd('/') + "/";
        using var response = await client.PostAsJsonAsync(new Uri(new Uri(baseUrl), path), body, Json, timeout.Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string? Reason(string text) => Refusal(text)?.Reason;

    private static string? Message(string text) => Refusal(text)?.Message;

    private static ServerRefusal? Refusal(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<ServerRefusal>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Trim(string s) => s.Length <= 500 ? s : s[..497] + "...";

    private static string AppVersion => typeof(LicenseRefreshService).Assembly.GetName().Version?.ToString(3) ?? "";
}
