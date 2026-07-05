using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeroconf;

namespace Packet.Node.Core.HeadEnd;

/// <summary>
/// The production <see cref="IHeadEndDiscovery"/>: a thin wrapper over the <c>Zeroconf</c> resolver
/// that browses <c>_pdnhead._tcp</c> once and projects each responder into a
/// <see cref="DiscoveredHeadEnd"/>. Zeroconf binds the mDNS port with address-reuse, so it coexists
/// with the <c>avahi-daemon</c> the node's own advertiser (<see cref="Discovery.MdnsAdvertiserHostedService"/>)
/// registers through. All the logic beyond "browse and read the TXT" lives above this seam.
/// </summary>
/// <remarks>
/// <para>
/// The head-end advertises TXT <c>instance=</c> (the authoritative stable id), optional
/// <c>httpport=</c>, and <c>v=1</c>; the SRV port is the HTTP API. The instance id is taken from the
/// TXT value regardless of the DNS-SD label — a concurrent daemon change may echo the id into the
/// label too, but the TXT is authoritative. A responder with no usable <c>instance=</c> or no
/// address is skipped (it cannot be keyed or dialled).
/// </para>
/// <para>
/// Total by contract: any resolver fault (no multicast route, no responder, a malformed record)
/// logs at debug and yields an empty list. Discovery is a convenience — the manual
/// <see cref="Configuration.HeadEndConfig.Address"/> always works without it.
/// </para>
/// </remarks>
public sealed partial class MdnsHeadEndDiscovery : IHeadEndDiscovery
{
    /// <summary>The DNS-SD service type split-station head-ends advertise.</summary>
    public const string ServiceType = "_pdnhead._tcp.local.";

    private readonly ILogger<MdnsHeadEndDiscovery> logger;

    /// <summary>Build the discovery. <paramref name="loggerFactory"/> is optional (null ⇒ no logs).</summary>
    public MdnsHeadEndDiscovery(ILoggerFactory? loggerFactory = null)
    {
        logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<MdnsHeadEndDiscovery>();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DiscoveredHeadEnd>> DiscoverAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var scanTime = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(2);
        IReadOnlyList<IZeroconfHost> hosts;
        try
        {
            hosts = await ZeroconfResolver
                .ResolveAsync(ServiceType, scanTime, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No multicast route / no responder / malformed record: discovery is best-effort. The
            // manual address path is unaffected, so this is a debug note, not a warning.
            LogBrowseFailed(ex, ServiceType);
            return [];
        }

        var found = new List<DiscoveredHeadEnd>(hosts.Count);
        foreach (var host in hosts)
        {
            if (!TryProject(host, out var discovered))
            {
                continue;
            }
            found.Add(discovered);
        }
        return found;
    }

    // Project one Zeroconf host into a DiscoveredHeadEnd, or skip it (false) when it can't be keyed
    // or dialled. The instance id is the TXT `instance=` value (authoritative); the port is the TXT
    // `httpport=` when present and valid, else the SRV port.
    private static bool TryProject(IZeroconfHost host, out DiscoveredHeadEnd discovered)
    {
        discovered = null!;

        var service = host.Services.Values.FirstOrDefault(
            s => s.Name.Contains("_pdnhead._tcp", StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            return false;
        }

        var instance = LookupTxt(service, "instance");
        if (string.IsNullOrWhiteSpace(instance))
        {
            return false; // no authoritative id ⇒ nothing to key the binding by
        }

        var address = host.IPAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false; // no A/AAAA ⇒ nothing to dial
        }

        // TXT httpport wins when present + valid; otherwise the SRV port is the HTTP API.
        int port = service.Port;
        var txtPort = LookupTxt(service, "httpport");
        if (!string.IsNullOrWhiteSpace(txtPort)
            && int.TryParse(txtPort, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 0 and <= 65535)
        {
            port = parsed;
        }

        if (port is <= 0 or > 65535)
        {
            return false;
        }

        discovered = new DiscoveredHeadEnd(instance.Trim(), address, port);
        return true;
    }

    // A DNS-SD service carries its TXT record as one or more property maps; read a key from the
    // first map that has it (case-insensitively — some responders upper-case TXT keys).
    private static string? LookupTxt(IService service, string key)
    {
        foreach (var map in service.Properties)
        {
            foreach (var kvp in map)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }
        }
        return null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "mDNS head-end browse of {ServiceType} failed; treating as no discoveries.")]
    private partial void LogBrowseFailed(Exception ex, string serviceType);
}
