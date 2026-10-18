using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Workbench.Application.Identity;
using Workbench.Application.Security;
using Workbench.Contracts.Security;
using Workbench.Web.Identity;

namespace Workbench.Web.Security;

/// <summary>
/// Session rules (A.8.5): a session ends after the idle timeout (sliding cookie), the absolute lifetime after sign-in,
/// when the user is deactivated, or when an admin signs them out everywhere. Checked on every request and, for Blazor
/// circuits, periodically and on reconnect.
/// </summary>
public static class SessionValidation
{
    /// <summary>When the user signed in (Unix milliseconds). Survives the cookie's sliding renewals, unlike its issue time.</summary>
    public const string SignedInAtClaim = "wb_signed_in_at";

    public static Claim SignedInAt(DateTimeOffset now) =>
        new(SignedInAtClaim, now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64);

    /// <summary>The sign-in time from our claim, else a JWT's <c>iat</c> (seconds), else null.</summary>
    public static DateTime? SignedInAtUtc(ClaimsPrincipal? principal)
    {
        if (long.TryParse(principal?.FindFirst(SignedInAtClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }

        return long.TryParse(principal?.FindFirst("iat")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
    }

    /// <summary>Why the session is no longer valid, or null when it is.</summary>
    public static async Task<string?> CheckAsync(ClaimsPrincipal? principal, UserStatusCache cache, SessionSettings settings, TimeProvider clock, bool enforceLifetime, CancellationToken cancellationToken)
    {
        if (ClaimsUser.EmailOf(principal) is not { } email)
        {
            return null;
        }

        var signedInAt = SignedInAtUtc(principal);
        if (enforceLifetime && (signedInAt is null || clock.GetUtcNow().UtcDateTime - signedInAt.Value > settings.AbsoluteLifetime))
        {
            return "The session reached its maximum lifetime.";
        }

        var state = await cache.GetAsync(email, cancellationToken);
        if (!state.Exists)
        {
            return "The account no longer exists.";
        }

        if (!state.IsActive)
        {
            return "The account is deactivated.";
        }

        return state.Accepts(signedInAt) ? null : "An administrator ended this user's sessions.";
    }

    /// <summary>Cookie <c>OnValidatePrincipal</c>: rejects the cookie (and records why) when the session is no longer valid.</summary>
    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var reason = await CheckAsync(context.Principal, services.GetRequiredService<UserStatusCache>(), services.GetRequiredService<SessionSettings>(),
            services.GetRequiredService<TimeProvider>(), enforceLifetime: true, context.HttpContext.RequestAborted);
        if (reason is null)
        {
            return;
        }

        var email = ClaimsUser.EmailOf(context.Principal);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await services.GetRequiredService<SecurityEventLog>().RecordAsync(SecurityEventTypes.SessionRejected, email, SecurityOutcomes.Denied, reason, actor: email);
    }
}

/// <summary>
/// The Blazor circuit's authentication state, re-checked every <see cref="SessionSettings.CircuitRevalidationSeconds"/>
/// and whenever the circuit reconnects. A failed check signs the circuit out; <c>SessionGuard</c> then sends the
/// browser to sign in.
/// </summary>
internal sealed class RevalidatingAuthenticationStateProvider : ServerAuthenticationStateProvider, IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SessionSettings _settings;
    private readonly TimeProvider _clock;
    private readonly CircuitActivity _activity;
    private readonly CancellationTokenSource _stop = new();
    private bool _loopStarted;

    public RevalidatingAuthenticationStateProvider(IServiceScopeFactory scopes, SessionSettings settings, TimeProvider clock, CircuitActivity activity)
    {
        _scopes = scopes;
        _settings = settings;
        _clock = clock;
        _activity = activity;
        AuthenticationStateChanged += OnChanged;
    }

    /// <summary>Checks the session now; signs the circuit out when it's no longer valid. Returns whether it's valid.</summary>
    public async Task<bool> RevalidateAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetAuthenticationStateAsync();
        if (state.User.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var reason = await SessionValidation.CheckAsync(state.User, scope.ServiceProvider.GetRequiredService<UserStatusCache>(), _settings, _clock,
            enforceLifetime: SessionValidation.SignedInAtUtc(state.User) is not null, cancellationToken);

        // The cookie's idle timeout doesn't see activity inside the circuit, so the circuit enforces it too.
        if (reason is null && _clock.GetUtcNow().UtcDateTime - _activity.LastActivityUtc > _settings.IdleTimeout)
        {
            reason = "The session was idle too long.";
        }
        if (reason is null)
        {
            return true;
        }

        var email = ClaimsUser.EmailOf(state.User);
        await scope.ServiceProvider.GetRequiredService<SecurityEventLog>()
            .RecordAsync(SecurityEventTypes.SessionRejected, email, SecurityOutcomes.Denied, $"Circuit: {reason}", actor: email, cancellationToken: CancellationToken.None);
        SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        return false;
    }

    public void Dispose()
    {
        AuthenticationStateChanged -= OnChanged;
        _stop.Cancel();
        _stop.Dispose();
    }

    private void OnChanged(Task<AuthenticationState> state)
    {
        if (_loopStarted)
        {
            return;
        }

        _loopStarted = true;
        _ = LoopAsync(_stop.Token);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _settings.CircuitRevalidationSeconds)), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    if (!await RevalidateAsync(cancellationToken))
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A transient database error keeps the session; the next tick checks again.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>When the user last did something in this circuit (clicks, typing, navigation).</summary>
internal sealed class CircuitActivity(TimeProvider clock)
{
    public DateTime LastActivityUtc { get; private set; } = clock.GetUtcNow().UtcDateTime;

    public void Touch() => LastActivityUtc = clock.GetUtcNow().UtcDateTime;
}

/// <summary>Re-checks the session whenever a circuit's connection comes back, and tracks activity for the idle timeout (A.8.5).</summary>
internal sealed class SessionCircuitHandler(AuthenticationStateProvider provider, CircuitActivity activity) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        context =>
        {
            activity.Touch();
            return next(context);
        };

    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (provider is RevalidatingAuthenticationStateProvider revalidating)
        {
            await revalidating.RevalidateAsync(cancellationToken);
        }
    }
}
