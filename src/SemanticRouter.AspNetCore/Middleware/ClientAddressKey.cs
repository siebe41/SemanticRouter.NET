using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The key a client is counted under by address (failed sign-ins, rate limits): an IPv4 address as it is, and an IPv6
/// address by the /64 network it is in (<c>2001:db8:1:2::/64</c>), since one site or device is routinely given a whole
/// /64 and could otherwise take a fresh address, and a fresh allowance, for every request.
/// </summary>
internal static class ClientAddressKey
{
    /// <summary>The key for a request without a remote address (an in-process test server).</summary>
    public const string Local = "local";

    /// <summary>The key for <paramref name="context"/>'s remote address.</summary>
    public static string KeyOf(HttpContext context) => KeyOf(context.Connection.RemoteIpAddress);

    /// <summary>The key for <paramref name="address"/>; IPv4 addresses mapped to IPv6 count as IPv4.</summary>
    public static string KeyOf(IPAddress? address)
    {
        if (address is null)
        {
            return Local;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(address))
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes) + "/64";
    }
}
