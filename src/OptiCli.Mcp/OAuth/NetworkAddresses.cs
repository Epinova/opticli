using System.Net;
using System.Net.Sockets;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// Which addresses the site may fetch a client metadata document from: public ones only. A <c>client_id</c> URL is
/// chosen by whoever starts a sign-in, so without this it could make the site fetch from its own network (cloud metadata
/// services, internal admin pages).
/// </summary>
internal static class NetworkAddresses
{
    /// <summary>IPv4 ranges that aren't the public internet: this network, private, CGNAT, loopback, link-local, and more.</summary>
    private static readonly IPNetwork[] PrivateV4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
    ];

    /// <summary>IPv6 ranges that aren't the public internet: unspecified, loopback, unique-local, link-local, site-local, multicast, documentation.</summary>
    private static readonly IPNetwork[] PrivateV6 =
    [
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fec0::/10"),
        IPNetwork.Parse("ff00::/8"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("100::/64"),
    ];

    /// <summary>IPv6 forms that carry an IPv4 address, which is then what counts: NAT64, 6to4.</summary>
    private static readonly IPNetwork Nat64 = IPNetwork.Parse("64:ff9b::/96");
    private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");
    private static readonly IPNetwork Teredo = IPNetwork.Parse("2001::/32");

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !PrivateV4.Any(n => n.Contains(address));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || PrivateV6.Any(n => n.Contains(address)) || Teredo.Contains(address))
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        if (Nat64.Contains(address))
        {
            return IsPublic(new IPAddress(bytes[12..16]));
        }
        if (SixToFour.Contains(address))
        {
            return IsPublic(new IPAddress(bytes[2..6]));
        }
        // ::a.b.c.d, the deprecated IPv4-compatible form.
        if (bytes[..12].All(b => b == 0))
        {
            return false;
        }
        return true;
    }
}
