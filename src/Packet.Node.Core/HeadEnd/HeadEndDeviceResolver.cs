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
/// Stage 3a resolves <c>headEndId → address</c> from the manual <see cref="HeadEndConfig.Address"/>.
/// Stage 3b swaps in mDNS: the same resolver shape, but the address comes from a browse of the
/// instance id rather than static config. The <see cref="HeadEndClient"/> factory is injectable so
/// tests point resolution at a stub inventory server.
/// </remarks>
public sealed class HeadEndDeviceResolver
{
    private readonly IReadOnlyList<HeadEndConfig> headEnds;
    private readonly Func<HeadEndConfig, HeadEndClient> clientFactory;

    /// <summary>Build a resolver over the configured head-end fleet. A null
    /// <paramref name="clientFactory"/> builds a real <see cref="HeadEndClient"/> from each head-end's
    /// manual address.</summary>
    public HeadEndDeviceResolver(
        IReadOnlyList<HeadEndConfig> headEnds,
        Func<HeadEndConfig, HeadEndClient>? clientFactory = null)
    {
        this.headEnds = headEnds ?? [];
        this.clientFactory = clientFactory ?? DefaultClient;
    }

    /// <summary>
    /// Resolve <paramref name="headEndId"/> + <paramref name="deviceId"/> to the raw-pipe host / TCP
    /// port / baud, plus the <see cref="HeadEndClient"/> the caller wires <c>setBaud</c> to. Throws
    /// <see cref="InvalidOperationException"/> for an unknown head-end id, a head-end with no address
    /// (Stage 3a needs a manual one), or a device the head-end's inventory doesn't list — the port
    /// supervisor treats any throw as a clean transport/radio-open failure (log + degrade / retry).
    /// </summary>
    public async Task<HeadEndDeviceBinding> ResolveAsync(
        string headEndId, string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headEndId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var headEnd = headEnds.FirstOrDefault(h => string.Equals(h.Id, headEndId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"no head-end with id '{headEndId}' is configured.");

        if (string.IsNullOrWhiteSpace(headEnd.Address))
        {
            throw new InvalidOperationException(
                $"head-end '{headEndId}' has no address — a manual host:port is required in Stage 3a " +
                "(mDNS resolution of the instance id lands in Stage 3b).");
        }

        var client = clientFactory(headEnd);
        var inventory = await client.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var device = inventory.Ports.FirstOrDefault(p => string.Equals(p.Id, deviceId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"head-end '{headEndId}' inventory has no device '{deviceId}' " +
                $"(known: {(inventory.Ports.Count == 0 ? "<none>" : string.Join(", ", inventory.Ports.Select(p => p.Id)))}).");

        return new HeadEndDeviceBinding(client, client.BaseAddress.Host, device.TcpPort, device.Baud, deviceId);
    }

    private static HeadEndClient DefaultClient(HeadEndConfig headEnd) =>
        new(HeadEndClient.BaseAddressFor(headEnd.Address));
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
