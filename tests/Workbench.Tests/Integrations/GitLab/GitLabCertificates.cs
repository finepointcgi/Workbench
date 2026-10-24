using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Workbench.Tests.Integrations.GitLab;

/// <summary>A throwaway private certificate authority and server certificates, as a self-managed GitLab behind an internal CA has.</summary>
internal static class GitLabCertificates
{
    public static X509Certificate2 Authority(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    public static X509Certificate2 Issue(X509Certificate2 authority, string host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        var serial = RandomNumberGenerator.GetBytes(12);
        using var cert = request.Create(authority, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
        return cert.CopyWithPrivateKey(key);
    }

    public static string Pem(X509Certificate2 certificate) => certificate.ExportCertificatePem();
}
