using System.Text.Json.Serialization;

namespace Packet.Rhp2;

// The RHPv2 message catalogue. One sealed DTO per wire `type`. Conventions
// that apply across the file:
//
//   * Optional fields are nullable and rely on the codec-wide
//     WhenWritingNull ignore condition (RhpJson.Options) to vanish from
//     emitted JSON, matching XRouter's output byte-for-byte.
//   * Replies carry `errCode` / `errText` with capital C / T. The published
//     spec mostly writes them lowercase, but real XRouter emits the
//     capitalised form on EVERY reply — we write XRouter's form and read
//     either (PropertyNameCaseInsensitive).
//   * `port` is a string everywhere. Where XRouter is known to emit a JSON
//     number instead (TRACE recv; the spec's accept example), a
//     StringOrIntConverter normalises on read.
//   * Request `handle` and `data` fields are nullable so field ABSENCE
//     survives deserialization: the wire distinguishes a missing field
//     (errCode 12, "Missing handle"/"Missing data" — RHPTEST + live
//     XRouter) from a present-but-empty one ("data":"" is a legal
//     zero-byte send). Reply `handle` fields are nullable for the same
//     reason XRouter omits them on parameter errors — there is nothing
//     truthful to echo. Emission for present values is unchanged
//     (WhenWritingNull only ever removes fields).

/// <summary>Client authentication request (<c>auth</c>).</summary>
public sealed class AuthMessage : RhpMessage
{
    /// <summary>Creates an <c>auth</c> message.</summary>
    public AuthMessage() : base(RhpMessageType.Auth)
    {
    }

    /// <summary>Username.</summary>
    [JsonPropertyName("user")]
    public string User { get; set; } = string.Empty;

    /// <summary>Password.</summary>
    [JsonPropertyName("pass")]
    public string Pass { get; set; } = string.Empty;
}

/// <summary>Reply to <c>auth</c> (<c>authReply</c>).</summary>
public sealed class AuthReplyMessage : RhpMessage
{
    /// <summary>Creates an <c>authReply</c> message.</summary>
    public AuthReplyMessage() : base(RhpMessageType.AuthReply)
    {
    }

    /// <summary>Result code; see <see cref="RhpErrorCode"/>.</summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    /// <summary>Human-readable result text.</summary>
    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }
}

/// <summary>
/// Combined create/bind/connect-or-listen request (<c>open</c>) — the
/// high-level alternative to the socket/bind/listen/connect sequence.
/// </summary>
public sealed class OpenMessage : RhpMessage
{
    /// <summary>Creates an <c>open</c> message.</summary>
    public OpenMessage() : base(RhpMessageType.Open)
    {
    }

    /// <summary>Protocol family; see <see cref="ProtocolFamily"/>.</summary>
    [JsonPropertyName("pfam")]
    public string Pfam { get; set; } = string.Empty;

    /// <summary>Socket mode; see <see cref="SocketMode"/>.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    /// <summary>Port identifier, where the family needs one.</summary>
    [JsonPropertyName("port")]
    public string? Port { get; set; }

    /// <summary>Local address (e.g. our callsign).</summary>
    [JsonPropertyName("local")]
    public string? Local { get; set; }

    /// <summary>Remote address, for active opens.</summary>
    [JsonPropertyName("remote")]
    public string? Remote { get; set; }

    /// <summary>Bit flags; see <see cref="OpenFlags"/>.</summary>
    [JsonPropertyName("flags")]
    public int Flags { get; set; }
}

/// <summary>Reply to <c>open</c> (<c>openReply</c>).</summary>
public sealed class OpenReplyMessage : RhpMessage
{
    /// <summary>Creates an <c>openReply</c> message.</summary>
    public OpenReplyMessage() : base(RhpMessageType.OpenReply)
    {
    }

    /// <summary>Handle for the newly opened socket.</summary>
    [JsonPropertyName("handle")]
    public int Handle { get; set; }

    /// <summary>Result code; see <see cref="RhpErrorCode"/>.</summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    /// <summary>Human-readable result text.</summary>
    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }
}

/// <summary>Create an unbound socket (<c>socket</c>).</summary>
public sealed class SocketMessage : RhpMessage
{
    /// <summary>Creates a <c>socket</c> message.</summary>
    public SocketMessage() : base(RhpMessageType.Socket)
    {
    }

    /// <summary>Protocol family; see <see cref="ProtocolFamily"/>.</summary>
    [JsonPropertyName("pfam")]
    public string Pfam { get; set; } = string.Empty;

    /// <summary>Socket mode; see <see cref="SocketMode"/>.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;
}

/// <summary>Reply to <c>socket</c> (<c>socketReply</c>).</summary>
public sealed class SocketReplyMessage : RhpMessage
{
    /// <summary>Creates a <c>socketReply</c> message.</summary>
    public SocketReplyMessage() : base(RhpMessageType.SocketReply)
    {
    }

