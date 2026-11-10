using System.Net;
using System.Net.Sockets;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Network;

public interface IDnsResolver
{
    Task<IPAddress[]> GetHostAddressesAsync(string hostName, CancellationToken ct);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public static readonly SystemDnsResolver Instance = new();

    public async Task<IPAddress[]> GetHostAddressesAsync(string hostName, CancellationToken ct)
    {
        return await Dns.GetHostAddressesAsync(hostName, ct);
    }
}

public sealed class SsrfDestinationValidator(IDnsResolver? dnsResolver = null) : IWebhookDestinationValidator
{
    private readonly IDnsResolver _dnsResolver = dnsResolver ?? SystemDnsResolver.Instance;

    public async Task<bool> ValidateAsync(string endpointUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpointUrl) || endpointUrl.Length > 2048)
        {
            return false;
        }

        if (!Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (uri.Port != 443 && !uri.IsDefaultPort)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var host = uri.DnsSafeHost;
        if (string.IsNullOrWhiteSpace(host) || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Direct IP address in URL check
        if (IPAddress.TryParse(host, out var directIp))
        {
            return !IsBlockedAddress(directIp);
        }

        try
        {
            var resolvedAddresses = await _dnsResolver.GetHostAddressesAsync(host, ct);
            if (resolvedAddresses == null || resolvedAddresses.Length == 0)
            {
                return false;
            }

            foreach (var ip in resolvedAddresses)
            {
                if (IsBlockedAddress(ip))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 0.0.0.0/8 (Current network)
            if (bytes[0] == 0) return true;

            // 127.0.0.0/8 (Loopback)
            if (bytes[0] == 127 || IPAddress.IsLoopback(address)) return true;

            // 10.0.0.0/8 (Private RFC 1918)
            if (bytes[0] == 10) return true;

            // 172.16.0.0/12 (Private RFC 1918)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

            // 192.168.0.0/16 (Private RFC 1918)
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // 169.254.0.0/16 (Link-local and AWS/GCP/Azure Cloud Metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // 100.64.0.0/10 (Carrier Grade NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;

            // 192.0.2.0/24 (TEST-NET-1), 198.51.100.0/24 (TEST-NET-2), 203.0.113.0/24 (TEST-NET-3)
            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) return true;
            if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return true;
            if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return true;

            // 224.0.0.0/4 (Multicast)
            if (bytes[0] >= 224 && bytes[0] <= 239) return true;

            // 240.0.0.0/4 and 255.255.255.255 (Reserved & Broadcast)
            if (bytes[0] >= 240) return true;

            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address)) return true;
            if (address.IsIPv6LinkLocal) return true;
            if (address.IsIPv6Multicast) return true;
            if (address.IsIPv6SiteLocal) return true;

            var bytes = address.GetAddressBytes();

            // :: (Unspecified)
            if (bytes.All(b => b == 0)) return true;

            // fc00::/7 (Unique Local Address RFC 4193)
            if ((bytes[0] & 0xfe) == 0xfc) return true;

            return false;
        }

        return true;
    }
}
