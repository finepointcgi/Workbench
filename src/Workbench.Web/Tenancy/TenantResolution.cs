using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Web.Tenancy;

/// <summary>
/// MultiTenant request routing (ISO 27001 A.8.3): the tenant comes from the subdomain (acme.workbench.example → acme),
/// or in Development only from an <c>X-Tenant</c> header. Unknown, provisioning or failed tenants get 404 and suspended
/// ones 403, before authentication runs. The base domain itself is the host admin site, which only serves
/// <c>/host</c>, sign-in, health and static files. SingleTenant mode does nothing here.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, TenancyOptions options, IHostEnvironment environment, TenantCatalog catalog)
{
    public const string TenantHeader = "X-Tenant";
    private static readonly object TenantKey = new();
    private static readonly object HostKey = new();

    public static TenantInfo? TenantOf(HttpContext? context) => context?.Items[TenantKey] as TenantInfo;

    /// <summary>True for a request to the host admin site (the base domain, no tenant).</summary>
    public static bool IsHostSite(HttpContext? context) => context?.Items.ContainsKey(HostKey) == true;

    public async Task InvokeAsync(HttpContext context)
    {
        var slug = SlugFromHost(context.Request.Host.Host, options.BaseDomain);
        if (environment.IsDevelopment() && options.AllowTenantHeader && context.Request.Headers[TenantHeader].ToString() is { Length: > 0 } header)
        {
            slug = header.Trim().ToLowerInvariant();
        }

        if (slug is null)
        {
            await HostSiteAsync(context);
            return;
        }

        if (IsHostOnlyPath(context.Request.Path))
        {
            await PlainAsync(context, StatusCodes.Status404NotFound, "Not found.");
            return;
        }

        var entry = slug.Length == 0 ? null : await catalog.FindAsync(slug, context.RequestAborted);
        if (entry is null || entry.Status is TenantStatus.Provisioning or TenantStatus.Failed)
        {
            await PlainAsync(context, StatusCodes.Status404NotFound, "There's no Workbench organization at this address.");
            return;
        }

        if (entry.Status == TenantStatus.Suspended)
        {
            await PlainAsync(context, StatusCodes.Status403Forbidden, "This Workbench organization is suspended. Contact your Workbench provider.");
            return;
        }

        context.Items[TenantKey] = entry.Info;
        using (TenantScope.Enter(entry.Info))
        {
            await next(context);
        }
    }

    /// <summary>
    /// The tenant slug for <paramref name="host"/>: null for the base domain itself (the host site), "" for a host that
    /// isn't a single-label subdomain of it, else the subdomain.
    /// </summary>
    public static string? SlugFromHost(string host, string baseDomain)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        baseDomain = baseDomain.Trim().TrimEnd('.').ToLowerInvariant();
        if (host == baseDomain)
        {
            return null;
        }

        if (!host.EndsWith("." + baseDomain, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var label = host[..^(baseDomain.Length + 1)];
        return label.Length == 0 || label.Contains('.') ? string.Empty : label;
    }

    private async Task HostSiteAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path == "/")
        {
            context.Response.Redirect("/host");
            return;
        }

        if (IsHostOnlyPath(path) || path.StartsWithSegments("/account") || path.StartsWithSegments("/health")
            || path.StartsWithSegments("/_blazor") || path.StartsWithSegments("/_framework") || path.StartsWithSegments("/_content")
            || path.StartsWithSegments("/not-found") || path.StartsWithSegments("/signin-oidc") || path.StartsWithSegments("/signout-callback-oidc")
            || path.StartsWithSegments("/webhooks/email")
            || Path.HasExtension(path.Value))
        {
            context.Items[HostKey] = true;
            using (TenantScope.Enter(null))
            {
                await next(context);
            }

            return;
        }

        await PlainAsync(context, StatusCodes.Status404NotFound, "Open your organization's address (for example acme." + options.BaseDomain + ").");
    }

    private static bool IsHostOnlyPath(PathString path) => path.StartsWithSegments("/host") || path.StartsWithSegments("/api/v1/host");

    private static Task PlainAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message, context.RequestAborted);
    }
}

/// <summary>
/// Pins a Blazor circuit to the tenant it started in, and runs every inbound circuit activity (events, JS interop,
/// navigation) in that tenant. A circuit whose tenant is suspended stops working on its next activity.
/// </summary>
internal sealed class TenantCircuitHandler(IHttpContextAccessor http, TenantCatalog catalog) : CircuitHandler
{
    private bool _pinned;
    private TenantInfo? _tenant;

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            var arriving = TenantScope.Current ?? TenantResolutionMiddleware.TenantOf(http.HttpContext);
            if (!_pinned)
            {
                _tenant = arriving;
                _pinned = true;
            }
            else if (arriving is not null && arriving.Id != _tenant?.Id)
            {
                throw new InvalidOperationException("This circuit belongs to another tenant.");
            }

            if (_tenant is not null && (await catalog.FindAsync(_tenant.Slug))?.Status != TenantStatus.Active)
            {
                throw new InvalidOperationException("This organization isn't active.");
            }

            using (TenantScope.Enter(_tenant))
            {
                await next(context);
            }
        };
}

/// <summary>Runs each SignalR hub call and connection event in the tenant of the connection's request.</summary>
internal sealed class TenantHubFilter : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        using (TenantScope.Enter(TenantResolutionMiddleware.TenantOf(invocationContext.Context.GetHttpContext())))
        {
            return await next(invocationContext);
        }
    }

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        using (TenantScope.Enter(TenantResolutionMiddleware.TenantOf(context.Context.GetHttpContext())))
        {
            await next(context);
        }
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        using (TenantScope.Enter(TenantResolutionMiddleware.TenantOf(context.Context.GetHttpContext())))
        {
            await next(context, exception);
        }
    }
}

public static class TenancyHosting
{
    /// <summary>MultiTenant only: circuit pinning, the hub filter and tenant-scoped Data Protection. Call after AddWorkbenchSecurity.</summary>
    public static WebApplicationBuilder AddWorkbenchTenancyHosting(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(TenancyOptions.SectionName).Get<TenancyOptions>() ?? new TenancyOptions();
        if (!options.IsMultiTenant)
        {
            return builder;
        }

        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, TenantCircuitHandler>());
        builder.Services.AddSignalR(o => o.AddFilter<TenantHubFilter>());
        builder.Services.AddSingleton<TenantHubFilter>();
        TenantDataProtectionProvider.Decorate(builder.Services);
        return builder;
    }

    /// <summary>Resolves the tenant; call right after the security headers, before authentication.</summary>
    public static WebApplication UseWorkbenchTenancy(this WebApplication app)
    {
        if (app.Services.GetRequiredService<TenancyOptions>().IsMultiTenant)
        {
            app.UseMiddleware<TenantResolutionMiddleware>();
        }

        return app;
    }
}
