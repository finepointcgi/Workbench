namespace Workbench.ApiClient;

/// <summary>
/// Where the desktop app connects. In MultiTenant (SaaS) mode each organization has its own address, which carries the
/// tenant for the API and the SignalR hubs alike: <c>WORKBENCH_TENANT_URL</c> (e.g. https://acme.workbench.example/), or
/// <c>WORKBENCH_SERVER_URL</c> plus <c>WORKBENCH_TENANT</c> (the tenant id becomes the subdomain). A self-hosted server
/// only needs <c>WORKBENCH_SERVER_URL</c>.
/// </summary>
public static class WorkbenchServerAddress
{
    public static Uri Resolve(string? tenantUrl, string? serverUrl, string? tenant, string defaultServerUrl)
    {
        if (!string.IsNullOrWhiteSpace(tenantUrl))
        {
            return WithSlash(new Uri(tenantUrl.Trim(), UriKind.Absolute));
        }

        var server = WithSlash(new Uri(string.IsNullOrWhiteSpace(serverUrl) ? defaultServerUrl : serverUrl.Trim(), UriKind.Absolute));
        if (string.IsNullOrWhiteSpace(tenant))
        {
            return server;
        }

        var slug = tenant.Trim().ToLowerInvariant();
        if (slug.Contains('.') || Uri.CheckHostName($"{slug}.{server.Host}") != UriHostNameType.Dns)
        {
            throw new ArgumentException($"'{tenant}' isn't a valid tenant id.", nameof(tenant));
        }

        return new UriBuilder(server) { Host = $"{slug}.{server.Host}" }.Uri;
    }

    public static Uri FromEnvironment(string defaultServerUrl) => Resolve(
        Environment.GetEnvironmentVariable("WORKBENCH_TENANT_URL"),
        Environment.GetEnvironmentVariable("WORKBENCH_SERVER_URL"),
        Environment.GetEnvironmentVariable("WORKBENCH_TENANT"),
        defaultServerUrl);

    private static Uri WithSlash(Uri uri) => uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri + "/");
}
