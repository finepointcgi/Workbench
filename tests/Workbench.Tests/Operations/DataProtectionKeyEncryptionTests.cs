using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Tenancy;
using Workbench.Domain.Security;
using Workbench.Web.Security;

namespace Workbench.Tests.Operations;

/// <summary>Data Protection keys encrypted at rest (certificate, AWS KMS, Azure Key Vault), rotation and the startup check.</summary>
public sealed class DataProtectionKeyEncryptionTests : IDisposable
{
    private const string Tin = "12-3456789";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"wb-dp-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_certificate_encrypts_the_key_ring_at_rest()
    {
        var pfx = TestCertificates.CreatePfx("dp-a");
        using var services = FolderServices(Certificate(pfx));
        var protectedTin = Protector(services).Protect(Tin);

        var xml = KeyFiles();
        Assert.Contains("encryptedSecret", xml);
        Assert.Contains("EncryptedData", xml);
        Assert.DoesNotContain("<value>", xml); // The plain master key would be <masterKey><value>...</value>.
        Assert.Equal(Tin, Protector(services).Unprotect(protectedTin));
    }

    [Fact]
    public async Task Rotating_the_certificate_keeps_protected_data_readable_and_rewrap_retires_the_old_one()
    {
        var oldPfx = TestCertificates.CreatePfx("dp-old");
        var newPfx = TestCertificates.CreatePfx("dp-new");
        string protectedTin;
        using (var before = FolderServices(Certificate(oldPfx)))
        {
            protectedTin = Protector(before).Protect(Tin);
        }

        // Without the old certificate the new one can't read keys the old one encrypted (tried on a copy: the key
        // manager would add a fresh key to the ring it can't read).
        var copy = _folder + "-copy";
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.EnumerateFiles(_folder))
        {
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        }

        var newOnlySettings = Certificate(newPfx);
        newOnlySettings["DataProtection:Store"] = "FileSystem";
        newOnlySettings["DataProtection:KeysFolder"] = copy;
        using (var newOnly = Build(newOnlySettings, null))
        {
            Assert.ThrowsAny<CryptographicException>(() => Protector(newOnly).Unprotect(protectedTin));
        }

        Directory.Delete(copy, recursive: true);

        // Step 1: new certificate current, old one listed under PreviousCertificates: everything still reads.
        var rotating = Certificate(newPfx);
        rotating["DataProtection:PreviousCertificates:0:Path"] = oldPfx;
        rotating["DataProtection:PreviousCertificates:0:Password"] = TestCertificates.Password;
        using (var during = FolderServices(rotating))
        {
            Assert.Equal(Tin, Protector(during).Unprotect(protectedTin));

            // Step 2: --rewrap-data-protection-keys re-encrypts every key with the new certificate.
            Assert.Equal(1, await DataProtectionKeyRewrap.RunAsync(during));
        }