    /// <summary>Handle for the new socket; absent on failure.</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Result code; see <see cref="RhpErrorCode"/>.</summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    /// <summary>Human-readable result text.</summary>
    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }
}

/// <summary>Bind a socket to a local address (<c>bind</c>).</summary>
public sealed class BindMessage : RhpMessage
{
    /// <summary>Creates a <c>bind</c> message.</summary>
    public BindMessage() : base(RhpMessageType.Bind)
    {
    }

    /// <summary>Socket handle from <c>socketReply</c>. Null when the wire
    /// field was absent — the server answers errCode 12 ("Missing handle").</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Local address to bind.</summary>
    [JsonPropertyName("local")]
    public string Local { get; set; } = string.Empty;

    /// <summary>Port identifier, where the family needs one.</summary>
    [JsonPropertyName("port")]
    public string? Port { get; set; }
}

/// <summary>Reply to <c>bind</c> (<c>bindReply</c>).</summary>
public sealed class BindReplyMessage : RhpMessage
{
    /// <summary>Creates a <c>bindReply</c> message.</summary>
    public BindReplyMessage() : base(RhpMessageType.BindReply)
    {
    }

    /// <summary>The socket handle the request named; omitted on parameter
    /// errors (the request named none), matching XRouter.</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Result code; see <see cref="RhpErrorCode"/>.</summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    /// <summary>Human-readable result text.</summary>
    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }
}

/// <summary>Put a bound socket into the listening state (<c>listen</c>).</summary>
public sealed class ListenMessage : RhpMessage
{
    /// <summary>Creates a <c>listen</c> message.</summary>
    public ListenMessage() : base(RhpMessageType.Listen)
    {
    }

    /// <summary>Socket handle to listen on. Null when the wire field was
    /// absent — the server answers errCode 12 ("Missing handle").</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Bit flags; see <see cref="OpenFlags"/>.</summary>
    [JsonPropertyName("flags")]
    public int Flags { get; set; }
}

/// <summary>Reply to <c>listen</c> (<c>listenReply</c>).</summary>
public sealed class ListenReplyMessage : RhpMessage
{
    /// <summary>Creates a <c>listenReply</c> message.</summary>
    public ListenReplyMessage() : base(RhpMessageType.ListenReply)
    {
    }

    /// <summary>The socket handle the request named; omitted on parameter
    /// errors (the request named none), matching XRouter.</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Result code; see <see cref="RhpErrorCode"/>.</summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    /// <summary>Human-readable result text.</summary>
    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }
}

/// <summary>Connect a socket to a remote address (<c>connect</c>).</summary>
public sealed class ConnectMessage : RhpMessage
{
    /// <summary>Creates a <c>connect</c> message.</summary>
    public ConnectMessage() : base(RhpMessageType.Connect)
    {
    }

    /// <summary>Socket handle to connect. Null when the wire field was
    /// absent — the server answers errCode 12 ("Missing handle").</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Remote address, possibly with digi path.</summary>
    [JsonPropertyName("remote")]
    public string Remote { get; set; } = string.Empty;
}

/// <summary>
/// Reply to <c>connect</c> (<c>connectReply</c>). We always emit the
/// camelCase type; the deserializer also accepts the spec's "ConnectReply"
/// typo on read.
/// </summary>
public sealed class ConnectReplyMessage : RhpMessage
{
    /// <summary>Creates a <c>connectReply</c> message.</summary>
    public ConnectReplyMessage() : base(RhpMessageType.ConnectReply)
    {
    }

    /// <summary>The socket handle the request named; omitted on parameter
    /// errors (the request named none), matching XRouter.</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Result code; see <see cref="RhpErrorCode"/>.</summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    /// <summary>Human-readable result text.</summary>
    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }
}

/// <summary>Send data on a socket (<c>send</c>).</summary>
public sealed class SendMessage : RhpMessage
{
    /// <summary>Creates a <c>send</c> message.</summary>
    public SendMessage() : base(RhpMessageType.Send)
    {
    }

    /// <summary>Socket handle to send on. Null when the wire field was
    /// absent — the server answers errCode 12 ("Missing handle").</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>
    /// Payload as a Latin-1 wire string — use
    /// <see cref="RhpDataEncoding"/> to convert binary payloads. The field
    /// is mandatory even when empty (RHPTEST): null (absent on the wire)
    /// draws errCode 12 "Missing data", while <c>""</c> is a legal
    /// zero-byte send.
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    /// <summary>Destination port (DGRAM mode only).</summary>
    [JsonPropertyName("port")]
    public string? Port { get; set; }

    /// <summary>Source address override (DGRAM mode only).</summary>
    [JsonPropertyName("local")]
    public string? Local { get; set; }

