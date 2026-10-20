using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Workbench.Contracts.Integrations;

namespace Workbench.Application.Integrations;

/// <summary>
/// Webhook signing secrets and connector secrets (incoming webhook URLs, SFTP passwords) are encrypted at rest with Data
/// Protection; API keys are stored only as SHA-256 hashes.
/// </summary>
public sealed class IntegrationSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _webhook = provider.CreateProtector("Workbench.Integrations.WebhookSecret.v1");
    private readonly IDataProtector _connector = provider.CreateProtector("Workbench.Integrations.ConnectorSecrets.v1");

    public const string ApiKeyPrefix = "wbk_";

    public static string NewSigningSecret() => WebhookSignature.SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string NewApiKey() => ApiKeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lower-case hex SHA-256. Keys are 256 random bits, so a plain hash is enough (no stretching needed).</summary>
    public static string HashApiKey(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())));

    public static string DisplayPrefix(string key) => key[..Math.Min(key.Length, ApiKeyPrefix.Length + 8)];

    public string ProtectSigningSecret(string secret) => _webhook.Protect(secret);

    public string UnprotectSigningSecret(string protectedSecret) => _webhook.Unprotect(protectedSecret);

    public string? ProtectConnectorSecrets(IReadOnlyDictionary<string, string> secrets) =>
        secrets.Count == 0 ? null : _connector.Protect(JsonSerializer.Serialize(secrets));

    public Dictionary<string, string> UnprotectConnectorSecrets(string? protectedSecrets) =>
        string.IsNullOrEmpty(protectedSecrets)
            ? []
            : JsonSerializer.Deserialize<Dictionary<string, string>>(_connector.Unprotect(protectedSecrets)) ?? [];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
