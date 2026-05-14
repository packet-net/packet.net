using Packet.Kiss.NinoTnc;

namespace Packet.NinoTnc.Spike;

internal static class DiscoveryProbe
{
    public static int Run()
    {
        // Make sure the env-var override isn't in play for the probe.
        Environment.SetEnvironmentVariable("PACKETNET_NINOTNC_PORTS", null);
        var candidates = NinoTncPortDiscovery.EnumerateCandidates();
        Console.WriteLine($"NinoTncPortDiscovery returned {candidates.Count} candidate(s):");
        foreach (var c in candidates)
        {
            Console.WriteLine($"  {c.PortName}  ({c.ResolvedDevicePath ?? "no resolved path"})");
        }
        return 0;
    }
}
