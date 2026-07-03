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
}

/// <summary>
/// One tuning-protocol telegram: a compact ASCII line
/// <c>V1|&lt;seq&gt;|&lt;verb&gt;|&lt;args&gt;</c> with verbs
/// <c>HI</c>/<c>RQ</c>/<c>MS</c>/<c>AD</c>/<c>BY</c>. The same telegram
/// travels over any <see cref="ITuningLink"/> — as the text of a Tait SDM
/// (<see cref="SdmTuningLink"/>) or as a JSON-wrapped WebSocket frame
/// (<see cref="WebSocketTuningLink"/>).
/// </summary>
/// <remarks>
/// A plain Tait SDM carries at most 32 characters, and the canonical
/// <c>MS</c> args (<c>dec/&lt;n&gt;|fec:&lt;delta&gt;|clip:&lt;delta&gt;|rssi:&lt;dbm&gt;</c>)
/// can exceed that — so the codec also has a documented <em>compact wire
/// form</em> (<see cref="EncodeCompact"/>) that shortens the <c>MS</c> arg
/// keys to <c>f</c>/<c>c</c>/<c>r</c>. <see cref="TryParse"/> accepts both
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

    /// <summary>Encode to the canonical wire form <c>V1|seq|verb|args</c>
    /// (the trailing <c>|args</c> is omitted when <see cref="Args"/> is empty).</summary>
    public string Encode()
    {
        string head = string.Create(
            CultureInfo.InvariantCulture, $"{VersionPrefix}|{Sequence}|{VerbToWire(Verb)}");
        return Args.Length == 0 ? head : head + "|" + Args;
    }

    /// <summary>
    /// Encode to the compact wire form used over SDM (32-character budget):
    /// identical to <see cref="Encode"/> except that <c>MS</c> args are
    /// re-encoded with single-letter keys via
    /// <see cref="MeterReport.ToCompactArgs"/> when they parse as a report.
    /// </summary>
    public string EncodeCompact()
    {
        if (Verb == TuningVerb.Measurement && MeterReport.TryParse(Args, out var report) && report is not null)
        {
            return (this with { Args = report.ToCompactArgs() }).Encode();
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
        _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "unknown tuning verb"),
    };

    private static TuningVerb? WireToVerb(string wire) => wire switch
    {
        "HI" => TuningVerb.Hello,
        "RQ" => TuningVerb.BurstRequest,
        "MS" => TuningVerb.Measurement,
        "AD" => TuningVerb.Advice,
        "BY" => TuningVerb.Bye,
        _ => null,
    };

    /// <inheritdoc/>
    public override string ToString() => Encode();
}
