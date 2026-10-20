using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Workbench.Application.Integrations;

/// <summary>Resolves host names for the SSRF guard (a fake in tests, so no lookups leave the machine).</summary>
public interface IOutboundDns
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemOutboundDns : IOutboundDns
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// The SSRF guard for every outbound call Workbench makes for integrations (webhooks, Teams and Slack, SFTP): https only
/// (outside Development), no credentials in the URL, and the host must resolve only to public addresses (no loopback, private,
/// link-local, cloud metadata, carrier-grade NAT, multicast or reserved ranges, in IPv4 or IPv6). <see cref="GuardedHttpTransport"/>
/// checks the address again when it connects, so a DNS answer that changes after registration (rebinding) is refused too.
/// </summary>
public sealed class OutboundUrlGuard(IOutboundDns dns, IOptions<IntegrationOptions> options)
{
    private static readonly string[] InternalSuffixes = [".localhost", ".local", ".internal", ".intranet", ".lan", ".home.arpa", ".corp", ".localdomain"];

    /// <summary>Checks a URL an admin typed in. Throws <see cref="ArgumentException"/> with a plain reason.</summary>
    public async Task<Uri> ValidateAsync(string? url, string what, CancellationToken cancellationToken)
    {
        var uri = ParseShape(url, what, options.Value.AllowHttp);
        if (!options.Value.AllowPrivateNetworkTargets)
        {
            await EnsurePublicHostAsync(uri.IdnHost, what, cancellationToken);
        }

        return uri;
    }

    /// <summary>Checks a host name (an SFTP server) the same way.</summary>
    public async Task EnsurePublicHostAsync(string host, string what, CancellationToken cancellationToken)
    {
        if (options.Value.AllowPrivateNetworkTargets)
        {
            return;
        }

        host = host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();
        if (host.Length == 0 || host == "localhost" || InternalSuffixes.Any(host.EndsWith) || !host.Contains('.') && !IPAddress.TryParse(host, out _))
        {
            throw new ArgumentException($"{what} must be a public address, not an internal name like {host}.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await dns.ResolveAsync(host, cancellationToken);
            }
            catch (SocketException)
            {
                throw new ArgumentException($"{what}: we couldn't find the server {host}. Check the address.");
            }
        }

        if (addresses.Length == 0)
        {
            throw new ArgumentException($"{what}: we couldn't find the server {host}. Check the address.");
        }

        if (addresses.FirstOrDefault(a => !IsPublic(a)) is { } blocked)
        {
            throw new ArgumentException($"{what} points at a private or internal address ({blocked}). Workbench only sends to public internet addresses.");
        }
    }

    /// <summary>The addresses of <paramref name="host"/> Workbench may connect to (empty when every one is blocked).</summary>
    public async Task<IReadOnlyList<IPAddress>> AllowedAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await dns.ResolveAsync(host, cancellationToken);
        return options.Value.AllowPrivateNetworkTargets ? addresses : addresses.Where(IsPublic).ToList();
    }

    public bool AllowHttp => options.Value.AllowHttp;

    internal static Uri ParseShape(string? url, string what, bool allowHttp)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"{what} must be a full web address starting with https://.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(allowHttp && uri.Scheme == Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"{what} must start with https://.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException($"{what} can't contain a user name or password.");
        }

        if (uri.Fragment.Length > 0)
        {
            throw new ArgumentException($"{what} can't contain a # fragment.");
        }

        if (url.Trim().Length > 2000)
        {
            throw new ArgumentException($"{what} is too long (2,000 characters at most).");
        }

        return uri;
    }

    /// <summary>True for globally routable unicast addresses; false for every private, local, reserved or special range.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0                                           // 0.0.0.0/8 "this network"
                || b[0] == 10                                            // 10/8 private
                || b[0] == 127                                           // loopback
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)             // 100.64/10 carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                          // link-local, cloud metadata (169.254.169.254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)              // 172.16/12 private
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)               // 192.0.0/24 IETF protocol assignments
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)               // TEST-NET-1
                || (b[0] == 192 && b[1] == 88 && b[2] == 99)             // 6to4 relay anycast
                || (b[0] == 192 && b[1] == 168)                          // 192.168/16 private
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))           // 198.18/15 benchmarking
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)            // TEST-NET-2
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)             // TEST-NET-3
                || b[0] >= 224);                                         // multicast, reserved, broadcast
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
        {
            return false;
        }

        var v6 = address.GetAddressBytes();
        // 64:ff9b::/96 (NAT64) and 2002::/16 (6to4) embed an IPv4 address: judge that one.
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b && v6[4..12].All(x => x == 0))
        {
            return IsPublic(new IPAddress(v6[12..16]));
        }

        if (v6[0] == 0x20 && v6[1] == 0x02)
        {
            return IsPublic(new IPAddress(v6[2..6]));
        }

        // 2001:db8::/32 documentation, 2001::/32 Teredo, 100::/64 discard, ::/8 reserved (incl. IPv4-compatible).
        if ((v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8)
            || (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x00 && v6[3] == 0x00)
            || (v6[0] == 0x01 && v6[1] == 0x00 && v6[2..8].All(x => x == 0))
            || v6[0] == 0x00)
        {
            return false;
        }

        // Only global unicast (2000::/3) is public.
        return (v6[0] & 0xe0) == 0x20;
    }
}