    /// <summary>Destination address (DGRAM mode only).</summary>
    [JsonPropertyName("remote")]
    public string? Remote { get; set; }
}

/// <summary>Reply to <c>send</c> (<c>sendReply</c>).</summary>
public sealed class SendReplyMessage : RhpMessage
{
    /// <summary>Creates a <c>sendReply</c> message.</summary>
    public SendReplyMessage() : base(RhpMessageType.SendReply)
    {
    }

    /// <summary>The socket handle the request named; omitted on parameter
    /// errors (the request named none), matching XRouter.</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Result code; see <see cref="RhpErrorCode"/>.</summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    /// <summary>Human-readable result text.</summary>
    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }

    /// <summary>Connection status bits (STREAM mode); see <see cref="StatusFlags"/>.</summary>
    [JsonPropertyName("status")]
    public int? Status { get; set; }
}

/// <summary>Send a datagram to an explicit destination (<c>sendto</c> — all-lowercase "to" on the wire).</summary>
public sealed class SendToMessage : RhpMessage
{
    /// <summary>Creates a <c>sendto</c> message.</summary>
    public SendToMessage() : base(RhpMessageType.SendTo)
    {
    }

    /// <summary>Socket handle to send on. Null when the wire field was
    /// absent — the server answers errCode 12 ("Missing handle").</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>
    /// Payload as a Latin-1 wire string — use
    /// <see cref="RhpDataEncoding"/> to convert binary payloads. Null when
    /// the wire field was absent (see <see cref="SendMessage.Data"/>).
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    /// <summary>Destination port.</summary>
    [JsonPropertyName("port")]
    public string? Port { get; set; }

    /// <summary>Source address override.</summary>
    [JsonPropertyName("local")]
    public string? Local { get; set; }

    /// <summary>Destination address.</summary>
    [JsonPropertyName("remote")]
    public string? Remote { get; set; }

    /// <summary>Type of service.</summary>
    [JsonPropertyName("tos")]
    public int? Tos { get; set; }
}

/// <summary>Reply to <c>sendto</c> (<c>sendtoReply</c> — all-lowercase "to" on the wire).</summary>
public sealed class SendToReplyMessage : RhpMessage
{
    /// <summary>Creates a <c>sendtoReply</c> message.</summary>
    public SendToReplyMessage() : base(RhpMessageType.SendToReply)
    {
    }

    /// <summary>The socket handle the request named; omitted on parameter
    /// errors (the request named none), matching XRouter.</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Result code; see <see cref="RhpErrorCode"/>.</summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    /// <summary>Human-readable result text.</summary>
    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }
}

/// <summary>
/// Server notification: data arrived, or — for sockets opened in TRACE /
/// RAW mode — a decoded frame record (<c>recv</c>).
/// </summary>
public sealed class RecvMessage : RhpMessage
{
    /// <summary>Creates a <c>recv</c> message.</summary>
    public RecvMessage() : base(RhpMessageType.Recv)
    {
    }

    /// <summary>Socket handle the data arrived on.</summary>
    [JsonPropertyName("handle")]
    public int Handle { get; set; }

    /// <summary>
    /// Payload as a Latin-1 wire string — use
    /// <see cref="RhpDataEncoding"/> to recover binary payloads.
    /// </summary>
    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;

    /// <summary>
    /// Port the data arrived on. XRouter emits a JSON string in DGRAM mode
    /// but a JSON number in TRACE mode — normalised to a string on read.
    /// </summary>
    [JsonPropertyName("port")]
    [JsonConverter(typeof(StringOrIntConverter))]
    public string? Port { get; set; }

    /// <summary>Local (destination) address, DGRAM mode.</summary>
    [JsonPropertyName("local")]
    public string? Local { get; set; }

    /// <summary>Remote (source) address, DGRAM mode.</summary>
    [JsonPropertyName("remote")]
    public string? Remote { get; set; }

    // TRACE / RAW metadata. None of these appear in plain STREAM /
    // DGRAM recv messages, and ilen/pid/ptcl/tseq aren't in the published
    // spec at all — they're XRouter-observed.

    /// <summary>"sent" or "rcvd" (TRACE / RAW).</summary>
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    /// <summary>Source callsign of the traced frame.</summary>
    [JsonPropertyName("srce")]
    public string? Srce { get; set; }

    /// <summary>Destination callsign of the traced frame.</summary>
    [JsonPropertyName("dest")]
    public string? Dest { get; set; }

    /// <summary>Raw AX.25 control byte.</summary>
    [JsonPropertyName("ctrl")]
    public int? Ctrl { get; set; }

    /// <summary>Decoded frame type, e.g. "I", "RR", "SABM" (all-lowercase <c>frametype</c> on the wire).</summary>
    [JsonPropertyName("frametype")]
    public string? FrameType { get; set; }

