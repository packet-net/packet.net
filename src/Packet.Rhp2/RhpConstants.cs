using System.Globalization;

namespace Packet.Rhp2;

/// <summary>
/// RHPv2 protocol families — the wire values of the <c>pfam</c> field.
/// </summary>
/// <remarks>
/// String constants rather than an enum because the field is an open set:
/// XRouter can grow new families without a protocol revision, and an enum
/// would force every consumer through a parse-or-throw choice the wire
/// doesn't require.
/// </remarks>
public static class ProtocolFamily
{
    /// <summary>XRouter command-line interface and node applications.</summary>
    public const string Unix = "unix";

    /// <summary>TCP / UDP / ICMP / raw IP.</summary>
    public const string Inet = "inet";

    /// <summary>AX.25 link layer (connected mode, UI, monitoring).</summary>
    public const string Ax25 = "ax25";

    /// <summary>NET/ROM layer 3/4 datagrams and circuits.</summary>
    public const string NetRom = "netrom";
}

/// <summary>
/// RHPv2 socket modes — the wire values of the <c>mode</c> field.
/// </summary>
/// <remarks>Open set on the wire; see <see cref="ProtocolFamily"/> for why these are strings.</remarks>
public static class SocketMode
{
    /// <summary>Reliable ordered byte stream.</summary>
    public const string Stream = "stream";

    /// <summary>Unreliable datagrams.</summary>
    public const string Dgram = "dgram";

    /// <summary>Reliable sequenced packets (AX.25 connected mode).</summary>
    public const string Seqpkt = "seqpkt";

    /// <summary>Caller-specified protocol.</summary>
    public const string Custom = "custom";

    /// <summary>Decoded addresses plus raw payload.</summary>
    public const string SemiRaw = "semiraw";

    /// <summary>Decoded headers plus payload (monitoring).</summary>
    public const string Trace = "trace";

    /// <summary>Entire raw packet.</summary>
    public const string Raw = "raw";
}

#pragma warning disable CA1711 // "...Flags" names are the right names here: both types model the wire `flags` fields of PWP-0222 and ARE [Flags] enums — the suffix is descriptive, not a misuse.

/// <summary>
/// Bit flags for the <c>flags</c> field of <c>open</c>.
/// </summary>
[Flags]
public enum OpenFlags
{
    /// <summary>Passive open (listen). The wire default — no bits set.</summary>
    Passive = 0x00,

    /// <summary>Report incoming frames (RAW / TRACE modes).</summary>
    TraceIncoming = 0x01,

    /// <summary>Report outgoing frames (RAW / TRACE modes).</summary>
    TraceOutgoing = 0x02,

    /// <summary>Report supervisory frames (TRACE mode, AX.25).</summary>
    TraceSupervisory = 0x04,

    /// <summary>Active open — initiate an outbound connection.</summary>
    Active = 0x80,
}

/// <summary>
/// Bit flags carried in <c>status</c> notifications and the optional
/// <c>status</c> field of <c>sendReply</c>.
/// </summary>
[Flags]
public enum StatusFlags
{
    /// <summary>No status bits set.</summary>
    None = 0,

    /// <summary>Listener is clear to accept a new connection.</summary>
    ConOk = 1,

    /// <summary>Downlink is connected.</summary>
    Connected = 2,

    /// <summary>Not clear to send (flow control asserted).</summary>
    Busy = 4,
}

#pragma warning restore CA1711

/// <summary>
/// RHPv2 <c>errCode</c> values and their canonical <c>errText</c> strings.
/// </summary>
/// <remarks>
/// Codes 0–16 come from the published spec (PWP-0222 / PWP-0245).
/// <see cref="NotConnected"/> (17) is XRouter-observed only: real XRouter
/// returns it from <c>send</c> on a stream socket whose downlink hasn't
/// (or has stopped) being connected. Ints rather than an enum so an
/// unrecognised future code still flows through DTOs unchanged.
/// </remarks>
public static class RhpErrorCode
{
    /// <summary>Success.</summary>
    public const int Ok = 0;

    /// <summary>Unspecified failure.</summary>
    public const int Unspecified = 1;

    /// <summary>The request's <c>type</c> field was bad or missing.</summary>
    public const int BadOrMissingType = 2;

    /// <summary>The supplied socket handle is not valid.</summary>
    public const int InvalidHandle = 3;

    /// <summary>Server is out of memory.</summary>
    public const int NoMemory = 4;

    /// <summary>The request's <c>mode</c> field was bad or missing.</summary>
    public const int BadOrMissingMode = 5;

    /// <summary>The <c>local</c> address is not valid.</summary>
    public const int InvalidLocalAddress = 6;

    /// <summary>The <c>remote</c> address is not valid.</summary>
    public const int InvalidRemoteAddress = 7;

    /// <summary>The request's <c>pfam</c> field was bad or missing.</summary>
    public const int BadOrMissingFamily = 8;

    /// <summary>A socket with the same binding already exists.</summary>
    public const int DuplicateSocket = 9;

