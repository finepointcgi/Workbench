using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Security;

namespace Workbench.Web.Security;

/// <summary>The web host's ISO 27001 product controls. See docs/security and docs/iso for what each one covers.</summary>
public static class WebSecuritySetup
{
    /// <summary>Call after <c>AddWorkbenchAuthentication</c>. Refuses a weak configuration outside Development.</summary>
    public static WebApplicationBuilder AddWorkbenchSecurity(this WebApplicationBuilder builder)
    {
        ProductionConfigurationCheck.Enforce(builder.Configuration, builder.Environment);

        var services = builder.Services;
        var configuration = builder.Configuration;
        var host = configuration.GetSection(HostSecuritySettings.SectionName).Get<HostSecuritySettings>() ?? new HostSecuritySettings();
        services.AddSingleton(host);
        services.Replace(ServiceDescriptor.Singleton(configuration.GetSection(RetentionOptions.SectionName).Get<RetentionOptions>() ?? new RetentionOptions()));

        services.AddHsts(o =>
        {
            o.MaxAge = TimeSpan.FromDays(Math.Max(1, host.HstsMaxAgeDays));
            o.IncludeSubDomains = true;
        });
        services.AddWorkbenchForwardedHeaders(configuration);
        services.AddWorkbenchRateLimiting(configuration);
        services.AddWorkbenchScim();
        services.AddWorkbenchDataProtection(configuration, builder.Environment);
        services.AddWorkbenchHealthChecks();
        services.AddWorkbenchTelemetry(configuration);
        services.AddWorkbenchJobInstrumentation();
        services.AddHostedService<RetentionBackgroundService>();
        return builder;
    }

    /// <summary>Headers and forwarded headers; call first in the pipeline.</summary>
    public static WebApplication UseWorkbenchSecurityHeaders(this WebApplication app)
    {
        var host = app.Services.GetRequiredService<HostSecuritySettings>();
        if (host.TlsTerminatedByProxy)
        {
            app.UseForwardedHeaders();
        }

        app.UseWorkbenchSecurityHeaders(host, app.Environment.IsDevelopment());
        return app;
    }
}
