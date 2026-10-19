using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Amazon;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workbench.Web.Security;

/// <summary>
/// Encrypts the Data Protection key ring at rest (A.8.24). The keys protect auth cookies and the secrets Workbench
/// stores encrypted (vendor TINs, bank account numbers, tenant connection strings), so whoever reads the key table
/// must not be able to use it. <c>DataProtection:KeyEncryption</c> picks a certificate, Azure Key Vault or AWS KMS.
/// </summary>
public static class DataProtectionKeyEncryption
{
    public static void Configure(IDataProtectionBuilder builder, DataProtectionSettings settings)
    {
        switch (settings.ResolveKeyEncryption())
        {
            case KeyEncryptionMode.None:
                break;
            case KeyEncryptionMode.Certificate:
                ConfigureCertificate(builder, settings);
                break;
            case KeyEncryptionMode.AzureKeyVault:
                var vault = settings.AzureKeyVault;
                if (!Uri.TryCreate(vault.KeyId, UriKind.Absolute, out var keyId) || keyId.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidOperationException("Set DataProtection:AzureKeyVault:KeyId to the key's https:// identifier.");
                }

                builder.ProtectKeysWithAzureKeyVault(keyId, AzureCredential(vault));
                break;
            case KeyEncryptionMode.AwsKms:
                var kms = settings.AwsKms;
                if (string.IsNullOrWhiteSpace(kms.KeyId))
                {
                    throw new InvalidOperationException("Set DataProtection:AwsKms:KeyId to the KMS key's ARN or alias ARN.");
                }

                // TryAdd: a registration made before this one (a test double) wins.
                builder.Services.TryAddSingleton<IKeyWrapClient>(_ => new AwsKmsKeyWrapClient(string.IsNullOrWhiteSpace(kms.Region)
                    ? new AmazonKeyManagementServiceClient()
                    : new AmazonKeyManagementServiceClient(RegionEndpoint.GetBySystemName(kms.Region))));
                builder.Services.AddOptions<KeyManagementOptions>().Configure<IKeyWrapClient>((o, client) => o.XmlEncryptor = new KeyWrapXmlEncryptor(client, kms.KeyId));
                break;
        }
    }

    private static void ConfigureCertificate(IDataProtectionBuilder builder, DataProtectionSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CertificatePath))
        {
            builder.ProtectKeysWithCertificate(LoadPfx(settings.CertificatePath, settings.CertificatePassword));
        }
        else if (!string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
        {
            // Encrypting with a store certificate also adds it to the decryption certificates.
            builder.ProtectKeysWithCertificate(FromStore(settings.CertificateThumbprint));
        }
        else
        {
            throw new InvalidOperationException("DataProtection:KeyEncryption is Certificate: set DataProtection:CertificatePath (a PFX) or :CertificateThumbprint.");
        }

        var previous = settings.PreviousCertificates
            .Select(c => !string.IsNullOrWhiteSpace(c.Path) ? LoadPfx(c.Path, c.Password)
                : !string.IsNullOrWhiteSpace(c.Thumbprint) ? FromStore(c.Thumbprint)
                : null)
            .OfType<X509Certificate2>()
            .ToArray();
        if (previous.Length > 0)
        {
            builder.UnprotectKeysWithAnyCertificate(previous);
        }
    }

    private static X509Certificate2 LoadPfx(string path, string? password)
    {
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
        return certificate.HasPrivateKey
            ? certificate
            : throw new InvalidOperationException($"The Data Protection certificate in {path} has no private key.");
    }

    /// <summary>Looks in the <c>My</c> store of the current user, then the local machine (Windows and Linux).</summary>
    internal static X509Certificate2 FromStore(string thumbprint)
    {
        var wanted = thumbprint.Replace(" ", "", StringComparison.Ordinal).Trim();
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                continue; // That store doesn't exist on this machine.
            }

            var found = store.Certificates.Find(X509FindType.FindByThumbprint, wanted, validOnly: false);
            if (found.FirstOrDefault(c => c.HasPrivateKey) is { } certificate)
            {
                return certificate;
            }
        }

        throw new InvalidOperationException($"No certificate with a private key and thumbprint {wanted} in the My store of the current user or local machine.");
    }

    private static TokenCredential AzureCredential(AzureKeyVaultKeySettings vault)
    {
        if (!string.IsNullOrWhiteSpace(vault.ClientSecret))
        {
            return new ClientSecretCredential(vault.TenantId, vault.ClientId, vault.ClientSecret);
        }

        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = string.IsNullOrWhiteSpace(vault.ClientId) ? null : vault.ClientId,
        });
    }
}

