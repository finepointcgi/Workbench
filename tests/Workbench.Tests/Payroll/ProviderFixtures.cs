using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Workbench.Application;
using Workbench.Application.Payroll;
using Workbench.Domain.Payroll;

namespace Workbench.Tests.Payroll;

/// <summary>
/// Replays recorded provider API responses (Payroll/Fixtures/{provider}/*.json) and records every request, so the
/// adapters are tested against the providers' documented contracts without partner credentials or network calls.
/// </summary>
internal sealed class RecordedProvider : HttpMessageHandler, IPayrollHttpHandlerFactory
{
    private readonly List<(HttpMethod Method, string PathPrefix, Func<HttpResponseMessage> Respond)> _routes = [];

    public string Folder { get; }

    public List<RecordedRequest> Requests { get; } = [];

    public X509Certificate2? ClientCertificate { get; private set; }

    public RecordedProvider(string folder) => Folder = folder;

    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, string? Authorization, IReadOnlyDictionary<string, string> Headers)
    {
        public JsonNode? Json => Body is { Length: > 0 } b && (b.StartsWith('{') || b.StartsWith('[')) ? JsonNode.Parse(b) : null;

        public string PathAndQuery => Uri.PathAndQuery;
    }

    /// <summary>Answers <paramref name="method"/> requests whose path and query start with <paramref name="pathPrefix"/> with a fixture file.</summary>
    public RecordedProvider On(HttpMethod method, string pathPrefix, string fixture, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((method, pathPrefix, () => new HttpResponseMessage(status)
        {
            Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Payroll", "Fixtures", Folder, fixture)), Encoding.UTF8, "application/json"),
        }));
        return this;
    }

    public RecordedProvider OnStatus(HttpMethod method, string pathPrefix, HttpStatusCode status, string body = "")
    {
        _routes.Add((method, pathPrefix, () => new HttpResponseMessage(status) { Content = new StringContent(body) }));
        return this;
    }

    public HttpMessageHandler Create(string providerKey, X509Certificate2? clientCertificate)
    {
        ClientCertificate = clientCertificate;
        return this;
    }

    public IEnumerable<RecordedRequest> To(HttpMethod method, string pathPrefix) =>
        Requests.Where(r => r.Method == method && r.PathAndQuery.StartsWith(pathPrefix, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString(),
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
        var path = request.RequestUri!.PathAndQuery;
        var route = _routes.LastOrDefault(r => r.Method == request.Method && path.StartsWith(r.PathPrefix, StringComparison.Ordinal));
        return route.Respond is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"No fixture for {request.Method} {path}") }
            : route.Respond();
    }
}

/// <summary>A payroll connection store over a test database with throwaway Data Protection keys.</summary>
internal static class TestConnections
{
    public static async Task<(PayrollConnections Store, IWorkbenchDbContextFactory Factory)> CreateAsync(string providerKey, IReadOnlyDictionary<string, string>? values, string environment = "production")
    {
        var factory = TestDb.CreateFactory();
        var store = new PayrollConnections(factory, new EphemeralDataProtectionProvider(), TimeProvider.System);
        if (values is not null)
        {
            await using var db = await factory.CreateAsync();
            db.Set<PayrollProviderConnection>().Add(new PayrollProviderConnection
            {
                ProviderKey = providerKey,
                Environment = environment,
                ProtectedCredentials = store.Protect(values),
                ConnectedBy = "finance@example.com",
                ConnectedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        return (store, factory);
    }
}
