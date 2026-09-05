using Packet.Kiss.NinoTnc;
using M0LTE.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Node.Core.Tuning;

/// <summary>
/// The node-host operations <see cref="PortTuningService"/>'s arm path needs: look a running port
/// up, and restore (rebuild) one. Production is <see cref="NodeHostTuningGateway"/> over
/// <see cref="Hosting.NodeHostedService"/> + <see cref="Hosting.PortSupervisor"/>; the service's
/// internal constructor takes this instead (test seam, InternalsVisibleTo
/// <c>Packet.Node.Tests</c>) so the whole pause -> SDM -> start -> restore orchestration, and every
/// failure path that must put the port back, is drivable without a supervisor, a listener or a
/// radio.
/// </summary>
internal interface ITuningPortGateway
{
    /// <summary>The running port with this id, or <c>null</c> when it is unknown, disabled, faulted
    /// or mid-restart (the arm path maps that to <see cref="TuningStartError.NotFound"/>).</summary>
    ITuningPortHandle? GetPort(string portId);

    /// <summary>
    /// Restore a port to normal service: a full in-place teardown + bring-up under the host's
    /// exclusive gate, the definitive guarantee that nothing is left paused, wedged or keyed.
    /// Exceptions propagate to the caller, which logs and swallows them (a restore failure must
    /// never mask the error that caused the restore).
    /// </summary>
    Task RestartAsync(string portId, CancellationToken cancellationToken);
}

/// <summary>
/// One running port, as the tuning arm path sees it. The orchestration itself only uses
/// <see cref="HasNinoTnc"/>, <see cref="Radio"/> and <see cref="PauseAsync"/>; the concrete
/// <see cref="Tnc"/> / <see cref="Tait"/> drivers are what the production session builders bind
/// their stimulus / meter / station to (a fake handle leaves those null and supplies its own
/// session factory).
/// </summary>
internal interface ITuningPortHandle
{
    /// <summary>The port's id (the tuning registry key).</summary>
    string PortId { get; }

    /// <summary>Whether this port's modem is a NinoTNC - the bursts and metering are NinoTNC
    /// operations, so a session cannot arm without one (preflight).</summary>
    bool HasNinoTnc { get; }

    /// <summary>The port's radio as the arm path uses it (PROGRESS + the SDM probe), or
    /// <c>null</c> when the port has no Tait CCDI radio attached (preflight refuses).</summary>
    ITuningRadio? Radio { get; }

    /// <summary>The NinoTNC serial port the session's stimulus / meter / station drive, or
    /// <c>null</c> when there is none.</summary>
    NinoTncSerialPort? Tnc { get; }

    /// <summary>The live Tait CCDI driver the session's meter/station read RSSI and carrier from,
    /// and the SDM link rides on; <c>null</c> when the port has no Tait radio.</summary>
    TaitCcdiRadio? Tait { get; }

    /// <summary>Pause the port's normal AX.25 traffic (stop its listener) under the host's
    /// exclusive gate. The modem serial port stays open so the session can key bursts and
    /// meter.</summary>
    Task PauseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The radio operations the arm path performs before a session exists: enable PROGRESS (the SDM
/// link's DCD and delivery receipts ride on it) and transmit the wildcard probe SDM that says
/// whether the radio's programming has short data messages enabled at all.
/// </summary>
internal interface ITuningRadio
{
    /// <summary>FUNCTION 0/4: enable (or disable) unsolicited PROGRESS output.</summary>
    Task SetProgressMessagesAsync(bool enable, CancellationToken cancellationToken);

    /// <summary>SEND_ADAPTABLE_SDM: transmit a short data message (the arm path sends a wildcard
    /// probe). A radio with SDM disabled in its programming rejects it with ERROR 0/06.</summary>
    Task SendSdmAsync(string dataMessageId, string message, CancellationToken cancellationToken);
}

/// <summary>Opens the SDM coordination link to the peer for a port. Production wraps the static
/// <see cref="SdmTuningLink.Create(TaitCcdiRadio, string, SdmTuningLinkOptions?, bool, TimeProvider?)"/>;
/// a test supplies an in-memory link.</summary>
internal interface ITuningLinkFactory
{
    /// <summary>Open the link to <paramref name="peerSdmId"/> over <paramref name="port"/>'s radio,
    /// with <paramref name="log"/> attached as the link's diagnostic sink.</summary>
    ITuningLink Create(ITuningPortHandle port, string peerSdmId, Action<string> log);
}