        // Step 3: the old certificate can be removed.
        using var after = FolderServices(Certificate(newPfx));
        Assert.Equal(Tin, Protector(after).Unprotect(protectedTin));
        Assert.DoesNotContain("<value>", KeyFiles());
    }

    [Fact]
    public async Task Rewrap_encrypts_an_existing_unencrypted_key_ring_in_the_database()
    {
        var database = TestDb.CreateFactory();
        string protectedTin;
        using (var plain = DatabaseServices(database, []))
        {
            protectedTin = Protector(plain).Protect(Tin);
        }

        Assert.Contains("<value>", await StoredKeysAsync(database));

        var pfx = TestCertificates.CreatePfx("dp-db");
        using (var encrypting = DatabaseServices(database, Certificate(pfx)))
        {
            Assert.Equal(1, await DataProtectionKeyRewrap.RunAsync(encrypting));
        }

        var stored = await StoredKeysAsync(database);
        Assert.DoesNotContain("<value>", stored);
        Assert.Contains("encryptedSecret", stored);
        using var after = DatabaseServices(database, Certificate(pfx));
        Assert.Equal(Tin, Protector(after).Unprotect(protectedTin));
    }

    [Fact]
    public void Aws_kms_wraps_each_key_and_unwraps_it_again()
    {
        var kms = new FakeKeyWrapClient();
        var settings = new Dictionary<string, string?>
        {
            ["DataProtection:KeyEncryption"] = "AwsKms",
            ["DataProtection:AwsKms:KeyId"] = "arn:aws:kms:us-east-1:111122223333:alias/workbench-dp",
        };
        string protectedTin;
        using (var services = FolderServices(settings, s => s.AddSingleton<IKeyWrapClient>(kms)))
        {
            Assert.IsType<KeyWrapXmlEncryptor>(services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor);
            protectedTin = Protector(services).Protect(Tin);
        }

        Assert.Contains("wrappedKey", KeyFiles());
        Assert.DoesNotContain("<value>", KeyFiles());
        Assert.Equal(1, kms.Encryptions);

        using var again = FolderServices(settings, s => s.AddSingleton<IKeyWrapClient>(kms));
        Assert.Equal(Tin, Protector(again).Unprotect(protectedTin));
        Assert.Equal(["arn:aws:kms:us-east-1:111122223333:alias/workbench-dp"], kms.DecryptedWith.Distinct());
    }

    [Fact]
    public void Azure_key_vault_is_chosen_by_configuration()
    {
        using var services = FolderServices(new()
        {
            ["DataProtection:KeyEncryption"] = "AzureKeyVault",
            ["DataProtection:AzureKeyVault:KeyId"] = "https://workbench-test.vault.azure.net/keys/dp",
        });

        var encryptor = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor;
        Assert.NotNull(encryptor);
        Assert.Contains("AzureKeyVault", encryptor.GetType().Name);
    }

    [Fact]
    public void A_configured_certificate_is_used_without_naming_the_mode()
    {
        var settings = new DataProtectionSettings { CertificateThumbprint = "ABC" };
        Assert.Equal(KeyEncryptionMode.Certificate, settings.ResolveKeyEncryption());
        Assert.Equal(KeyEncryptionMode.None, new DataProtectionSettings().ResolveKeyEncryption());
        Assert.Throws<InvalidOperationException>(() => new DataProtectionSettings { KeyEncryption = "Rot13" }.ResolveKeyEncryption());
    }

    [Fact]
    public void Production_refuses_unencrypted_or_incomplete_key_encryption()
    {
        var pfx = TestCertificates.CreatePfx("dp-check");
        Assert.Contains(DataProtectionProblems("Production", []), p => p.Contains("aren't encrypted at rest"));
        Assert.DoesNotContain(DataProtectionProblems("Staging", []), p => p.Contains("aren't encrypted at rest"));
        Assert.Empty(DataProtectionProblems("Production", Certificate(pfx)));
        Assert.Empty(DataProtectionProblems("Production", new() { ["DataProtection:CertificateThumbprint"] = "0123456789ABCDEF" }));
        Assert.Empty(DataProtectionProblems("Production", new()
        {
            ["DataProtection:KeyEncryption"] = "AzureKeyVault",
            ["DataProtection:AzureKeyVault:KeyId"] = "https://workbench.vault.azure.net/keys/dp",
        }));

        Assert.Contains(DataProtectionProblems("Production", new() { ["DataProtection:CertificatePath"] = "/nope/dp.pfx" }), p => p.Contains("doesn't exist"));
        Assert.Contains(DataProtectionProblems("Staging", new() { ["DataProtection:KeyEncryption"] = "Certificate" }), p => p.Contains("CertificatePath"));
        Assert.Contains(DataProtectionProblems("Production", new() { ["DataProtection:KeyEncryption"] = "AwsKms" }), p => p.Contains("AwsKms:KeyId"));
        Assert.Contains(DataProtectionProblems("Production", new()
        {
            ["DataProtection:KeyEncryption"] = "AzureKeyVault",
            ["DataProtection:AzureKeyVault:KeyId"] = "http://insecure/keys/dp",
        }), p => p.Contains("AzureKeyVault:KeyId"));
        Assert.Contains(DataProtectionProblems("Production", new() { ["DataProtection:KeyEncryption"] = "Plaintext" }), p => p.Contains("Unsupported"));

        var defaultStore = Certificate(pfx);
        defaultStore["DataProtection:Store"] = "Default";
        Assert.Contains(DataProtectionProblems("Production", defaultStore), p => p.Contains("Store is Default"));
    }

    [Fact]
    public void Production_refuses_smtp_without_tls()
    {
        var pfx = TestCertificates.CreatePfx("dp-smtp");
        var plain = Certificate(pfx);
        plain["Email:Smtp:Host"] = "smtp.contoso.test";
        plain["Email:Smtp:EnableSsl"] = "false";
        var none = Certificate(pfx);
        none["Email:Mode"] = "Smtp";
        none["Email:Smtp:Security"] = "None";
        var tls = Certificate(pfx);
        tls["Email:Smtp:Host"] = "smtp.contoso.test";
        tls["Email:Smtp:Port"] = "587";

        Assert.Contains(DataProtectionProblems("Production", plain), p => p.Contains("without TLS"));
        Assert.Contains(DataProtectionProblems("Production", none), p => p.Contains("without TLS"));
        Assert.Empty(DataProtectionProblems("Production", tls));
    }

    [Fact]
    public void The_host_refuses_to_start_in_production_without_key_encryption()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Authority"] = "https://login.example.test/",
            ["Authentication:Oidc:ClientId"] = "workbench",
            ["Security:TlsTerminatedByProxy"] = "true",
            ["Scim:Enabled"] = "false",
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => ProductionConfigurationCheck.Enforce(configuration, Environment("Production")));
        Assert.Contains("Data Protection keys", error.Message);
    }

    private static IEnumerable<string> DataProtectionProblems(string environment, Dictionary<string, string?> values)
    {
        // Only the Data Protection findings: the rest of the check is satisfied.
        var all = new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Authority"] = "https://login.example.test/",
            ["Authentication:Oidc:ClientId"] = "workbench",
            ["Security:TlsTerminatedByProxy"] = "true",
            ["Scim:Enabled"] = "false",
        };
        foreach (var (key, value) in values)
        {
            all[key] = value;
        }

        return ProductionConfigurationCheck.FindProblems(new ConfigurationBuilder().AddInMemoryCollection(all).Build(), Environment(environment));
    }

    private static Dictionary<string, string?> Certificate(string pfx) => new()
    {
        ["DataProtection:CertificatePath"] = pfx,
        ["DataProtection:CertificatePassword"] = TestCertificates.Password,
    };

    private ServiceProvider FolderServices(Dictionary<string, string?> values, Action<IServiceCollection>? before = null)
    {
        values["DataProtection:Store"] = "FileSystem";
        values["DataProtection:KeysFolder"] = _folder;
        return Build(values, before);
    }

    private static ServiceProvider DatabaseServices(IWorkbenchDbContextFactory database, Dictionary<string, string?> values) =>
        Build(values, s =>
        {
            s.AddSingleton(database);
            s.AddSingleton(new TenancyOptions());
        });

    private static ServiceProvider Build(Dictionary<string, string?> values, Action<IServiceCollection>? before)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        before?.Invoke(services);
        services.AddWorkbenchDataProtection(configuration, Environment("Production"));
        return services.BuildServiceProvider();
    }

    private static IDataProtector Protector(IServiceProvider services) =>
        services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Workbench.Vendors.Tin");

    private string KeyFiles() => string.Concat(Directory.EnumerateFiles(_folder, "*.xml").Select(File.ReadAllText));

    private static async Task<string> StoredKeysAsync(IWorkbenchDbContextFactory database)
    {
        await using var db = await database.CreateAsync();
        return string.Concat(await db.Set<DataProtectionKeyRecord>().Select(k => k.Xml).ToListAsync());
    }

    private static HostingEnvironment Environment(string name) => new() { EnvironmentName = name, ApplicationName = "Workbench.Web", ContentRootPath = AppContext.BaseDirectory };

    /// <summary>Stands in for AWS KMS: a reversible transform that remembers which key id was asked for.</summary>
    private sealed class FakeKeyWrapClient : IKeyWrapClient
    {
        public int Encryptions { get; private set; }
        public List<string> DecryptedWith { get; } = [];

        public byte[] Encrypt(string keyId, byte[] plaintext)
        {
            Encryptions++;
            return [.. plaintext.Select(b => (byte)(b ^ 0x5A)).Reverse()];
        }

        public byte[] Decrypt(string keyId, byte[] ciphertext)
        {
            DecryptedWith.Add(keyId);
            return [.. ciphertext.Reverse().Select(b => (byte)(b ^ 0x5A))];
        }
    }
}
