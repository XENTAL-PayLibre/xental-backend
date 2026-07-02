using System.Net;
using System.Net.Sockets;
using Xental.Application.Common.Exceptions;
using Xental.Application.Common.Interfaces;

namespace Xental.Infrastructure.Webhooks;

/// <summary>
/// SSRF protection for developer webhook URLs: requires HTTPS and rejects any host that resolves
/// to a non-public address (loopback, private RFC1918, link-local, CGNAT, ULA, etc.). Re-checked
/// before each delivery to blunt DNS rebinding.
/// </summary>
public sealed class OutboundUrlGuard : IOutboundUrlGuard
{
    public async Task EnsureSafeAsync(string url, CancellationToken ct = default)
    {
        if (!await IsSafeAsync(url, ct))
            throw new ValidationException("Webhook URL must be a public HTTPS endpoint (private/loopback hosts are not allowed).");
    }

    public async Task<bool> IsSafeAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
        }
        catch
        {
            return false;
        }
        return addresses.Length > 0 && addresses.All(IsPublic);
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            return false;

        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] switch
            {
                0 or 127 or 10 => false,                       // this-network, loopback, private
                169 when b[1] == 254 => false,                 // link-local
                172 when b[1] is >= 16 and <= 31 => false,     // private
                192 when b[1] == 168 => false,                 // private
                100 when b[1] is >= 64 and <= 127 => false,    // CGNAT
                _ => true,
            };
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if ((b[0] & 0xFE) == 0xFC) return false;           // unique local fc00::/7
            var mapped = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : null;
            return mapped is null || IsPublic(mapped);
        }
        return false;
    }
}
