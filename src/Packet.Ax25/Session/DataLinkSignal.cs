namespace Packet.Ax25.Session;

/// <summary>
/// Base type for all signals the data-link layer raises **upward** to Layer 3.
/// Emitted by the dispatcher when an SDL <c>signal_upper</c> verb fires; the
/// session forwards them to the upper-layer consumer via the configured
/// <c>sendUpward</c> callback.
/// </summary>
/// <remarks>
/// The five DL primitives plus the ten error-indication letter variants per
/// §5.5 / §C5 are modelled here. New signals land as new SDL pages need them.
/// </remarks>
public abstract record DataLinkSignal(string Name);

/// <summary>Link establishment requested by the peer (§5.5.1).</summary>
public sealed record DataLinkConnectIndication() : DataLinkSignal("DL_CONNECT_indication");

/// <summary>Our outbound DL-CONNECT-request has been accepted (§5.5.2).</summary>
public sealed record DataLinkConnectConfirm() : DataLinkSignal("DL_CONNECT_confirm");

/// <summary>Link teardown requested by the peer (§5.5.3).</summary>
public sealed record DataLinkDisconnectIndication() : DataLinkSignal("DL_DISCONNECT_indication");

/// <summary>Our outbound DL-DISCONNECT-request has been acknowledged (§5.5.4).</summary>
public sealed record DataLinkDisconnectConfirm() : DataLinkSignal("DL_DISCONNECT_confirm");

/// <summary>
/// An I-frame's information field is being delivered to Layer 3 (§5.5.5).
/// </summary>
/// <param name="Info">The information field of the I-frame being delivered.</param>
/// <param name="Pid">The PID octet from the I-frame, identifying the Layer-3 protocol.</param>
public sealed record DataLinkDataIndication(ReadOnlyMemory<byte> Info, byte Pid)
    : DataLinkSignal("DL_DATA_indication");

/// <summary>
/// A data-link error has occurred. <see cref="Code"/> is the letter code per
/// §C5 error indication table (e.g. <c>"C"</c>, <c>"D"</c>, <c>"C_D"</c>,
/// <c>"E"</c>, …, <c>"O"</c>). The dispatcher derives the code from the
/// SDL verb spelling — <c>DL_ERROR_indication_C_D</c> → <c>Code = "C_D"</c>.
/// </summary>
public sealed record DataLinkErrorIndication(string Code)
    : DataLinkSignal("DL_ERROR_indication");
