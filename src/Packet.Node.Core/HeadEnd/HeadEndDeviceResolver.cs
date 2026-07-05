using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.HeadEnd;

/// <summary>
/// Resolves a port's <c>(headEndId, deviceId)</c> binding to a concrete, dial-able endpoint at
/// bring-up: <c>headEndId → address</c> (from <see cref="NodeConfig.HeadEnds"/>), then
/// <c>GET /inventory</c> on that head-end, then <c>deviceId → tcpPort + baud</c>. This is the seam
/// the radio / transport factories call for their head-end branches; keying by
/// <c>(instanceId, deviceId)</c> — never <c>host:port</c> — is what lets a re-addressed head-end keep
/// its port configs (the address is looked up fresh each resolve).
/// </summary>
/// <remarks>
/// Stage 3a resolved <c>headEndId → address</c> from the manual <see cref="HeadEndConfig.Address"/>
/// only. Stage 3b threads an optional <see cref="IHeadEndAddressResolver"/> through the same shape:
/// when supplied, the address step prefers the config address and falls back to an mDNS browse of
/// the instance id, so a head-end configured in discover mode (blank address) — or one that changed
/// address — resolves to its current endpoint at bring-up. The <see cref="HeadEndClient"/> factory
/// takes the <em>resolved</em> base <see cref="Uri"/> and is injectable so tests point resolution at
/// a stub inventory server.
/// </remarks>
public sealed class HeadEndDeviceResolver
{
    private readonly IReadOnlyList<HeadEndConfig> headEnds;
    private readonly Func<Uri, HeadEndClient> clientFactory;
    private readonly IHeadEndAddressResolver? addressResolver;

    /// <summary>Build a resolver over the configured head-end fleet. A null
    /// <paramref name="clientFactory"/> builds a real <see cref="HeadEndClient"/> for the resolved
    /// base address. When <paramref name="addressResolver"/> is supplied the <c>headEndId → address</c>
    /// step consults it (config-then-mDNS); when null, only a manual <see cref="HeadEndConfig.Address"/>
    /// is used (today's behaviour, for a node with no discovery wired).</summary>
    public HeadEndDeviceResolver(
        IReadOnlyList<HeadEndConfig> headEnds,
        Func<Uri, HeadEndClient>? clientFactory = null,
        IHeadEndAddressResolver? addressResolver = null)
    {
        this.headEnds = headEnds ?? [];
        this.clientFactory = clientFactory ?? DefaultClient;
        this.addressResolver = addressResolver;
    }

    /// <summary>
    /// Resolve <paramref name="headEndId"/> + <paramref name="deviceId"/> to the raw-pipe host / TCP
    /// port / baud, plus the <see cref="HeadEndClient"/> the caller wires <c>setBaud</c> to. Throws
    /// <see cref="InvalidOperationException"/> for an unknown head-end id, a head-end whose address
    /// resolves to nothing (no manual address and no unambiguous mDNS hit), or a device the head-end's
    /// inventory doesn't list — the port supervisor treats any throw as a clean transport/radio-open
    /// failure (log + degrade / retry).
    /// </summary>
    public async Task<HeadEndDeviceBinding> ResolveAsync(
        string headEndId, string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headEndId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var headEnd = headEnds.FirstOrDefault(h => string.Equals(h.Id, headEndId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"no head-end with id '{headEndId}' is configured.");

        var baseAddress = await ResolveBaseAddressAsync(headEnd, cancellationToken).ConfigureAwait(false);

        var client = clientFactory(baseAddress);
        var inventory = await client.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var device = inventory.Ports.FirstOrDefault(p => string.Equals(p.Id, deviceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"head-end '{headEndId}' inventory has no device '{deviceId}' " +
                $"(known: {(inventory.Ports.Count == 0 ? "<none>" : string.Join(", ", inventory.Ports.Select(p => p.Id)))}).");

        return new HeadEndDeviceBinding(client, client.BaseAddress.Host, device.TcpPort, device.Baud, deviceId);
    }

    // The headEndId → base address step. With a discovery-backed resolver: config address, else an
    // mDNS browse of the instance id (a duplicate-id conflict resolves to null → a clear throw).
    // Without one: a manual address is required (unchanged from Stage 3a).
    private async Task<Uri> ResolveBaseAddressAsync(HeadEndConfig headEnd, CancellationToken cancellationToken)
    {
        if (addressResolver is not null)
        {
            return await addressResolver.ResolveBaseAddressAsync(headEnd.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"head-end '{headEnd.Id}' has no resolvable address — its config address is blank and mDNS " +
                    "discovery returned no unambiguous match (unknown instance, or a duplicate-id conflict).");
        }

        if (string.IsNullOrWhiteSpace(headEnd.Address))
        {
            throw new InvalidOperationException(
                $"head-end '{headEnd.Id}' has no address — a manual host:port is required when no mDNS " +
                "discovery is wired.");
        }

        return HeadEndClient.BaseAddressFor(headEnd.Address);
    }

    private static HeadEndClient DefaultClient(Uri baseAddress) => new(baseAddress);
}

/// <summary>
/// The result of resolving a <c>(headEndId, deviceId)</c> binding: the raw-pipe <see cref="Host"/> +
/// <see cref="TcpPort"/> to dial, the <see cref="Baud"/> the head-end clocks the UART at, and the
/// <see cref="HeadEndClient"/> the <see cref="SetBaud"/> line-control seam routes through.
/// </summary>
public sealed record HeadEndDeviceBinding(HeadEndClient Client, string Host, int TcpPort, int Baud, string DeviceId)
{
    /// <summary>The Stage-1 <c>setBaud</c> callback for this device: routes a re-clock to the
    /// head-end's <c>POST /ports/{deviceId}/line</c> verb (the data socket cannot carry it).</summary>
    public Func<int, CancellationToken, Task> SetBaud =>
        (baud, cancellationToken) => Client.SetLineAsync(DeviceId, baud, cancellationToken: cancellationToken);
}
