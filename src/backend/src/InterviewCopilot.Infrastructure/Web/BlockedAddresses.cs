using System.Net;
using System.Net.Sockets;

namespace InterviewCopilot.Infrastructure.Web;

/// <summary>
/// SSRF guard: addresses a user-supplied link must never reach (loopback, private, link-local, CGNAT,
/// multicast, reserved, unspecified). IPv6 forms that embed an IPv4 address follow the IPv4 rules.
/// </summary>
internal static class BlockedAddresses
{
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsBlockedV4(address.GetAddressBytes());
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return true; // unknown family: refuse
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlockedV4(address.MapToIPv4().GetAddressBytes());
        }

        return IsBlockedV6(address.GetAddressBytes());
    }

    private static bool IsBlockedV4(byte[] b) =>
        b[0] == 0                                        // 0.0.0.0/8
        || b[0] == 10                                    // 10.0.0.0/8
        || (b[0] == 100 && (b[1] & 0xC0) == 64)          // 100.64.0.0/10
        || b[0] == 127                                   // 127.0.0.0/8
        || (b[0] == 169 && b[1] == 254)                  // 169.254.0.0/16
        || (b[0] == 172 && (b[1] & 0xF0) == 16)          // 172.16.0.0/12
        || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // 192.0.0.0/24
        || (b[0] == 192 && b[1] == 168)                  // 192.168.0.0/16
        || (b[0] == 198 && (b[1] & 0xFE) == 18)          // 198.18.0.0/15
        || b[0] >= 224;                                  // 224.0.0.0/4 multicast and 240.0.0.0/4 reserved

    private static bool IsBlockedV6(byte[] b)
    {
        if ((b[0] & 0xFE) == 0xFC)                       // fc00::/7 unique local
        {
            return true;
        }

        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)       // fe80::/10 link-local
        {
            return true;
        }

        if (b[0] == 0xFF)                                // ff00::/8 multicast
        {
            return true;
        }

        if (b.Take(12).All(x => x == 0))                 // ::/96 covers ::, ::1 and deprecated IPv4-compatible forms
        {
            return true;
        }

        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].All(x => x == 0))
        {
            return IsBlockedV4(b[12..16]);               // 64:ff9b::/96 NAT64 embeds an IPv4 address
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsBlockedV4(b[2..6]);                 // 2002::/16 6to4 embeds an IPv4 address
        }

        return false;
    }
}
