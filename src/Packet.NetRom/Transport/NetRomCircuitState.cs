namespace Packet.NetRom.Transport;

/// <summary>
/// The lifecycle state of a NET/ROM L4 circuit. A textbook connection FSM:
/// Disconnected → (Connecting | listening) → Connected → Disconnecting →
/// Disconnected. Hand-written (NET/ROM has no SDL), kept small and conventional.
/// </summary>
public enum NetRomCircuitState
{
    /// <summary>No circuit — the initial and terminal state.</summary>
    Disconnected,

    /// <summary>We sent a Connect Request and are awaiting the Connect Acknowledge.</summary>
    Connecting,

    /// <summary>The circuit is up; Information may flow both ways.</summary>
    Connected,

    /// <summary>We sent a Disconnect Request and are awaiting the Disconnect Acknowledge.</summary>
    Disconnecting,
}

/// <summary>Why a circuit ended — surfaced to the consumer on close.</summary>
public enum NetRomCircuitCloseReason
{
    /// <summary>A clean disconnect (either end requested it and it was acknowledged).</summary>
    Normal,

    /// <summary>The far end refused our Connect Request (Connect Acknowledge with the refuse bit).</summary>
    Refused,

    /// <summary>Retries were exhausted on a connect / disconnect / data message — the link is dead.</summary>
    Timeout,
}
