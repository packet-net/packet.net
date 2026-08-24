using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios.Programming;

/// <summary>
/// The node-host operations a programming run needs: the port's config, the device path of its
/// live radio, and "take the port out of service, run this, put it back". Production is
/// <see cref="NodeHostProgrammingGateway"/> over <see cref="NodeHostedService"/> +
/// <see cref="PortSupervisor"/>; the service's internal constructor takes this instead (test seam,
/// InternalsVisibleTo <c>Packet.Node.Tests</c>) so the orchestration - and every failure path that
/// must put the port back - is drivable without a supervisor, a listener or a radio.
/// </summary>
internal interface ITaitProgrammingGateway
{
    /// <summary>The port's live config baseline, or null when the id is unknown.</summary>
    PortConfig? GetPortConfig(string portId);

    /// <summary>
    /// The serial device this port's <b>open</b> Tait CCDI radio is on, or null when the port is not
    /// running, has no radio attached, or its radio is not a locally-cabled Tait. Read while the
    /// port is still up: it is the exact answer, and it costs nothing, where resolving a
    /// serial-bound radio afterwards costs a bus scan.
    /// </summary>
    string? LiveRadioDevicePath(string portId);

    /// <summary>Resolve a radio block to the device path to program, by scanning for its CCDI
    /// serial. Only valid once the port is down - the scan opens candidate ports, so a device the
    /// node still holds open cannot be found.</summary>
    Task<string> ResolveDevicePathAsync(PortRadioConfig radio, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the radio on <paramref name="devicePath"/> is answering CCDI right now: one identity
    /// query at the radio's CONFIGURED control baud (not the 19200 the programming handshake runs
    /// at), which is the exact question the port's radio bring-up is about to ask. Never throws for
    /// a no.
    /// </summary>
    Task<bool> ProbeRadioAsync(PortRadioConfig radio, string devicePath, CancellationToken cancellationToken);

    /// <summary>
    /// Take the port out of service, run <paramref name="work"/>, and bring the port back - on
    /// every exit path, including a throw from <paramref name="work"/> and a cancelled run. The
    /// restore is <b>always</b> attempted; if it is the thing that fails, that failure is reported
    /// (so the run never reads "done" over a port that is still down) unless an earlier one has
    /// already claimed the run.
    /// </summary>
    Task RunWithPortDownAsync(string portId, Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}

/// <summary>
/// The production <see cref="ITaitProgrammingGateway"/>: the live node host. Down is
/// <c>PortSupervisor.StopPortAsync</c> and back up is <c>PortSupervisor.RestartPortAsync</c>, both
/// under <c>NodeHostedService.RunExclusiveAsync</c> so they serialise against config reconciles.
/// </summary>
/// <remarks>
/// The exclusive gate is held for the two transitions only, not across the whole run: a programming
/// run lasts minutes, and holding the node's config gate for that long would hang every other
/// config write behind it. Nothing can steal the radio's serial device in between - the programmer
/// holds it open exclusively, so a reconcile that tries to rebuild the port mid-run fails to open
/// it and degrades that port, which the restore at the end then fixes.
/// </remarks>
internal sealed partial class NodeHostProgrammingGateway : ITaitProgrammingGateway
{
    private readonly NodeHostedService host;
    private readonly ILogger logger;

    private NodeHostProgrammingGateway(NodeHostedService host, ILogger logger)
    {
        this.host = host;
        this.logger = logger;
    }

    /// <summary>Wrap a node host (null-checked as the service's own <c>host</c> argument).</summary>
    internal static NodeHostProgrammingGateway For(NodeHostedService host, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logger);
        return new NodeHostProgrammingGateway(host, logger);
    }

    /// <inheritdoc/>
    public PortConfig? GetPortConfig(string portId) => host.Supervisor?.GetPortConfig(portId);

    /// <inheritdoc/>
    public string? LiveRadioDevicePath(string portId)
    {
        var running = host.Supervisor?.GetPort(portId);
        // A head-end-bound radio sits behind the reconnect facade, so resolve the concrete driver
        // rather than caching one; its PortName is a host:port pipe name, which the caller has
        // already refused by then (programming is a locally-cabled operation).
        return RadioControls.LiveTait(running?.Radio)?.PortName;
    }

    /// <inheritdoc/>
    public Task<string> ResolveDevicePathAsync(PortRadioConfig radio, CancellationToken cancellationToken) =>
        Resolve(radio, cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> ProbeRadioAsync(
        PortRadioConfig radio, string devicePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(radio);
        return await TaitRadioPortDiscovery.ProbeAsync(devicePath, radio.Baud, cancellationToken)
            .ConfigureAwait(false) is not null;
    }

    private static async Task<string> Resolve(PortRadioConfig radio, CancellationToken cancellationToken)
    {
        var (path, _) = await TaitEndpointResolver.ResolveAsync(radio, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <inheritdoc/>
    public async Task RunWithPortDownAsync(
        string portId, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        ExceptionDispatchInfo? failure = null;
        try
        {
            // Inside the try: a teardown that throws part-way leaves the port down, and the restore
            // below is what puts it back. Stopping a port that was never running, or an unknown
            // one, is a no-op either way.
            await host.RunExclusiveAsync(
                () => host.Supervisor is { } sup
                    ? sup.StopPortAsync(portId, cancellationToken)
                    : Task.FromResult(false),
                cancellationToken).ConfigureAwait(false);
            await work(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Held, not thrown yet: the port has to come back first, and the reason the run failed
            // must survive whatever happens while it does.
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            // Never cancellable: a cancelled run must still put the port back.
            await host.RunExclusiveAsync(
                () => host.Supervisor is { } sup
                    ? sup.RestartPortAsync(portId, CancellationToken.None)
                    : Task.FromResult(false),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRestoreFailed(logger, portId, ex);
            // The operator needs to be told why their programming RUN failed, not why the tidy-up
            // afterwards did - so an earlier failure always wins. With no earlier failure the radio
            // was programmed fine but the port is down, which is worth failing the run over: the
            // port is not back, and saying "done" would be a lie.
            failure ??= ExceptionDispatchInfo.Capture(ex);
        }

        failure?.Throw();
    }

    [LoggerMessage(EventId = 7792, Level = LogLevel.Error,
        Message = "port {PortId}: could not bring the port back after a codeplug programming run")]
    private static partial void LogRestoreFailed(ILogger logger, string portId, Exception exception);
}
