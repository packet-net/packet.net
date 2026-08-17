using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Radios;
using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Node.Core.Tuning;

/// <summary>
/// The production <see cref="ITuningPortGateway"/>: the live node host. Port lookup is the
/// supervisor's running-port map; restore is <c>PortSupervisor.RestartPortAsync</c> under
/// <c>NodeHostedService.RunExclusiveAsync</c>, so it serialises against config reconciles.
/// </summary>
internal sealed class NodeHostTuningGateway : ITuningPortGateway
{
    private readonly NodeHostedService host;

    private NodeHostTuningGateway(NodeHostedService host) => this.host = host;

    /// <summary>Wrap a node host (null-checked as the service's own <c>host</c> argument).</summary>
    internal static NodeHostTuningGateway For(NodeHostedService host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new NodeHostTuningGateway(host);
    }

    /// <inheritdoc/>
    public ITuningPortHandle? GetPort(string portId)
    {
        var running = host.Supervisor?.GetPort(portId);
        return running is null ? null : new RunningPortHandle(host, running);
    }

    /// <inheritdoc/>
    public Task RestartAsync(string portId, CancellationToken cancellationToken) =>
        host.RunExclusiveAsync(
            () => host.Supervisor is { } sup ? sup.RestartPortAsync(portId, cancellationToken) : Task.FromResult(false),
            cancellationToken);
}

/// <summary>A live <see cref="RunningPort"/> as the tuning arm path sees it.</summary>
internal sealed class RunningPortHandle : ITuningPortHandle
{
    private readonly NodeHostedService host;
    private readonly RunningPort running;
    private readonly TaitCcdiRadio? tait;
    private readonly ITuningRadio? radio;

    /// <summary>Wrap one running port; resolves its live Tait driver once, for this arm.</summary>
    internal RunningPortHandle(NodeHostedService host, RunningPort running)
    {
        this.host = host;
        this.running = running;
        // Resolve the LIVE driver: a head-end-bound radio sits behind the reconnect facade
        // (#576), so the concrete Tait handle must be re-resolved per operation, never cached
        // beyond it - this handle lives for one arm only.
        tait = RadioControls.LiveTait(running.Radio);
        radio = tait is null ? null : new TaitTuningRadio(tait);
    }

    /// <inheritdoc/>
    public string PortId => running.Id;

    /// <inheritdoc/>
    public bool HasNinoTnc => running.NinoTnc is not null;

    /// <inheritdoc/>
    public ITuningRadio? Radio => radio;

    /// <inheritdoc/>
    public NinoTncSerialPort? Tnc => running.NinoTnc;

    /// <inheritdoc/>
    public TaitCcdiRadio? Tait => tait;

    /// <inheritdoc/>
    public Task PauseAsync(CancellationToken cancellationToken) =>
        host.RunExclusiveAsync(
            async () =>
            {
                // Tell the supervisor this stop is DELIBERATE before making it: the
                // running-state watchdog would otherwise read the stopped listener as a port
                // that died on the air and restart it underneath the tuning session (#722).
                // The suspension clears on the next teardown / bring-up, which is exactly what
                // restore does (RestartPortAsync).
                host.Supervisor?.SuspendSupervision(running.Id);
                await running.Listener.StopAsync().ConfigureAwait(false);
            },
            cancellationToken);
}

/// <summary>The production <see cref="ITuningRadio"/>: a live Tait CCDI driver.</summary>
internal sealed class TaitTuningRadio(TaitCcdiRadio radio) : ITuningRadio
{
    /// <inheritdoc/>
    public Task SetProgressMessagesAsync(bool enable, CancellationToken cancellationToken) =>
        radio.SetProgressMessagesAsync(enable, cancellationToken);

    /// <inheritdoc/>
    public Task SendSdmAsync(string dataMessageId, string message, CancellationToken cancellationToken) =>
        radio.SendSdmAsync(dataMessageId, message, leadInDelay: null, cancellationToken);
}

/// <summary>The production <see cref="ITuningLinkFactory"/>: an <see cref="SdmTuningLink"/> over the
/// port's live Tait radio.</summary>
internal sealed class SdmTuningLinkFactory : ITuningLinkFactory
{
    /// <summary>The shared stateless instance.</summary>
    internal static SdmTuningLinkFactory Instance { get; } = new();

    /// <inheritdoc/>
    public ITuningLink Create(ITuningPortHandle port, string peerSdmId, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(port);
        var link = SdmTuningLink.Create(port.Tait!, peerSdmId);
        link.Log = log;
        return link;
    }
}
