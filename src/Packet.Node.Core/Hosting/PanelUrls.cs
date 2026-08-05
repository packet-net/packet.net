using System.Net;
using System.Net.Sockets;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// Derives the concrete <c>http://...</c> URLs the control panel answers on, for the
/// startup log: <c>journalctl -u packetnet</c> is the operator's first stop on a headless
/// box, and "0.0.0.0:8080" is not an address anyone can browse to. Pure (no NIC
/// enumeration): the caller supplies the machine's addresses, so the wildcard expansion is
/// unit-testable. An unparseable bind mirrors Kestrel's fallback in Program.cs (loopback),
/// so the log never names an address the listener did not bind.
/// </summary>
public static class PanelUrls
{
    public static IReadOnlyList<string> For(string? bind, int port, IReadOnlyList<IPAddress> machineAddresses)
    {
        var address = IPAddress.TryParse(bind?.Trim() ?? "", out var ip) ? ip : IPAddress.Loopback;

        if (!Equals(address, IPAddress.Any) && !Equals(address, IPAddress.IPv6Any))
        {
            return [Url(address, port)];
        }

        // Wildcard: name addresses another machine can actually reach. Loopback and
        // link-local (169.254/16, fe80::/10) resolve nowhere useful off-box. IPv4 first;
        // globally-routable IPv6 only when the box has no IPv4 at all, since an operator
        // faced with both will type the v4 one. (An IPv6-any listener accepts v4 too,
        // dual-mode.)
        var v4 = machineAddresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(a)
                        && !IsLinkLocalV4(a))
            .Select(a => Url(a, port))
            .Distinct()
            .ToList();
        if (v4.Count > 0)
        {
            return v4;
        }

        var v6 = machineAddresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6
                        && !IPAddress.IsLoopback(a)
                        && !a.IsIPv6LinkLocal)
            .Select(a => Url(a, port))
            .Distinct()
            .ToList();
        return v6.Count > 0 ? v6 : [Url(IPAddress.Loopback, port)];
    }

    private static bool IsLinkLocalV4(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    private static string Url(IPAddress a, int port) =>
        a.AddressFamily == AddressFamily.InterNetworkV6
            ? $"http://[{a}]:{port}"
            : $"http://{a}:{port}";
}
