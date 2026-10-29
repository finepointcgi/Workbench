using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Workbench.Application.Security;
using Workbench.Contracts.Security;
using Workbench.Web.Identity;

namespace Workbench.Web.Security;

/// <summary>
/// Rate limits (A.8.20): sign-in endpoints and SCIM per client IP, the REST API per user. Static files, the Blazor
/// circuit and hubs aren't limited. Limits come from <see cref="RateLimitSettings"/>.
/// </summary>
public static class RateLimiting
{
    public static IServiceCollection AddWorkbenchRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(RateLimitSettings.SectionName).Get<RateLimitSettings>() ?? new RateLimitSettings();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                // A body keeps the status-code pages from turning the 429 into a sign-in redirect.
                context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                await context.HttpContext.Response.WriteAsync("Too many requests. Try again in a minute.", cancellationToken);

                var log = context.HttpContext.RequestServices.GetRequiredService<SecurityEventLog>();
                await log.RecordAsync(SecurityEventTypes.RateLimited, context.HttpContext.Request.Path, SecurityOutcomes.Denied,
                    actor: ClaimsUser.EmailOf(context.HttpContext.User) ?? "anonymous", cancellationToken: CancellationToken.None);
            };
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                if (!settings.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("off");
                }

                var path = http.Request.Path;
                var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (path.StartsWithSegments("/account") || path.StartsWithSegments("/signin-oidc") || path.StartsWithSegments("/signout-callback-oidc") || path.StartsWithSegments("/signin-sso") || path.StartsWithSegments("/supplier/signin"))
                {
                    return Window($"signin:{ip}", settings.SignInPerMinute);
                }

                // The customer's link: generous for a person reloading, tight for anyone trying addresses.
                if (path.StartsWithSegments("/c"))
                {
                    return Window($"customer:{ip}", settings.SignInPerMinute);
                }

                if (path.StartsWithSegments(ScimEndpoints.BasePath))
                {
                    return Window($"scim:{ip}", settings.ScimPerMinute);
                }

                if (path.StartsWithSegments("/api"))
                {
                    return Window($"api:{ClaimsUser.EmailOf(http.User) ?? ip}", settings.ApiPerMinute);
                }

                return RateLimitPartition.GetNoLimiter("unlimited");
            });
        });
        return services;
    }

    private static RateLimitPartition<string> Window(string key, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, permitsPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
}

/// <summary>
/// Response headers (A.8.20–8.23, A.8.26). The Content-Security-Policy allows only this origin's scripts (Blazor and
/// MudBlazor load as files; nothing inline), the circuit's WebSocket back to this origin, and inline styles, which
/// MudBlazor sets as style attributes. No frames, no plugins, no form posts elsewhere.
/// </summary>
public static class SecurityHeaders
{
    public static string ContentSecurityPolicy(IEnumerable<string>? extraConnectSources = null)
    {
        var connect = string.Join(' ', new[] { "'self'" }.Concat(extraConnectSources ?? []));
        return string.Join("; ",
            "default-src 'self'",
            "script-src 'self'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data: blob:",
            "font-src 'self' data:",
            $"connect-src {connect}",
            "media-src 'self' blob:",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'",
            "frame-src 'none'",
            "worker-src 'self' blob:",
            "manifest-src 'self'",
            "upgrade-insecure-requests");
    }

    public const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), fullscreen=(self), geolocation=(), gyroscope=(), " +
        "magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), publickey-credentials-get=(self), screen-wake-lock=(), " +
        "sync-xhr=(), usb=(), xr-spatial-tracking=(), clipboard-write=(self)";

    public static IApplicationBuilder UseWorkbenchSecurityHeaders(this IApplicationBuilder app, HostSecuritySettings settings, bool development)
    {
        var csp = ContentSecurityPolicy(settings.ConnectSources);

        // Plain-HTTP development would otherwise be told to upgrade every request to HTTPS.
        if (development)
        {
            csp = csp.Replace("; upgrade-insecure-requests", "", StringComparison.Ordinal);
        }

        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers.XFrameOptions = "DENY";
                headers["Permissions-Policy"] = PermissionsPolicy;
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
                headers["X-Permitted-Cross-Domain-Policies"] = "none";
                if (!headers.ContainsKey("Content-Security-Policy"))
                {
                    headers.ContentSecurityPolicy = csp;
                }

                // Authenticated responses (pages, API, CSV exports) must not be cached by shared caches.
                if (!context.Request.Path.StartsWithSegments("/_framework") && !context.Request.Path.StartsWithSegments("/_content")
                    && !headers.ContainsKey("Cache-Control") && context.User.Identity?.IsAuthenticated == true)
                {
                    headers.CacheControl = "no-store";
                }

                return Task.CompletedTask;
            });
            await next();
        });
    }

    /// <summary>Records 403 responses (API refusals) in the security event log, unless the service already did.</summary>
    public static IApplicationBuilder UseWorkbenchDenialLogging(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            await next();
            if (context.Response.StatusCode == StatusCodes.Status403Forbidden && !context.Items.ContainsKey(HttpSecurityEventContext.DenialRecordedKey))
            {
                var log = context.RequestServices.GetRequiredService<SecurityEventLog>();
                await log.RecordAsync(SecurityEventTypes.PermissionDenied, $"{context.Request.Method} {context.Request.Path}", SecurityOutcomes.Denied,
                    actor: ClaimsUser.EmailOf(context.User) ?? "anonymous");
            }
        });

    /// <summary>Behind a TLS-terminating proxy: trust X-Forwarded-For/Proto so the scheme, HSTS and client IPs are right.</summary>
    public static IServiceCollection AddWorkbenchForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.ForwardLimit = 1;

            // KnownProxies/KnownNetworks: configure your proxy's address (ForwardedHeaders:KnownProxies) to restrict which hops are trusted.
            foreach (var proxy in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
            {
                if (System.Net.IPAddress.TryParse(proxy, out var address))
                {
                    o.KnownProxies.Add(address);
                }
            }

            // ForwardedHeaders:KnownNetworks: CIDR ranges of proxies whose addresses change (a container network, a
            // Kubernetes pod range), e.g. 172.28.0.0/24.
            foreach (var network in configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
            {
                if (System.Net.IPNetwork.TryParse(network, out var parsed))
                {
                    o.KnownIPNetworks.Add(parsed);
                }
            }
        });
        return services;
    }
}
