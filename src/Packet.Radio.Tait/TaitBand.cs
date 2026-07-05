namespace Packet.Radio.Tait;

/// <summary>
/// One Tait TM8100/TM8200 <b>band split</b>: the two-character band designator (e.g. <c>B1</c>),
/// the RF range the hardware covers, and the UK amateur band that range falls in (if any). The
/// designator is the <c>[A-Z][0-9]</c> code carried in the radio's product code — the character pair
/// immediately after the first <c>-</c> in the <c>RADIO_VERSIONS</c> record <c>[00]</c> product code
/// string (e.g. <c>TMAB12-<b>B1</b>00_0201</c>). Table from
/// <see href="https://wiki.oarc.uk/radios:tait_tm8100">the OARC Tait TM8100 wiki</see>, with the UK
/// 4&#8239;m allocation added to <c>A4</c> (the wiki marks it "no amateur band", but 66–88&#8239;MHz
/// covers the UK 70&#8239;MHz / 4&#8239;m band — bench-confirmed on 4&#8239;m A4 radios).
/// </summary>
/// <param name="Code">The two-character band designator, e.g. <c>B1</c>.</param>
/// <param name="MinHz">Bottom of the hardware's RF range, in hertz.</param>
/// <param name="MaxHz">Top of the hardware's RF range, in hertz.</param>
/// <param name="AmateurBand">The UK amateur band the split covers (<c>4m</c> / <c>2m</c> / <c>70cm</c>),
/// or <c>null</c> when the split has no UK amateur allocation.</param>
public sealed record TaitBand(string Code, int MinHz, int MaxHz, string? AmateurBand);

/// <summary>
/// The catalogue of known Tait TM8100/TM8200 band splits and a parser that reads the band off a
/// radio's product code. The product code is <see cref="TaitRadioIdentity.Versions"/> record
/// <c>[00]</c> (the <c>RADIO_VERSIONS</c> query, e.g. <c>TMAB12-B100_0201</c>); the band designator is
/// the <c>[A-Z][0-9]</c> pair immediately after the <b>first</b> <c>-</c>. The <em>frequency</em> a
/// radio is tuned to is not CCDI-readable, but the <em>band split</em> — hence the amateur band — is,
/// so PDN can label a head-end-adopted port by band without operator input.
/// </summary>
public static class TaitBandCatalog
{
    /// <summary>The <see cref="TaitRadioIdentity.Versions"/> record number carrying the product code.</summary>
    public const string ProductCodeRecord = "00";

    /// <summary>Every known band split, in designator order. UK amateur allocation on
    /// <c>A4</c>&#8239;=&#8239;4&#8239;m, <c>B1</c>&#8239;=&#8239;2&#8239;m,
    /// <c>H5</c>/<c>H6</c>/<c>H7</c>&#8239;=&#8239;70&#8239;cm; the rest have none.</summary>
    public static IReadOnlyList<TaitBand> All { get; } =
    [
        new("A4", 66_000_000, 88_000_000, "4m"),
        new("B1", 136_000_000, 174_000_000, "2m"),
        new("C0", 174_000_000, 225_000_000, null),
        new("D1", 216_000_000, 266_000_000, null),
        new("G2", 350_000_000, 400_000_000, null),
        new("H5", 400_000_000, 470_000_000, "70cm"),
        new("H6", 450_000_000, 530_000_000, "70cm"),
        new("H7", 450_000_000, 520_000_000, "70cm"),
        new("K5", 762_000_000, 870_000_000, null),
    ];

    private static readonly Dictionary<string, TaitBand> ByCode =
        All.ToDictionary(b => b.Code, StringComparer.Ordinal);

    /// <summary>
    /// Extract the band split from a Tait <b>product code</b> (e.g. <c>TMAB12-B100_0201</c>): take the
    /// <c>[A-Z][0-9]</c> pair immediately after the first <c>-</c> and match it against
    /// <see cref="All"/>. Returns <c>false</c> for a null/blank code, a code with no <c>-</c> or no
    /// two-character <c>[A-Z][0-9]</c> designator after it (malformed), or a well-formed designator
    /// that names no known split (unknown).
    /// </summary>
    public static bool TryParseProductCode(string? productCode, out TaitBand band)
    {
        band = null!;
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return false;
        }

        int dash = productCode.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 || dash + 2 >= productCode.Length)
        {
            return false;
        }

        char letter = productCode[dash + 1];
        char digit = productCode[dash + 2];
        if (!char.IsAsciiLetterUpper(letter) || !char.IsAsciiDigit(digit))
        {
            return false;
        }

        return ByCode.TryGetValue(new string([letter, digit]), out band!);
    }

    /// <summary>
    /// Extract the band split from a radio's assembled <see cref="TaitRadioIdentity"/> — its
    /// <see cref="TaitRadioIdentity.Versions"/> record <c>[00]</c> product code. Returns <c>false</c>
    /// (leaving <paramref name="band"/> null) when the radio reported no product-code record, or the
    /// code is malformed / names no known split (see <see cref="TryParseProductCode"/>).
    /// </summary>
    public static bool TryParse(TaitRadioIdentity identity, out TaitBand band)
    {
        band = null!;
        if (identity is null)
        {
            return false;
        }

        return identity.Versions.TryGetValue(ProductCodeRecord, out var productCode)
            && TryParseProductCode(productCode, out band);
    }
}
