using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Tenancy;
using Workbench.Domain.Security;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Web.Security;

/// <summary>
/// <c>Workbench.Web --rewrap-data-protection-keys</c>: decrypts every stored Data Protection key with whatever
/// encrypted it (the previous certificate, vault key or KMS key, or nothing) and encrypts it again with the key
/// encryption configured now. Run it after rotating the key-encryption certificate or key, or after turning key
/// encryption on for an existing key ring. The keys themselves don't change, so cookies and everything already
/// protected (vendor TINs, bank accounts) stay readable. See docs/operations/data-protection-keys.md.
/// </summary>
public static class DataProtectionKeyRewrap
{
    private static readonly XNamespace DataProtectionNs = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XName EncryptedSecret = DataProtectionNs + "encryptedSecret";
    private static readonly XName RequiresEncryption = DataProtectionNs + "requiresEncryption";

    /// <summary>Re-encrypts every stored key and returns how many were rewritten. Restart the web hosts afterwards.</summary>
    public static async Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var encryptor = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor
            ?? throw new InvalidOperationException("No key encryption is configured. Set DataProtection:KeyEncryption (Certificate, AzureKeyVault or AwsKms) first.");
        var store = KeyStoreFor(services);
        var rows = await store.ListAsync(cancellationToken);

        // Rewrap (and verify) every key before writing any, so a key that can't be decrypted changes nothing.
        var rewrapped = rows.Select(r => (r.Id, Xml: Rewrap(XElement.Parse(r.Xml), encryptor, services))).ToList();
        foreach (var (id, xml) in rewrapped)
        {
            await store.SaveAsync(id, xml.ToString(SaveOptions.DisableFormatting), cancellationToken);
        }

        return rewrapped.Count;
    }

    /// <summary>
    /// Returns a copy of <paramref name="key"/> whose secrets are encrypted with <paramref name="encryptor"/>: encrypted
    /// secrets are decrypted with the decryptor named in them, plain ones (<c>requiresEncryption</c>) are encrypted.
    /// Every new secret is decrypted again and compared before it is returned.
    /// </summary>
    public static XElement Rewrap(XElement key, IXmlEncryptor encryptor, IServiceProvider services)
    {
        var copy = new XElement(key);
        foreach (var secret in copy.Descendants(EncryptedSecret).ToList())
        {
            secret.ReplaceWith(Encrypt(Decrypt(secret, services), encryptor, services));
        }

        foreach (var plain in copy.Descendants().Where(e => (bool?)e.Attribute(RequiresEncryption) == true).ToList())
        {
            plain.ReplaceWith(Encrypt(new XElement(plain), encryptor, services));
        }

        return copy;
    }

    private static XElement Encrypt(XElement plain, IXmlEncryptor encryptor, IServiceProvider services)
    {
        var info = encryptor.Encrypt(plain);
        var secret = new XElement(EncryptedSecret, new XAttribute("decryptorType", info.DecryptorType.AssemblyQualifiedName!), info.EncryptedElement);
        if (!XNode.DeepEquals(WithoutNamespaceDeclarations(Decrypt(secret, services)), WithoutNamespaceDeclarations(plain)))
        {
            throw new InvalidOperationException("A rewrapped Data Protection key didn't decrypt back to the original. Nothing was changed.");
        }

        return secret;
    }

    // Encrypting and decrypting can move namespace declarations (xmlns:...) around; the names themselves must match.
    private static XElement WithoutNamespaceDeclarations(XElement element)
    {
        var copy = new XElement(element);
        copy.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
        return copy;
    }

    private static XElement Decrypt(XElement secret, IServiceProvider services)
    {
        var typeName = (string?)secret.Attribute("decryptorType") ?? throw new InvalidOperationException("An encrypted Data Protection key names no decryptor.");
        var type = Type.GetType(typeName, throwOnError: true)!;
        var decryptor = (IXmlDecryptor)ActivatorUtilities.CreateInstance(services, type);
        return decryptor.Decrypt(secret.Elements().Single());
    }

    private static IKeyStore KeyStoreFor(IServiceProvider services)
    {
        var settings = services.GetRequiredService<IConfiguration>().GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new DataProtectionSettings();
        return settings.Store.ToLowerInvariant() switch
        {
            "database" when services.GetRequiredService<TenancyOptions>().IsMultiTenant => new CatalogKeyStore(services.GetRequiredService<IDbContextFactory<CatalogDbContext>>()),
            "database" => new DatabaseKeyStore(services.GetRequiredService<IWorkbenchDbContextFactory>()),
            "filesystem" => new FolderKeyStore(settings.KeysFolder!),
            _ => throw new InvalidOperationException($"--rewrap-data-protection-keys supports the Database and FileSystem stores, not '{settings.Store}'."),
        };
    }

    private interface IKeyStore
    {
        Task<List<(string Id, string Xml)>> ListAsync(CancellationToken cancellationToken);
        Task SaveAsync(string id, string xml, CancellationToken cancellationToken);
    }

    private sealed class DatabaseKeyStore(IWorkbenchDbContextFactory dbFactory) : IKeyStore
    {
        public async Task<List<(string Id, string Xml)>> ListAsync(CancellationToken cancellationToken)
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var rows = await db.Set<DataProtectionKeyRecord>().AsNoTracking().Select(k => new { k.Id, k.Xml }).ToListAsync(cancellationToken);
            return rows.Select(r => (r.Id.ToString(), r.Xml)).ToList();
        }

        public async Task SaveAsync(string id, string xml, CancellationToken cancellationToken)
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var row = await db.Set<DataProtectionKeyRecord>().SingleAsync(k => k.Id == Guid.Parse(id), cancellationToken);
            row.Xml = xml;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class CatalogKeyStore(IDbContextFactory<CatalogDbContext> catalog) : IKeyStore
    {
        public async Task<List<(string Id, string Xml)>> ListAsync(CancellationToken cancellationToken)
        {
            await using var db = await catalog.CreateDbContextAsync(cancellationToken);
            var rows = await db.DataProtectionKeys.AsNoTracking().Select(k => new { k.Id, k.Xml }).ToListAsync(cancellationToken);
            return rows.Select(r => (r.Id.ToString(), r.Xml)).ToList();
        }

        public async Task SaveAsync(string id, string xml, CancellationToken cancellationToken)
        {
            await using var db = await catalog.CreateDbContextAsync(cancellationToken);
            var row = await db.DataProtectionKeys.SingleAsync(k => k.Id == Guid.Parse(id), cancellationToken);
            row.Xml = xml;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class FolderKeyStore(string folder) : IKeyStore
    {
        public Task<List<(string Id, string Xml)>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Directory.EnumerateFiles(folder, "*.xml").Select(f => (f, File.ReadAllText(f))).ToList());

        public async Task SaveAsync(string id, string xml, CancellationToken cancellationToken)
        {
            var temp = id + ".rewrap";
            await File.WriteAllTextAsync(temp, xml, cancellationToken);
            File.Move(temp, id, overwrite: true);
        }
    }
}
