using System.Globalization;
using System.Text;
using Packet.Kiss.NinoTnc;

namespace Packet.Tune.Core;

/// <summary>
/// One station's <b>status</b> — the payload of a <see cref="TuningVerb.Status"/>
/// (<c>STAT</c>) telegram, sent in reply to a <see cref="StationHail"/>. It answers
/// "who are you, what modulation/modem are you on, and what can you do?" over the
/// mode-agnostic SDM side channel, so a hailer learns a peer's state even when the
/// packet path between them is broken by a mode mismatch.
/// </summary>
/// <remarks>
/// <para>
/// The wire form is a compact keyed line — <c>cs:&lt;call&gt;|m:&lt;mode&gt;|b:&lt;bitrate&gt;|
/// ch:&lt;channel&gt;|sm:&lt;m1.m2…&gt;|cap:&lt;t1.t2…&gt;|rh:&lt;dbm&gt;</c> — with every field
/// past the callsign optional. Only <c>cs</c> is mandatory. Unknown keys are ignored on
/// parse (forward compatibility). A minimal status (callsign + mode) fits a plain 32-character
/// SDM; a full status with the supported-mode and capability lists exceeds it and must ride an
/// <b>extended SDM</b> (128 characters — <see cref="Packet.Radio.Tait.TaitSdmSideChannelOptions.EnableExtendedSdm"/>).
/// </para>
/// <para>
/// <see cref="ModeName"/> is <em>derived</em> from <see cref="Mode"/> through
/// <see cref="NinoTncCatalog"/> rather than carried on the wire — both ends share the catalog,
/// so sending the human name would only waste budget and risk disagreeing with the number.
/// </para>
/// </remarks>
public sealed record StationStatus
{
    /// <summary>The responding station's callsign (mandatory).</summary>
    public required string Callsign { get; init; }

    /// <summary>The NinoTNC mode the station is currently running, or <c>null</c> when it could
    /// not be determined (no mode set / TNC unreadable).</summary>
    public byte? Mode { get; init; }

    /// <summary>The over-air bit rate (bits/s) of the current mode, or <c>null</c> when unknown.
    /// Authoritative — sourced from the responder's own modem rather than re-derived by the
    /// hailer — so it stays correct even for the variable "Set from KISS" mode.</summary>
    public int? BitRateHz { get; init; }

    /// <summary>The radio channel the station is on, as the radio reports it (e.g. <c>"0"</c>),
    /// or <c>null</c> when no radio/channel is known.</summary>
    public string? Channel { get; init; }

    /// <summary>The NinoTNC mode numbers this station advertises it can run (its modem
    /// capability set). Empty when not advertised.</summary>
    public IReadOnlyList<byte> SupportedModes { get; init; } = [];

    /// <summary>Short capability tokens the station advertises (e.g. <c>hail</c>, <c>tune</c>,
    /// <c>modecoord</c>, <c>extsdm</c>). Each is a single dot/pipe-free word. Empty when none.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>The responder's receiver RSSI (dBm) sampled at hail receipt — a best-effort
    /// link-quality snapshot of the hail as heard at this end. <c>null</c> when not sampled.</summary>
    public double? RssiOfHailDbm { get; init; }

    /// <summary>The human name of <see cref="Mode"/> from <see cref="NinoTncCatalog"/> (or
    /// <c>mode N</c> when the number is unknown to the catalog); <c>null</c> when
    /// <see cref="Mode"/> is <c>null</c>. Derived, not carried on the wire.</summary>
    public string? ModeName => Mode is { } m
        ? NinoTncCatalog.TryGetByMode(m)?.Name
          ?? string.Create(CultureInfo.InvariantCulture, $"mode {m}")
        : null;

    /// <summary>Encode to the telegram-args wire form (see the type remarks for the shape).</summary>
    public string ToArgs()
    {
        var sb = new StringBuilder();
        sb.Append("cs:").Append(Callsign);
        if (Mode is { } mode)
        {
            sb.Append("|m:").Append(mode.ToString(CultureInfo.InvariantCulture));
        }
        if (BitRateHz is { } bitrate)
        {
            sb.Append("|b:").Append(bitrate.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrEmpty(Channel))
        {
            sb.Append("|ch:").Append(Channel);
        }
        if (SupportedModes.Count > 0)
        {
            sb.Append("|sm:").AppendJoin('.', SupportedModes.Select(m => m.ToString(CultureInfo.InvariantCulture)));
        }
        if (Capabilities.Count > 0)
        {
            sb.Append("|cap:").AppendJoin('.', Capabilities);
        }
        if (RssiOfHailDbm is { } rssi)
        {
            sb.Append("|rh:").Append(rssi.ToString("0.0", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Wrap in a <see cref="TuningVerb.Status"/> telegram with the given sequence number.</summary>
    public TuningTelegram ToTelegram(int sequence) => new(sequence, TuningVerb.Status, ToArgs());

    /// <summary>Try to parse the args of a <c>STAT</c> telegram. Requires the mandatory
    /// <c>cs:</c> callsign token; malformed numeric fields drop that field rather than
    /// failing the whole parse; unknown keys are ignored (forward compatibility).</summary>
    public static bool TryParse(string? args, out StationStatus? status)
    {
        status = null;
        if (string.IsNullOrEmpty(args))
        {
            return false;
        }

        string? callsign = null;
        byte? mode = null;
        int? bitrate = null;
        string? channel = null;
        IReadOnlyList<byte> supportedModes = [];
        IReadOnlyList<string> capabilities = [];
        double? rssi = null;

        foreach (string token in args.Split('|'))
        {
            int colon = token.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }
            string key = token[..colon];
            string value = token[(colon + 1)..];
            switch (key)
            {
                case "cs":
                    callsign = value;
                    break;
                case "m" when byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out byte m):
                    mode = m;
                    break;
                case "b" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int b):
                    bitrate = b;
                    break;
                case "ch":
                    channel = value.Length == 0 ? null : value;
                    break;
                case "sm":
                    supportedModes = ParseModeList(value);
                    break;
                case "cap":
                    capabilities = value.Length == 0 ? [] : value.Split('.');
                    break;
                case "rh" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double r):
                    rssi = r;
                    break;
                default:
                    break; // unknown key — forward compatibility
            }
        }

        if (string.IsNullOrEmpty(callsign))
        {
            return false;
        }
        status = new StationStatus
        {
            Callsign = callsign,
            Mode = mode,
            BitRateHz = bitrate,
            Channel = channel,
            SupportedModes = supportedModes,
            Capabilities = capabilities,
            RssiOfHailDbm = rssi,
        };
        return true;
    }

    /// <summary>Extract a status from any telegram (<c>false</c> for non-<c>STAT</c> verbs or
    /// unreadable args).</summary>
    public static bool TryFromTelegram(TuningTelegram telegram, out StationStatus? status)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        if (telegram.Verb != TuningVerb.Status)
        {
            status = null;
            return false;
        }
        return TryParse(telegram.Args, out status);
    }

    /// <inheritdoc/>
    public override string ToString() => ToArgs();

    private static List<byte> ParseModeList(string value)
    {
        if (value.Length == 0)
        {
            return [];
        }
        var modes = new List<byte>();
        foreach (string part in value.Split('.'))
        {
            if (byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out byte m))
            {
                modes.Add(m);
            }
        }
        return modes;
    }
}
