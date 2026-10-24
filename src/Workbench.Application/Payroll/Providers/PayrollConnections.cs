using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Payroll;

namespace Workbench.Application.Payroll;

/// <summary>One field on a provider's connect screen.</summary>
public sealed record PayrollConnectionField(string Key, string Label, bool IsSecret, bool Required = true, bool Multiline = false, string? Help = null, string? Placeholder = null);

/// <summary>What a provider's connect screen asks for and where the customer gets it.</summary>
public sealed record PayrollConnectionSpec(
    IReadOnlyList<PayrollConnectionField> Fields,
    string SetupHelp,
    string DocsUrl,
    IReadOnlyList<string> Environments);

/// <summary>Decrypted credentials for one provider. Never logged, never returned to a client.</summary>
public sealed record PayrollCredentials(string ProviderKey, string Environment, IReadOnlyDictionary<string, string> Values)
{
    public string this[string key] => Values.TryGetValue(key, out var v) && v.Length > 0
        ? v
        : throw new PayrollProviderApiException($"The {key} isn't set in the connection. Open Payroll › Providers and connect again.");

    public string? Get(string key) => Values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public bool IsSandbox => string.Equals(Environment, "sandbox", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A provider API call failed: the message is safe to show (it never contains credentials).</summary>
public sealed class PayrollProviderApiException(string message) : Exception(message);

/// <summary>
/// The customers' own payroll provider credentials (<see cref="PayrollProviderConnection"/>), encrypted at rest with
/// Data Protection like the other integrations' secrets. Only adapters read them; the setup service writes them.
/// </summary>
public sealed class PayrollConnections(IWorkbenchDbContextFactory dbFactory, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Workbench.Payroll.ProviderCredentials.v1");

    public async Task<PayrollCredentials?> GetAsync(string providerKey, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var row = await db.Set<PayrollProviderConnection>().AsNoTracking().FirstOrDefaultAsync(c => c.ProviderKey == providerKey, ct);
        return row is null ? null : new PayrollCredentials(providerKey, row.Environment, Unprotect(row.ProtectedCredentials));
    }

    public async Task<bool> IsConnectedAsync(string providerKey, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<PayrollProviderConnection>().AnyAsync(c => c.ProviderKey == providerKey, ct);
    }

    /// <summary>Replaces one stored value (a rotated OAuth refresh token) without touching the rest.</summary>
    public async Task UpdateValueAsync(string providerKey, string key, string value, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var row = await db.Set<PayrollProviderConnection>().FirstOrDefaultAsync(c => c.ProviderKey == providerKey, ct);
        if (row is null)
        {
            return;
        }

        var values = Unprotect(row.ProtectedCredentials);
        values[key] = value;
        row.ProtectedCredentials = Protect(values);
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public string Protect(IReadOnlyDictionary<string, string> values) => _protector.Protect(JsonSerializer.Serialize(values));

    public Dictionary<string, string> Unprotect(string protectedValues)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(protectedValues)) ?? [];
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Keys rotated away or a database restored elsewhere: the customer has to connect again.
            throw new PayrollProviderApiException("The saved credentials can't be read on this server. Connect again under Payroll › Providers.");
        }
    }
}
