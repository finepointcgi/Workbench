using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Workbench.Tests.Operations;

/// <summary>Self-signed certificates for Data Protection key encryption, written once per test run as PFX files.</summary>
internal static class TestCertificates
{
    public const string Password = "test-only-password";

    private static readonly Lazy<string> Shared = new(() => CreatePfx("workbench-test-dp"));

    /// <summary>One PFX for the whole run (the web host tests' production configuration).</summary>
    public static string SharedPfx => Shared.Value;

    /// <summary>A new RSA certificate with a private key, saved to a new temp file; returns its path.</summary>
    public static string CreatePfx(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(Path.GetTempPath(), $"{subject}-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));
        return path;
    }
}