    /// <summary>The named port does not exist.</summary>
    public const int NoSuchPort = 10;

    /// <summary>The requested protocol is not valid.</summary>
    public const int InvalidProtocol = 11;

    /// <summary>A request parameter was bad.</summary>
    public const int BadParameter = 12;

    /// <summary>Server is out of buffers.</summary>
    public const int NoBuffers = 13;

    /// <summary>Authentication required or failed.</summary>
    public const int Unauthorised = 14;

    /// <summary>No route to the remote address.</summary>
    public const int NoRoute = 15;

    /// <summary>The operation is not supported on this socket.</summary>
    public const int OperationNotSupported = 16;

    /// <summary>
    /// Stream socket's downlink is not connected (XRouter-observed;
    /// not enumerated in the published spec).
    /// </summary>
    public const int NotConnected = 17;

    /// <summary>
    /// Canonical <c>errText</c> for a code, matching the spec's wording
    /// (including its inconsistent capitalisation — "No Route" but
    /// "Operation not supported").
    /// </summary>
    public static string Text(int code) => code switch
    {
        Ok => "Ok",
        Unspecified => "Unspecified",
        BadOrMissingType => "Bad or missing type",
        InvalidHandle => "Invalid handle",
        NoMemory => "No memory",
        BadOrMissingMode => "Bad or missing mode",
        InvalidLocalAddress => "Invalid local address",
        InvalidRemoteAddress => "Invalid remote address",
        BadOrMissingFamily => "Bad or missing family",
        DuplicateSocket => "Duplicate socket",
        NoSuchPort => "No such port",
        InvalidProtocol => "Invalid protocol",
        BadParameter => "Bad parameter",
        NoBuffers => "No buffers",
        Unauthorised => "Unauthorised",
        NoRoute => "No Route",
        OperationNotSupported => "Operation not supported",
        NotConnected => "Not connected",
        _ => string.Create(CultureInfo.InvariantCulture, $"Unknown ({code})"),
    };
}

/// <summary>
/// The wire values of the <c>type</c> discriminator field.
/// </summary>
/// <remarks>
/// Watch the casing traps: <c>sendto</c> / <c>sendtoReply</c> have a
/// lowercase "to" (unlike, say, <c>connectReply</c>'s camelCase), and we
/// always emit <c>connectReply</c> even though the spec's example shows
/// "ConnectReply" (a typo the deserializer tolerates on read).
/// </remarks>
public static class RhpMessageType
{
    /// <summary>Client authentication request.</summary>
    public const string Auth = "auth";

    /// <summary>Reply to <see cref="Auth"/>.</summary>
    public const string AuthReply = "authReply";

    /// <summary>Capability discovery request (pdn extension; see <see cref="HelloMessage"/>).</summary>
    public const string Hello = "hello";

    /// <summary>Reply to <see cref="Hello"/>.</summary>
    public const string HelloReply = "helloReply";

    /// <summary>Combined create/bind/connect-or-listen request.</summary>
    public const string Open = "open";

    /// <summary>Reply to <see cref="Open"/>.</summary>
    public const string OpenReply = "openReply";

    /// <summary>Create an unbound socket.</summary>
    public const string Socket = "socket";

    /// <summary>Reply to <see cref="Socket"/>.</summary>
    public const string SocketReply = "socketReply";

    /// <summary>Bind a socket to a local address.</summary>
    public const string Bind = "bind";

    /// <summary>Reply to <see cref="Bind"/>.</summary>
    public const string BindReply = "bindReply";

    /// <summary>Put a socket into the listening state.</summary>
    public const string Listen = "listen";

    /// <summary>Reply to <see cref="Listen"/>.</summary>
    public const string ListenReply = "listenReply";

    /// <summary>Connect a socket to a remote address.</summary>
    public const string Connect = "connect";

    /// <summary>Reply to <see cref="Connect"/>.</summary>
    public const string ConnectReply = "connectReply";

    /// <summary>Send data on a connected (or DGRAM-addressed) socket.</summary>
    public const string Send = "send";

    /// <summary>Reply to <see cref="Send"/>.</summary>
    public const string SendReply = "sendReply";

    /// <summary>Send a datagram to an explicit destination.</summary>
    public const string SendTo = "sendto";

    /// <summary>Reply to <see cref="SendTo"/>.</summary>
    public const string SendToReply = "sendtoReply";

    /// <summary>Server notification: received data (or a trace record).</summary>
    public const string Recv = "recv";

    /// <summary>Server notification: inbound connection accepted on a listener.</summary>
    public const string Accept = "accept";

    /// <summary>Socket status (request from client, or notification from server).</summary>
    public const string Status = "status";

    /// <summary>Reply to a client <see cref="Status"/> request.</summary>
    public const string StatusReply = "statusReply";

    /// <summary>Close a socket (request from client, or notification from server).</summary>
    public const string Close = "close";

    /// <summary>Reply to a client <see cref="Close"/> request.</summary>
    public const string CloseReply = "closeReply";
}
