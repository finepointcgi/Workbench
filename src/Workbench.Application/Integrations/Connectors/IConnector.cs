using Workbench.Contracts.Integrations;
using Workbench.Domain.Integrations;

namespace Workbench.Application.Integrations.Connectors;

/// <summary>
/// A prebuilt connector: a kind of configurable sync with its own settings and a run log. Event connectors (Teams, Slack)
/// post subscribed events through the dispatcher's delivery queue (retries and backoff included); schedule connectors
/// (GL export, report export) produce a file on a schedule or on demand. Register with <c>services.AddScoped&lt;IConnector, T&gt;()</c>.
/// Connectors make outbound calls only through <see cref="IOutboundTransport"/> and <see cref="ISftpUploader"/>, which tests fake.
/// </summary>
public interface IConnector
{
    /// <summary>Stable key stored on the connector row (<c>teams</c>, <c>slack</c>, <c>gl-export</c>, <c>report-export</c>).</summary>
    string Kind { get; }

    string Name { get; }

    string Description { get; }

    ConnectorMode Mode { get; }

    /// <summary>The settings form. May read data for options (saved reports, companies) through the caller's services.</summary>
    Task<IReadOnlyList<ConnectorFieldDto>> GetFieldsAsync(CancellationToken cancellationToken);

    /// <summary>Checks the settings before they are saved; throws <see cref="ArgumentException"/> with a plain reason.</summary>
    Task ValidateAsync(ConnectorConfig config, CancellationToken cancellationToken);

    /// <summary>Does the work: posts one event (or a test message when <see cref="ConnectorContext.Event"/> is null), or produces and sends a file.</summary>
    Task<ConnectorResult> RunAsync(ConnectorContext context, CancellationToken cancellationToken);
}

/// <summary>A connector's settings: non-secret values and decrypted secrets, by field key.</summary>
public sealed record ConnectorConfig(IReadOnlyDictionary<string, string> Settings, IReadOnlyDictionary<string, string> Secrets)
{
    public string? Get(string key) =>
        Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim()
        : Secrets.TryGetValue(key, out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim()
        : null;

    public string Required(string key, string label) => Get(key) ?? throw new ArgumentException($"{label} is required.");
}

public sealed record ConnectorContext(
    IntegrationConnector Connector,
    ConnectorConfig Config,
    ConnectorTrigger Trigger,
    IntegrationOutboxEvent? Event,
    DateTime UtcNow);

public sealed record ConnectorFile(string FileName, string ContentType, byte[] Content);

/// <param name="Request">For HTTP connectors: what was sent, for the delivery log.</param>
/// <param name="Response">For HTTP connectors: what came back; a failure is retried when <see cref="RetryPolicy.IsRetryable"/>.</param>
public sealed record ConnectorResult(
    bool Success,
    string Message,
    int ItemCount = 0,
    ConnectorFile? File = null,
    OutboundRequest? Request = null,
    OutboundResponse? Response = null);

/// <summary>Where a schedule connector sends its file.</summary>
public sealed record SftpTarget(string Host, int Port, string Username, string? Password, string? PrivateKey, string Folder, string HostKeySha256);

/// <summary>Uploads a file over SFTP. The real one lives in Infrastructure (SSH.NET); tests use a fake.</summary>
public interface ISftpUploader
{
    Task UploadAsync(SftpTarget target, string fileName, byte[] content, CancellationToken cancellationToken);
}

internal sealed class UnavailableSftpUploader : ISftpUploader
{
    public Task UploadAsync(SftpTarget target, string fileName, byte[] content, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("SFTP isn't available on this server. Choose \"Keep in Workbench\" or ask your administrator.");
}

/// <summary>Field helpers shared by the file connectors: destination (keep in Workbench or SFTP) and the SFTP settings.</summary>
internal static class FileDestination
{
    public const string Destination = "destination";
    public const string KeepOnly = "workbench";
    public const string Sftp = "sftp";

    public static IReadOnlyList<ConnectorFieldDto> Fields =>
    [
        new(Destination, "Send the file to", ConnectorFieldKind.Select, true, "Workbench always keeps a copy you can download from the run log.",
            [new(KeepOnly, "Keep in Workbench only"), new(Sftp, "An SFTP server")], KeepOnly),
        new("sftpHost", "SFTP server", ConnectorFieldKind.Text, false, "Host name, such as sftp.example.com. Must be a public address."),
        new("sftpPort", "SFTP port", ConnectorFieldKind.Number, false, null, null, "22"),
        new("sftpUser", "SFTP user name", ConnectorFieldKind.Text, false),
        new("sftpPassword", "SFTP password", ConnectorFieldKind.Secret, false, "Leave blank when using a private key."),
        new("sftpPrivateKey", "SFTP private key", ConnectorFieldKind.Secret, false, "OpenSSH private key (PEM), instead of a password."),
        new("sftpFolder", "Folder on the server", ConnectorFieldKind.Text, false, null, null, "/"),
        new("sftpHostKey", "Server key fingerprint (SHA256)", ConnectorFieldKind.Text, false,
            "The server's SHA256 host key fingerprint, as ssh-keyscan or your provider shows it. Workbench refuses any other server."),
    ];

    public static async Task ValidateAsync(ConnectorConfig config, OutboundUrlGuard guard, CancellationToken ct)
    {
        var destination = config.Get(Destination) ?? KeepOnly;
        if (destination is not (KeepOnly or Sftp))
        {
            throw new ArgumentException("Choose where to send the file.");
        }

        if (destination == Sftp)
        {
            var target = Target(config);
            await guard.EnsurePublicHostAsync(target.Host, "The SFTP server", ct);
        }
    }

    public static SftpTarget Target(ConnectorConfig config)
    {
        var host = config.Required("sftpHost", "The SFTP server");
        var port = int.TryParse(config.Get("sftpPort") ?? "22", out var p) && p is > 0 and < 65536 ? p : throw new ArgumentException("The SFTP port must be between 1 and 65535.");
        var user = config.Required("sftpUser", "The SFTP user name");
        var password = config.Get("sftpPassword");
        var key = config.Get("sftpPrivateKey");
        if (password is null && key is null)
        {
            throw new ArgumentException("Give the SFTP password or a private key.");
        }

        var fingerprint = config.Required("sftpHostKey", "The server key fingerprint");
        fingerprint = fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase) ? fingerprint[7..] : fingerprint;
        return new SftpTarget(host, port, user, password, key, config.Get("sftpFolder") ?? "/", fingerprint.TrimEnd('='));
    }

    /// <summary>Sends the file where the settings say. Returns a phrase for the run log.</summary>
    public static async Task<string> SendAsync(ConnectorConfig config, ConnectorFile file, ISftpUploader sftp, OutboundUrlGuard guard, CancellationToken ct)
    {
        if ((config.Get(Destination) ?? KeepOnly) != Sftp)
        {
            return "kept in Workbench";
        }

        var target = Target(config);
        await guard.EnsurePublicHostAsync(target.Host, "The SFTP server", ct);
        await sftp.UploadAsync(target, file.FileName, file.Content, ct);
        return $"uploaded to {target.Host}{(target.Folder.StartsWith('/') ? "" : "/")}{target.Folder}";
    }
}