/// <summary>Encrypts and decrypts small secrets with a key held by a key management service (AWS KMS).</summary>
public interface IKeyWrapClient
{
    byte[] Encrypt(string keyId, byte[] plaintext);

    /// <summary>Decrypts; the service finds the key from the ciphertext (so rotated key material keeps working).</summary>
    byte[] Decrypt(string keyId, byte[] ciphertext);
}

/// <summary>AWS KMS <c>Encrypt</c>/<c>Decrypt</c> (a Data Protection key element is well under KMS's 4 KB limit).</summary>
public sealed class AwsKmsKeyWrapClient(IAmazonKeyManagementService kms) : IKeyWrapClient
{
    // Binds every ciphertext to this use: KMS refuses to decrypt it under any other context (and CloudTrail logs it).
    private static readonly Dictionary<string, string> Context = new() { ["workbench:purpose"] = "data-protection-key-ring" };

    public byte[] Encrypt(string keyId, byte[] plaintext)
    {
        using var input = new MemoryStream(plaintext);
        var response = kms.EncryptAsync(new EncryptRequest { KeyId = keyId, Plaintext = input, EncryptionContext = Context }).GetAwaiter().GetResult();
        return response.CiphertextBlob.ToArray();
    }

    public byte[] Decrypt(string keyId, byte[] ciphertext)
    {
        using var input = new MemoryStream(ciphertext);
        var response = kms.DecryptAsync(new DecryptRequest { KeyId = keyId, CiphertextBlob = input, EncryptionContext = Context }).GetAwaiter().GetResult();
        return response.Plaintext.ToArray();
    }
}

/// <summary>Wraps each key element with an <see cref="IKeyWrapClient"/> (AWS KMS).</summary>
public sealed class KeyWrapXmlEncryptor(IKeyWrapClient client, string keyId) : IXmlEncryptor
{
    internal static readonly XNamespace Ns = "https://schemas.workbench.app/2026/data-protection/key-wrap";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var element = new XElement(Ns + "wrappedKey",
            new XComment(" This key is encrypted with a key management service key. "),
            new XElement(Ns + "keyId", keyId),
            new XElement(Ns + "ciphertext", Convert.ToBase64String(client.Encrypt(keyId, plaintext))));
        return new EncryptedXmlInfo(element, typeof(KeyWrapXmlDecryptor));
    }
}

/// <summary>Created by Data Protection from the type name stored with each key; gets the client from the services.</summary>
public sealed class KeyWrapXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var client = services.GetService<IKeyWrapClient>()
            ?? throw new InvalidOperationException("This Data Protection key is wrapped with AWS KMS. Set DataProtection:KeyEncryption=AwsKms and DataProtection:AwsKms:KeyId to read it.");
        var keyId = (string?)encryptedElement.Element(KeyWrapXmlEncryptor.Ns + "keyId") ?? "";
        var ciphertext = Convert.FromBase64String((string?)encryptedElement.Element(KeyWrapXmlEncryptor.Ns + "ciphertext") ?? "");
        return XElement.Parse(System.Text.Encoding.UTF8.GetString(client.Decrypt(keyId, ciphertext)));
    }
}
