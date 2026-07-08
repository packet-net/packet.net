using System.Globalization;

namespace Packet.Tune.Core;

/// <summary>The verbs of the tuning-telegram protocol.</summary>
public enum TuningVerb
{
    /// <summary><c>HI</c> — handshake/ready beacon. Args = the sender's role
    /// (<c>tuned</c> / <c>meter</c>). The tuned end also re-sends it between
    /// rounds as its "ready for the next burst" signal.</summary>
    Hello,

    /// <summary><c>RQ</c> — meter → tuned: transmit an n-frame burst now. Args = n.</summary>
    BurstRequest,

    /// <summary><c>MS</c> — meter → tuned: the burst's measurement
    /// (<see cref="MeterReport"/> args).</summary>
    Measurement,

    /// <summary><c>AD</c> — meter → tuned: advice for the human at the pot
    /// (<c>UP</c> / <c>DN</c> / <c>OK</c>).</summary>
    Advice,

    /// <summary><c>BY</c> — end of session (no args).</summary>
    Bye,

    /// <summary><c>MODE</c> — a mode-coordination message (propose / confirm /
    /// reject / commit / sent / report / revert). Args are the
    /// <see cref="ModeCoordMessage"/> wire form, e.g. <c>propose|2|1</c>.</summary>
    ModeCoordination,

    /// <summary><c>HAIL</c> — hailer → responder: "tell me your station status".
    /// Args are the <see cref="StationHail"/> wire form (the requester's callsign,
    /// or empty). The reply is a <see cref="Status"/> telegram.</summary>
    Hail,

    /// <summary><c>STAT</c> — responder → hailer: this station's
    /// <see cref="StationStatus"/> (callsign, current NinoTNC mode + bitrate, radio
    /// channel, supported modes, capabilities, and the responder's RSSI of the hail).
    /// The richer status can exceed the plain-SDM budget and rides an extended SDM.</summary>
    Status,

    /// <summary><c>TXD</c> — a TXDELAY-minimisation message (propose / confirm /
    /// reject / step / sent / report / apply / done / abort). Args are the
    /// <see cref="TxDelayMinMessage"/> wire form, e.g. <c>step|300|5</c>. A large
    /// <c>report</c> can exceed the plain-SDM budget and rides an extended SDM,
    /// like <see cref="Status"/>.</summary>
    TxDelay,
}

/// <summary>
/// One tuning-protocol telegram: a compact ASCII line
/// <c>V1|&lt;seq&gt;|&lt;verb&gt;|&lt;args&gt;</c> with verbs
/// <c>HI</c>/<c>RQ</c>/<c>MS</c>/<c>AD</c>/<c>BY</c>/<c>MODE</c>. The same telegram
/// travels over any <see cref="ITuningLink"/> — as the text of a Tait SDM
/// (<see cref="SdmTuningLink"/>) or as a JSON-wrapped WebSocket frame
/// (<see cref="WebSocketTuningLink"/>).
/// </summary>
/// <remarks>
/// A plain Tait SDM carries at most <see cref="SdmCharacterBudget"/>
/// characters, and the canonical <c>MS</c> args
/// (<c>dec/&lt;n&gt;|fec:&lt;delta&gt;|clip:&lt;delta&gt;|rssi:&lt;dbm&gt;|lvl:&lt;db&gt;</c>)
/// can exceed that — so the codec also has a documented <em>compact wire
/// form</em> (<see cref="EncodeCompact"/>) that shortens the <c>MS</c> arg
/// keys to <c>f</c>/<c>c</c>/<c>r</c>/<c>l</c> and, if the result would
/// still bust the budget, drops the optional audio-level enrichment (the
/// bracketing signals keep priority). <see cref="TryParse"/> accepts both
/// forms, so either encoding round-trips.
/// </remarks>
/// <param name="Sequence">Monotonic per-sender sequence number — the receiver
/// dedupes on it (transport retries may deliver a telegram twice).</param>
/// <param name="Verb">The protocol verb.</param>
/// <param name="Args">Verb-specific argument text (may be empty, e.g. <c>BY</c>).</param>
public sealed record TuningTelegram(int Sequence, TuningVerb Verb, string Args)
{
    /// <summary>The protocol version marker every telegram starts with.</summary>
    public const string VersionPrefix = "V1";