    /// <summary>Receive sequence number N(R).</summary>
    [JsonPropertyName("rseq")]
    public int? Rseq { get; set; }

    /// <summary>Transmit sequence number N(S).</summary>
    [JsonPropertyName("tseq")]
    public int? Tseq { get; set; }

    /// <summary>Command/response indicator, "C" or "R".</summary>
    [JsonPropertyName("cr")]
    public string? Cr { get; set; }

    /// <summary>Poll/final indicator, "P" or "F".</summary>
    [JsonPropertyName("pf")]
    public string? Pf { get; set; }

    /// <summary>Information field length (TRACE I-frames).</summary>
    [JsonPropertyName("ilen")]
    public int? Ilen { get; set; }

    /// <summary>AX.25 PID byte (TRACE I-frames).</summary>
    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    /// <summary>Decoded layer-3 protocol name, e.g. "DATA", "NETROM", "IP" (TRACE).</summary>
    [JsonPropertyName("ptcl")]
    public string? Ptcl { get; set; }
}

/// <summary>
/// Server notification: an inbound connection arrived on a listening
/// socket and a child socket was created for it (<c>accept</c>).
/// </summary>
public sealed class AcceptMessage : RhpMessage
{
    /// <summary>Creates an <c>accept</c> message.</summary>
    public AcceptMessage() : base(RhpMessageType.Accept)
    {
    }

    /// <summary>The listening socket's handle.</summary>
    [JsonPropertyName("handle")]
    public int Handle { get; set; }

    /// <summary>Handle of the newly created child socket.</summary>
    [JsonPropertyName("child")]
    public int Child { get; set; }

    /// <summary>Remote (caller's) address.</summary>
    [JsonPropertyName("remote")]
    public string? Remote { get; set; }

    /// <summary>Local address the caller connected to.</summary>
    [JsonPropertyName("local")]
    public string? Local { get; set; }

    /// <summary>
    /// Port the connection arrived on. The spec's example shows a JSON
    /// number but real XRouter sends a string — normalised to a string
    /// on read.
    /// </summary>
    [JsonPropertyName("port")]
    [JsonConverter(typeof(StringOrIntConverter))]
    public string? Port { get; set; }
}

/// <summary>
/// Socket status: a client request (no <c>flags</c>) or a server
/// notification carrying <see cref="StatusFlags"/> bits (<c>status</c>).
/// </summary>
public sealed class StatusMessage : RhpMessage
{
    /// <summary>Creates a <c>status</c> message.</summary>
    public StatusMessage() : base(RhpMessageType.Status)
    {
    }

    /// <summary>Socket handle. Always present on server pushes; null when a
    /// client request omitted it (the server answers errCode 12).</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Status bits; see <see cref="StatusFlags"/>. Absent on client requests.</summary>
    [JsonPropertyName("flags")]
    public int? Flags { get; set; }
}

/// <summary>Reply to a client <c>status</c> request (<c>statusReply</c>).</summary>
public sealed class StatusReplyMessage : RhpMessage
{
    /// <summary>Creates a <c>statusReply</c> message.</summary>
    public StatusReplyMessage() : base(RhpMessageType.StatusReply)
    {
    }

    /// <summary>The socket handle the request named; omitted on parameter
    /// errors (the request named none), matching XRouter.</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Result code; see <see cref="RhpErrorCode"/>.</summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    /// <summary>Human-readable result text.</summary>
    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }
}

/// <summary>
/// Close a socket: a client request, or a server notification that the
/// peer disconnected (<c>close</c>).
/// </summary>
public sealed class CloseMessage : RhpMessage
{
    /// <summary>Creates a <c>close</c> message.</summary>
    public CloseMessage() : base(RhpMessageType.Close)
    {
    }

    /// <summary>Socket handle to close. Always present on server pushes;
    /// null when a client request omitted it (errCode 12, not 3 — "3 is
    /// for handles that are well-formed but unknown", RHPTEST).</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }
}

/// <summary>Reply to a client <c>close</c> request (<c>closeReply</c>).</summary>
public sealed class CloseReplyMessage : RhpMessage
{
    /// <summary>Creates a <c>closeReply</c> message.</summary>
    public CloseReplyMessage() : base(RhpMessageType.CloseReply)
    {
    }

    /// <summary>The socket handle the request named; omitted on parameter
    /// errors (the request named none), matching XRouter.</summary>
    [JsonPropertyName("handle")]
    public int? Handle { get; set; }

    /// <summary>Result code; see <see cref="RhpErrorCode"/>.</summary>
    [JsonPropertyName("errCode")]
    public int ErrCode { get; set; }

    /// <summary>Human-readable result text.</summary>
    [JsonPropertyName("errText")]
    public string? ErrText { get; set; }
}
