namespace Packet.Agw;

/// <summary>
/// AGW command-kind letters as documented by SV2AGW's original
/// PE+AGWPE protocol specification and the de-facto extensions
/// implemented by direwolf, LinBPQ, and SoundModem.
/// </summary>
/// <remarks>
/// AGW uses a single ASCII letter at offset 4 of the frame header to
/// dispatch commands. Application → server letters are uppercase
/// (commands sent by us); server → application letters are mostly
/// lowercase or capital depending on the verb. The reverse case
/// distinction is the only schema-level handshake — no length prefix,
/// no negotiation. Reading the command letter is enough to know what
/// the remaining frame body means.
///
/// We deliberately do NOT enum these — the wire field is a byte and
/// some servers send letters not in the formal spec (custom modem
/// integrations occasionally use private letters). Treating it as a
/// raw byte keeps us tolerant.
/// </remarks>
public static class AgwCommandKind
{
    // ─── Application → server (we send) ─────────────────────────────

    /// <summary>Register a callsign so the server routes inbound frames addressed to it back to this client. Reply is <c>X</c> with a one-byte status.</summary>
    public const byte RegisterCallsign = (byte)'X';
    /// <summary>Unregister a previously-registered callsign.</summary>
    public const byte UnregisterCallsign = (byte)'x';
    /// <summary>Ask for a list of ports the server has configured. Reply is <c>G</c> with a NUL-terminated semicolon-delimited port description.</summary>
    public const byte AskPortInfo = (byte)'G';
    /// <summary>Ask for the server's version. Reply is <c>R</c> with major/minor uint16s in the user field.</summary>
    public const byte AskVersion = (byte)'R';
    /// <summary>Ask for the count of outstanding frames towards a remote callsign on this session. Reply is <c>Y</c>.</summary>
    public const byte AskOutstandingFrames = (byte)'Y';
    /// <summary>Initiate an AX.25 connect (SABM mod-8) towards the callsign in the To field.</summary>
    public const byte Connect = (byte)'C';
    /// <summary>Initiate a connect with a via-digipeater path. Body holds <c>digi_count</c> + the path callsigns.</summary>
    public const byte ConnectVia = (byte)'v';
    /// <summary>Initiate an AX.25 connect with a non-standard PID. Body's first byte is the PID.</summary>
    public const byte ConnectNonStandard = (byte)'c';
    /// <summary>Initiate an AX.25 v2.2 connect (SABME mod-128).</summary>
    public const byte ConnectV22 = (byte)'C';   // server side branches on bit-0 of reserved field; same letter
    /// <summary>Send connected-mode data on an established session.</summary>
    public const byte Data = (byte)'D';
    /// <summary>Disconnect an established session.</summary>
    public const byte Disconnect = (byte)'d';
    /// <summary>Send a UNPROTO (UI) frame. Body is the info field; PID at offset 6 of the header.</summary>
    public const byte Unproto = (byte)'M';
    /// <summary>Send a UNPROTO frame via a digipeater path.</summary>
    public const byte UnprotoVia = (byte)'V';
    /// <summary>Send a raw AX.25 frame (bypass the L2 stack; AGW server passes it directly to the modem).</summary>
    public const byte Raw = (byte)'K';

    // ─── Server → application (we receive) ──────────────────────────

    /// <summary>Inbound monitored UI / supervisory frame for our registered callsign(s). Server-initiated.</summary>
    public const byte MonitoredUnproto = (byte)'U';
    /// <summary>Inbound monitored connection-related frame (SABM/UA/DISC/DM seen on the channel). Server-initiated.</summary>
    public const byte MonitoredConnect = (byte)'I';
    /// <summary>Inbound monitored supervisory frame (RR/RNR/REJ/SREJ). Server-initiated.</summary>
    public const byte MonitoredSupervisory = (byte)'S';
    /// <summary>Heard list notification: a new station has been heard on the channel. Server-initiated.</summary>
    public const byte HeardStation = (byte)'H';
}
