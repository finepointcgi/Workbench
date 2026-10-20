using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Workbench.Application.Integrations;
using Workbench.Application.Integrations.Connectors;

namespace Workbench.Infrastructure.Integrations;

/// <summary>
/// SFTP uploads for the file connectors (SSH.NET). Connects only to a public address the SSRF guard allows (resolved here, so
/// a DNS change can't redirect it inside the network) and only to the server whose SHA256 host key fingerprint the admin
/// pinned. Password or private key authentication.
/// </summary>
internal sealed class SshNetSftpUploader(OutboundUrlGuard guard) : ISftpUploader
{
    public async Task UploadAsync(SftpTarget target, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var addresses = await guard.AllowedAddressesAsync(target.Host, cancellationToken);
        if (addresses.Count == 0)
        {
            throw new InvalidOperationException($"Refused: the SFTP server {target.Host} resolves only to private or internal addresses.");
        }

        var methods = new List<AuthenticationMethod>();
        if (target.PrivateKey is { } key)
        {
            using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(key));
            methods.Add(new PrivateKeyAuthenticationMethod(target.Username, new PrivateKeyFile(keyStream)));
        }

        if (target.Password is { } password)
        {
            methods.Add(new PasswordAuthenticationMethod(target.Username, password));
        }

        var info = new ConnectionInfo(addresses[0].ToString(), target.Port, target.Username, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(20) };
        using var client = new SftpClient(info);
        client.HostKeyReceived += (_, e) => e.CanTrust = string.Equals(e.FingerPrintSHA256.TrimEnd('='), target.HostKeySha256, StringComparison.Ordinal);
        try
        {
            await client.ConnectAsync(cancellationToken);
            var folder = string.IsNullOrWhiteSpace(target.Folder) ? "/" : target.Folder.Trim();
            var path = folder.EndsWith('/') ? folder + fileName : $"{folder}/{fileName}";
            using var stream = new MemoryStream(content);
            await Task.Run(() => client.UploadFile(stream, path, canOverride: true), cancellationToken);
        }
        catch (SshConnectionException ex) when (ex.DisconnectReason == Renci.SshNet.Messages.Transport.DisconnectReason.HostKeyNotVerifiable)
        {
            throw new InvalidOperationException($"The SFTP server's key doesn't match the fingerprint you entered. Check it with your provider.");
        }
        catch (SshAuthenticationException)
        {
            throw new InvalidOperationException("The SFTP server refused the user name, password or key.");
        }
        catch (SshException ex)
        {
            throw new InvalidOperationException($"SFTP failed: {ex.Message}");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            throw new InvalidOperationException($"Couldn't reach the SFTP server: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }
}

public static class IntegrationInfrastructure
{
    /// <summary>The real SFTP uploader (replaces the application's "not available" default).</summary>
    public static IServiceCollection AddIntegrationInfrastructure(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ISftpUploader, SshNetSftpUploader>());
        return services;
    }
}