    /// <summary>The character budget of a plain Tait SDM — the compact wire
    /// form must fit inside it.</summary>
    public const int SdmCharacterBudget = 32;

    /// <summary>Encode to the canonical wire form <c>V1|seq|verb|args</c>
    /// (the trailing <c>|args</c> is omitted when <see cref="Args"/> is empty).</summary>
    public string Encode()
    {
        string head = string.Create(
            CultureInfo.InvariantCulture, $"{VersionPrefix}|{Sequence}|{VerbToWire(Verb)}");
        return Args.Length == 0 ? head : head + "|" + Args;
    }

    /// <summary>
    /// Encode to the compact wire form used over SDM
    /// (<see cref="SdmCharacterBudget"/>-character budget): identical to
    /// <see cref="Encode"/> except that <c>MS</c> args are re-encoded with
    /// single-letter keys via <see cref="MeterReport.ToCompactArgs"/> when
    /// they parse as a report. If even the compact form busts the budget and
    /// the report carries the optional audio level, the level is dropped —
    /// it is enrichment; the bracketing signals (decode/FEC/clip/RSSI) keep
    /// priority.
    /// </summary>
    public string EncodeCompact()
    {
        if (Verb == TuningVerb.Measurement && MeterReport.TryParse(Args, out var report) && report is not null)
        {
            string wire = (this with { Args = report.ToCompactArgs() }).Encode();
            if (wire.Length > SdmCharacterBudget && report.AudioLevelDb is not null)
            {
                wire = (this with { Args = (report with { AudioLevelDb = null }).ToCompactArgs() }).Encode();
            }
            return wire;
        }
        return Encode();
    }

    /// <summary>
    /// Try to parse a telegram from its wire text (canonical or compact form).
    /// Rejects anything not starting <c>V1|</c>, with an unknown verb, or with
    /// a non-numeric sequence.
    /// </summary>
    public static bool TryParse(string? text, out TuningTelegram? telegram)
    {
        telegram = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string[] parts = text.Split('|', 4);
        if (parts.Length < 3 || parts[0] != VersionPrefix)
        {
            return false;
        }
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int seq))
        {
            return false;
        }
        if (WireToVerb(parts[2]) is not { } verb)
        {
            return false;
        }

        telegram = new TuningTelegram(seq, verb, parts.Length == 4 ? parts[3] : string.Empty);
        return true;
    }

    /// <summary>The two-letter wire token for a verb.</summary>
    public static string VerbToWire(TuningVerb verb) => verb switch
    {
        TuningVerb.Hello => "HI",
        TuningVerb.BurstRequest => "RQ",
        TuningVerb.Measurement => "MS",
        TuningVerb.Advice => "AD",
        TuningVerb.Bye => "BY",
        TuningVerb.ModeCoordination => "MODE",
        TuningVerb.Hail => "HAIL",
        TuningVerb.Status => "STAT",
        TuningVerb.TxDelay => "TXD",
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "unknown tuning verb"),
    };

    private static TuningVerb? WireToVerb(string wire) => wire switch
    {
        "HI" => TuningVerb.Hello,
        "RQ" => TuningVerb.BurstRequest,
        "MS" => TuningVerb.Measurement,
        "AD" => TuningVerb.Advice,
        "BY" => TuningVerb.Bye,
        "MODE" => TuningVerb.ModeCoordination,
        "HAIL" => TuningVerb.Hail,
        "STAT" => TuningVerb.Status,
        "TXD" => TuningVerb.TxDelay,
        _ => null,
    };

    /// <inheritdoc/>
    public override string ToString() => Encode();
}
