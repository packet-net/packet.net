namespace Packet.Rig;

/// <summary>
/// An operating mode as a canonical token (hamlib's vocabulary: <c>USB</c>, <c>LSB</c>,
/// <c>CW</c>, <c>PKTUSB</c>, …) with pass-through for anything a backend reports that has no
/// canonical spelling. Mode vocabularies genuinely diverge across backends — hamlib
/// canonicalises per-rig names itself, flrig surfaces whatever the attached rig calls the mode
/// ("DIGI", "DATA-U", …) — so this is a value wrapper over a token, not a closed enum: compare
/// against the well-known statics where a match exists, and fall back to <see cref="Token"/>
/// for the rest.
/// </summary>
public readonly record struct RigMode
{
    /// <summary>The wire token, normalised to upper-case invariant. Never empty.</summary>
    public string Token { get; }

    private RigMode(string token) => Token = token;

    /// <summary>
    /// Wrap a backend-native or user-supplied mode token. Trims and upper-cases; rejects empty
    /// tokens and tokens containing whitespace/control characters (mode tokens travel on
    /// line-oriented wire protocols, so embedded separators would be command injection).
    /// </summary>
    public static RigMode From(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var trimmed = token.Trim().ToUpperInvariant();
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                throw new ArgumentException(
                    $"Mode token '{token}' contains whitespace/control characters.", nameof(token));
            }
        }

        return new RigMode(trimmed);
    }

    /// <summary>Upper sideband.</summary>
    public static readonly RigMode Usb = new("USB");

    /// <summary>Lower sideband.</summary>
    public static readonly RigMode Lsb = new("LSB");

    /// <summary>CW.</summary>
    public static readonly RigMode Cw = new("CW");

    /// <summary>CW reverse sideband.</summary>
    public static readonly RigMode CwR = new("CWR");

    /// <summary>RTTY / FSK.</summary>
    public static readonly RigMode Rtty = new("RTTY");

    /// <summary>RTTY reverse sideband.</summary>
    public static readonly RigMode RttyR = new("RTTYR");

    /// <summary>AM.</summary>
    public static readonly RigMode Am = new("AM");

    /// <summary>FM.</summary>
    public static readonly RigMode Fm = new("FM");

    /// <summary>Narrow FM.</summary>
    public static readonly RigMode FmN = new("FMN");

    /// <summary>Wide FM (broadcast).</summary>
    public static readonly RigMode WFm = new("WFM");

    /// <summary>Data/packet on upper sideband (hamlib <c>PKTUSB</c>; rigs call it DATA-U,
    /// USB-D, DIG …).</summary>
    public static readonly RigMode PktUsb = new("PKTUSB");

    /// <summary>Data/packet on lower sideband.</summary>
    public static readonly RigMode PktLsb = new("PKTLSB");

    /// <summary>Data/packet over FM (9k6-style direct FSK or AFSK).</summary>
    public static readonly RigMode PktFm = new("PKTFM");

    /// <inheritdoc />
    public override string ToString() => Token;
}

/// <summary>A mode report: the mode plus the passband width where the backend supplies one
/// (hamlib does; flrig's get-side doesn't, so <see cref="PassbandHz"/> is <c>null</c> there).</summary>
/// <param name="Mode">The operating mode.</param>
/// <param name="PassbandHz">Receiver passband width in Hz, when reported. <c>0</c> means the
/// backend reported "default/unspecified".</param>
public readonly record struct RigModeState(RigMode Mode, int? PassbandHz);
