namespace Packet.Tune.Core;

/// <summary>
/// One <b>hail</b> request — the payload of a <see cref="TuningVerb.Hail"/> (<c>HAIL</c>)
/// telegram. A station sends a hail over the radios' SDM side channel to ask a peer for
/// its <see cref="StationStatus"/>. Because the side channel rides the radio's own FFSK
/// modem — independent of whatever packet modulation the TNC is in — a hail (and the
/// <c>STAT</c> reply) reaches a station you <em>cannot</em> talk to on the packet path
/// because of a mode mismatch, and the reply reveals the mismatch. That is the whole
/// diagnostic point.
/// </summary>
/// <remarks>
/// The wire form is deliberately tiny — the hail always fits a plain 32-character SDM.
/// Sequence numbering, dedupe and versioning come from the enclosing
/// <see cref="TuningTelegram"/>, exactly as for the <see cref="ModeCoordMessage"/> family.
/// </remarks>
public sealed record StationHail
{
    /// <summary>The hailing station's callsign, so the responder can log/audit who hailed
    /// it. Optional (a bare hail carries no callsign). Callsigns are pipe-free, so this
    /// travels verbatim as the telegram's single arg.</summary>
    public string? RequesterCallsign { get; init; }

    /// <summary>Encode to the telegram-args wire form (the requester callsign, or empty).</summary>
    public string ToArgs() => RequesterCallsign ?? string.Empty;

    /// <summary>Wrap in a <see cref="TuningVerb.Hail"/> telegram with the given sequence number.</summary>
    public TuningTelegram ToTelegram(int sequence) => new(sequence, TuningVerb.Hail, ToArgs());

    /// <summary>Parse the args of a <c>HAIL</c> telegram. Always succeeds — an empty arg is a
    /// valid bare hail; any non-empty arg is taken as the requester callsign.</summary>
    public static bool TryParse(string? args, out StationHail hail)
    {
        hail = new StationHail { RequesterCallsign = string.IsNullOrEmpty(args) ? null : args };
        return true;
    }

    /// <summary>Extract a hail from any telegram (<c>false</c> for non-<c>HAIL</c> verbs).</summary>
    public static bool TryFromTelegram(TuningTelegram telegram, out StationHail? hail)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        if (telegram.Verb != TuningVerb.Hail)
        {
            hail = null;
            return false;
        }
        bool parsed = TryParse(telegram.Args, out var value);
        hail = value;
        return parsed;
    }

    /// <inheritdoc/>
    public override string ToString() => ToArgs();
}
