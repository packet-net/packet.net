using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Node.Core.Tuning;

/// <summary>This port's role in a deviation-tuning session.</summary>
public enum TuningRole
{
    /// <summary>The end where the operator turns the TX-DEV pot: it transmits the frame bursts the
    /// meter requests. Rounds advance when the operator signals "I've adjusted the pot"
    /// (<c>POST .../tuning/next</c>).</summary>
    Tuned,

    /// <summary>The end that measures a remote peer's bursts and computes the deviation advice.
    /// Rounds are driven by the remote tuned peer's ready beacons; there is no local pot to adjust,
    /// so <c>.../tuning/next</c> does not apply.</summary>
    Meter,
}

/// <summary>The lifecycle state of a <see cref="PortTuningSession"/>.</summary>
public enum TuningSessionState
{
    /// <summary>The port is paused for tuning and the coordination link is up, waiting for the peer.</summary>
    Armed,

    /// <summary>The peer has been seen on the coordination link; measurement rounds are flowing.</summary>
    PeerConnected,

    /// <summary>(Tuned role) A round finished; waiting for the operator to adjust the pot and signal
    /// the next round.</summary>
    AwaitingAdjustment,

    /// <summary>The session finished cleanly (a goodbye was exchanged).</summary>
    Ended,

    /// <summary>The session ended because the coordination link failed.</summary>
    Error,

    /// <summary>The session was stopped by the operator (or node shutdown).</summary>
    Stopped,
}

/// <summary>
/// Pure, hardware-free preconditions for arming a tuning session on a port — the fail-fast checks
/// the API runs before pausing the port or keying anything. Kept a separate pure function so the
/// validation messages are unit-testable without a live <c>RunningPort</c>. The <b>SDM-enabled</b>
/// check (does the radio's programming allow short data messages) is <em>not</em> here: the only
/// way to determine it is to transmit a probe SDM, so it lives in <see cref="PortTuningService"/>
/// on the (already admin-scoped, audited, port-paused) start path, mirroring the capability
/// doctor's SDM probe.
/// </summary>
public static class TuningPreflight
{
    /// <summary>The exact SDM data-identity length a peer id must be (8 characters).</summary>
    public const int PeerSdmIdLength = TaitSdmSideChannel.IdentityLength;

    /// <summary>Parse the wire role token (<c>tuned</c>/<c>meter</c>) case-insensitively.</summary>
    /// <returns><c>true</c> and the parsed <paramref name="role"/> when recognised.</returns>
    public static bool TryParseRole(string? wire, out TuningRole role)
    {
        if (string.Equals(wire, TuningSession.TunedRole, StringComparison.OrdinalIgnoreCase))
        {
            role = TuningRole.Tuned;
            return true;
        }
        if (string.Equals(wire, TuningSession.MeterRole, StringComparison.OrdinalIgnoreCase))
        {
            role = TuningRole.Meter;
            return true;
        }
        role = default;
        return false;
    }

    /// <summary>The wire token (<c>tuned</c>/<c>meter</c>) for a role.</summary>
    public static string RoleToWire(TuningRole role) => role switch
    {
        TuningRole.Tuned => TuningSession.TunedRole,
        TuningRole.Meter => TuningSession.MeterRole,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "unknown tuning role"),
    };

    /// <summary>The wire token for a lifecycle state.</summary>
    public static string StateToWire(TuningSessionState state) => state switch
    {
        TuningSessionState.Armed => "armed",
        TuningSessionState.PeerConnected => "peer-connected",
        TuningSessionState.AwaitingAdjustment => "awaiting-adjustment",
        TuningSessionState.Ended => "ended",
        TuningSessionState.Error => "error",
        TuningSessionState.Stopped => "stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown tuning state"),
    };

    /// <summary>Whether a state is terminal (the session has finished — no further events).</summary>
    public static bool IsTerminal(TuningSessionState state) =>
        state is TuningSessionState.Ended or TuningSessionState.Error or TuningSessionState.Stopped;

    /// <summary>
    /// Check the hardware-shape preconditions for a session on a port.
    /// </summary>
    /// <param name="hasNinoTnc">Whether the port's modem is a NinoTNC (it must be — the bursts and
    /// metering are NinoTNC operations).</param>
    /// <param name="hasTaitRadio">Whether the port has a Tait CCDI radio attached (the SDM
    /// coordination link rides its modem).</param>
    /// <param name="peerSdmId">The peer's SDM data identity (must be exactly
    /// <see cref="PeerSdmIdLength"/> characters).</param>
    /// <param name="error">On failure, an operator-facing reason; <c>null</c> on success.</param>
    /// <returns><c>true</c> when the port can arm a session.</returns>
    public static bool CanArm(bool hasNinoTnc, bool hasTaitRadio, string? peerSdmId, out string? error)
    {
        if (!hasNinoTnc)
        {
            error = "this port's modem is not a NinoTNC — deviation tuning transmits and measures NinoTNC bursts";
            return false;
        }
        if (!hasTaitRadio)
        {
            error = "this port has no Tait CCDI radio attached — the SDM coordination link needs one";
            return false;
        }
        if (string.IsNullOrEmpty(peerSdmId) || peerSdmId.Length != PeerSdmIdLength)
        {
            error = $"peerSdmId must be exactly {PeerSdmIdLength} characters (the peer radio's SDM data identity)";
            return false;
        }
        error = null;
        return true;
    }
}
