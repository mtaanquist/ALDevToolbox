using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// SSRF protection for the outbound fetches the <see cref="CimdClientResolver"/>
/// performs against attacker-supplied URLs (the <c>client_id</c> metadata
/// document and its <c>jwks_uri</c>). Wired in as the
/// <see cref="SocketsHttpHandler.ConnectCallback"/> of the named HttpClient so
/// the check runs against the IP we actually dial — which closes the
/// DNS-rebinding hole a resolve-then-connect check would leave open — and with
/// <see cref="SocketsHttpHandler.AllowAutoRedirect"/> disabled so an HTTPS URL
/// can't 302 us onto an internal <c>http://</c> target. Connections to
/// loopback, link-local (incl. the cloud metadata service at 169.254.169.254),
/// private, CGNAT, and unique-local addresses are refused.
/// </summary>
public static class SsrfGuard
{
    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the
    /// target host, drops any address that isn't publicly routable, and dials
    /// only the survivors. Throws <see cref="IOException"/> when nothing is
    /// left so the fetch fails closed.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var resolved = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        var allowed = Array.FindAll(resolved, IsPubliclyRoutable);
        if (allowed.Length == 0)
        {
            throw new IOException(
                $"Refusing to connect to '{endpoint.Host}': it resolves only to disallowed " +
                "(loopback / link-local / private / unique-local) addresses.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when <paramref name="address"/> is a globally routable unicast
    /// address we're willing to fetch from. Conservative: anything we can't
    /// positively classify as public is refused.
    /// </summary>
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => false,                               // 0.0.0.0/8 "this network"
                10 => false,                              // 10.0.0.0/8 private
                127 => false,                             // 127.0.0.0/8 loopback
                100 when b[1] is >= 64 and <= 127 => false, // 100.64.0.0/10 CGNAT
                169 when b[1] == 254 => false,            // 169.254.0.0/16 link-local (cloud metadata)
                172 when b[1] is >= 16 and <= 31 => false,  // 172.16.0.0/12 private
                192 when b[1] == 168 => false,            // 192.168.0.0/16 private
                192 when b[1] == 0 && b[2] == 0 => false, // 192.0.0.0/24 IETF protocol assignments
                198 when b[1] is 18 or 19 => false,       // 198.18.0.0/15 benchmarking
                255 => false,                             // 255.x broadcast / reserved
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6UniqueLocal
                || address.IsIPv6Multicast
                || address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6Loopback))
            {
                return false;
            }
            return true;
        }

        return false;
    }
}
