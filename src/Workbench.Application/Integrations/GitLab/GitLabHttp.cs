using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;

namespace Workbench.Application.Integrations.GitLab;

/// <summary>The GitLab server addresses Workbench accepts and the REST paths under them.</summary>
public static class GitLabUrls
{
    public const string GitLabCom = "https://gitlab.com";

    /// <summary>
    /// The server's base address from what an admin typed: https only (http only where integrations allow it), no user name
    /// or password, and without a trailing slash, query or <c>/api/v4</c>. Keeps a path prefix (<c>https://host/gitlab</c>).
    /// </summary>
    public static Uri NormalizeBase(string? input, bool allowHttp)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Enter your GitLab address, such as https://gitlab.com or https://gitlab.example.com.");
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        var uri = OutboundUrlGuard.ParseShape(text, "The GitLab address", allowHttp);
        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in new[] { "/api/v4", "/api" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
            }
        }

        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, path);
        return new Uri(builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
    }

    public static bool IsGitLabCom(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) && string.Equals(u.Host, "gitlab.com", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>{base}/api/v4/{path}</c>.</summary>
    public static Uri Api(string baseUrl, string path) => new($"{baseUrl.TrimEnd('/')}/api/v4/{path.TrimStart('/')}");

    /// <summary>A path segment for a project or group given by path (<c>group/sub/project</c> becomes <c>group%2Fsub%2Fproject</c>).</summary>
    public static string Id(string idOrPath) => Uri.EscapeDataString(idOrPath.Trim().Trim('/'));

    /// <summary>
    /// What an admin pasted to name a GitLab project: its numeric id, its path, or its web address on this server
    /// (<c>https://gitlab.com/group/project</c>, with or without <c>/-/issues</c>). Returns the id or path to look up.
    /// </summary>
    public static string ProjectRef(string baseUrl, string input)
    {
        var text = input.Trim();
        if (long.TryParse(text, out _))
        {
            return text;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var basePath = new Uri(baseUrl).AbsolutePath.TrimEnd('/');
            var path = uri.AbsolutePath;
            if (basePath.Length > 0 && path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
            {
                path = path[basePath.Length..];
            }

            text = path;
        }

        var cut = text.IndexOf("/-/", StringComparison.Ordinal);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        text = text.Trim('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        if (text.Length == 0)
        {
            throw new ArgumentException("Enter the GitLab project's path (group/project), its web address or its id.");
        }

        return text;
    }
}

/// <summary>Private certificate authorities for self-managed servers whose HTTPS certificate isn't publicly trusted.</summary>
public static class GitLabTls
{
    /// <summary>The certificates in a PEM block. Throws <see cref="ArgumentException"/> when there are none or one can't be read.</summary>
    public static X509Certificate2Collection ParseCa(string pem)
    {
        var collection = new X509Certificate2Collection();
        try
        {
            collection.ImportFromPem(pem);
        }
        catch (CryptographicException)
        {
            throw new ArgumentException("The certificate authority couldn't be read. Paste the PEM text, starting with -----BEGIN CERTIFICATE-----.");
        }

        if (collection.Count == 0)
        {
            throw new ArgumentException("The certificate authority has no certificate in it. Paste the PEM text, starting with -----BEGIN CERTIFICATE-----.");
        }

        return collection;
    }

    /// <summary>"Example Root CA (expires 2031-04-01)", for the settings screen.</summary>
    public static string Describe(string pem)
    {
        var certs = ParseCa(pem);
        return string.Join("; ", certs.Select(c => $"{c.GetNameInfo(X509NameType.SimpleName, false)} (expires {c.NotAfter:yyyy-MM-dd})"));
    }

    /// <summary>
    /// Accepts the server's certificate when the system trusts it, or when it chains to one of <paramref name="roots"/>.
    /// The host name must always match: a private authority never excuses a certificate for another server. Revocation
    /// isn't checked against the private authority (air-gapped servers usually can't reach a CRL).
    /// </summary>
    public static bool Validate(X509Certificate? certificate, X509Chain? presented, SslPolicyErrors errors, X509Certificate2Collection? roots)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (roots is null || roots.Count == 0 || certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)
            || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        if (presented is not null)
        {
            foreach (var element in presented.ChainElements.Skip(1))
            {
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        return chain.Build(leaf);
    }
}

/// <summary>The HTTP handler GitLab calls go through. The real one guards addresses and trusts a private CA; tests use a fake GitLab.</summary>
public interface IGitLabHandlerFactory
{
    HttpMessageHandler Get(Uri baseUrl, string? caCertificatePem);
}

/// <summary>
/// One pooled handler per server and CA. Never follows redirects (a redirect could carry the token elsewhere); connects only
/// to public addresses unless the host is in <see cref="GitLabOptions.AllowedPrivateHosts"/> (or private targets are allowed
/// in Development), checked at connect time like <see cref="GuardedHttpTransport"/>.
/// </summary>
public sealed class GuardedGitLabHandlerFactory(OutboundUrlGuard guard, IOptions<GitLabOptions> options) : IGitLabHandlerFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, HttpMessageHandler> _handlers = new(StringComparer.Ordinal);

    public HttpMessageHandler Get(Uri baseUrl, string? caCertificatePem)
    {
        var key = $"{baseUrl.Scheme}://{baseUrl.Authority}|{(string.IsNullOrWhiteSpace(caCertificatePem) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(caCertificatePem))))}";
        return _handlers.GetOrAdd(key, _ => Create(caCertificatePem));
    }

    private SocketsHttpHandler Create(string? caPem)
    {
        var roots = string.IsNullOrWhiteSpace(caPem) ? null : GitLabTls.ParseCa(caPem);
        var settings = options.Value;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = settings.UseSystemProxy,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 2, 120)),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) => GitLabTls.Validate(certificate, chain, errors, roots),
            },
        };
        if (!settings.UseSystemProxy)
        {
            handler.ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = options.Value.IsPrivateHostAllowed(host)
                    ? await System.Net.Dns.GetHostAddressesAsync(host, ct)
                    : (await guard.AllowedAddressesAsync(host, ct)).ToArray();
                if (addresses.Length == 0)
                {
                    throw new HttpRequestException(
                        $"Refused: {host} resolves only to private or internal addresses. Your server operator can allow it in GitLab:AllowedPrivateHosts.");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return handler;
    }

    public void Dispose()
    {
        foreach (var handler in _handlers.Values)
        {
            handler.Dispose();
        }
    }
}
