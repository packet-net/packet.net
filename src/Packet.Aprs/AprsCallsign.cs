using System.Diagnostics.CodeAnalysis;

namespace Packet.Aprs;

/// <summary>
/// Permissive APRS-layer callsign type that round-trips what's
/// actually seen on APRS-IS, including AX.25-invalid spellings like
/// letter SSIDs (<c>-B</c>, <c>-D</c>, <c>-T</c> for D-Star port
/// markers; <c>-H</c> for D-Rats; etc.) and longer-than-6 bases.
/// </summary>
/// <remarks>
/// <para>
/// This type is **monitor-layer / display-only**. For outbound AX.25
/// frame production, use <see cref="Packet.Core.Callsign"/> - that
/// enforces the spec's 1-6 alphanumeric base + 0-15 numeric SSID.
/// </para>
/// <para>
/// Per the corpus differential (see
/// <c>tools/Packet.AprsIs.Spike/findings.md</c>), ~31% of APRS-IS
/// frames carry sources that don't validate against the strict AX.25
/// callsign rules - direwolf rejects those outright. This type lets
/// us still surface the callsign in monitor / web-UI contexts where
/// "the bytes the gateway sent" matters more than "is this a
/// spec-compliant AX.25 address."
/// </para>
/// <para>
/// Accepted shapes:
/// <list type="bullet">
///   <item>Base: 1-9 chars, A-Z, a-z, 0-9</item>
///   <item>SSID: empty, or 1-3 chars of A-Z, a-z, 0-9 after a single dash</item>
/// </list>
/// </para>
/// </remarks>
public readonly record struct AprsCallsign
{
    /// <summary>Base callsign - case-preserving (firmware bugs sometimes ship lowercase).</summary>
    public string Base { get; }

    /// <summary>SSID as-text. Empty when no SSID; otherwise 1-3 alphanumeric chars.</summary>
    public string Ssid { get; }

    /// <summary>Construct a permissive callsign with the supplied base + SSID.</summary>
    public AprsCallsign(string @base, string ssid = "")
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(ssid);
        if (@base.Length is < 1 or > 9)
        {
            throw new ArgumentException($"APRS callsign base must be 1–9 chars (got '{@base}', length {@base.Length})", nameof(@base));
        }
        foreach (char c in @base)
        {
            if (!IsValidChar(c))
            {
                throw new ArgumentException($"APRS callsign base char '{c}' is not alphanumeric", nameof(@base));
            }
        }
        if (ssid.Length > 3)
        {
            throw new ArgumentException($"APRS SSID must be 0–3 chars (got '{ssid}')", nameof(ssid));
        }
        foreach (char c in ssid)
        {
            if (!IsValidChar(c))
            {
                throw new ArgumentException($"APRS SSID char '{c}' is not alphanumeric", nameof(ssid));
            }
        }

        Base = @base;
        Ssid = ssid;
    }

    /// <summary>
    /// Try-parse the canonical text form: <c>BASE</c> or <c>BASE-SSID</c>.
    /// Accepts AX.25-invalid spellings (letter SSIDs, lowercase letters,
    /// long bases) the strict <see cref="Packet.Core.Callsign"/> would
    /// reject.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out AprsCallsign callsign)
    {
        callsign = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string baseStr;
        string ssidStr;
        int dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            baseStr = text[..dash];
            ssidStr = text[(dash + 1)..];
        }
        else
        {
            baseStr = text;
            ssidStr = string.Empty;
        }

        if (baseStr.Length is < 1 or > 9)
        {
            return false;
        }

        foreach (char c in baseStr)
        {
            if (!IsValidChar(c))
            {
                return false;
            }
        }
        if (ssidStr.Length > 3)
        {
            return false;
        }

        foreach (char c in ssidStr)
        {
            if (!IsValidChar(c))
            {
                return false;
            }
        }

        callsign = new AprsCallsign(baseStr, ssidStr);
        return true;
    }

    /// <summary>
    /// Try to convert this permissive callsign to a strict
    /// <see cref="Packet.Core.Callsign"/>. Succeeds when the base
    /// is 1–6 uppercase alphanumeric chars and the SSID parses as
    /// a number 0–15.
    /// </summary>
    public bool TryToStrictCallsign(out Packet.Core.Callsign strict)
    {
        return Packet.Core.Callsign.TryParse(ToString(), out strict);
    }

    /// <summary>
    /// Best-effort coercion to a strict <see cref="Packet.Core.Callsign"/>.
    /// Always succeeds, applying transformations as needed:
    /// </summary>
    /// <list type="bullet">
    ///   <item>Base uppercased; non-alphanumeric chars dropped.</item>
    ///   <item>Base truncated to 6 chars if longer.</item>
    ///   <item>SSID parsed numerically; if non-numeric or > 15, replaced
    ///   with the supplied <paramref name="fallbackSsid"/> (default 1).</item>
    ///   <item>If the resulting base is empty (e.g. all non-alphanumeric),
    ///   falls back to <c>"X"</c>.</item>
    /// </list>
    /// <remarks>
    /// Used by the corpus envelope-rewrite tool to coerce APRS-invalid
    /// callsigns (D-Star <c>-D</c>, <c>-B</c>; lowercase; long bases)
    /// into a form direwolf will accept so we can A/B test payload
    /// decoding against direwolf's reference.
    /// </remarks>
    public Packet.Core.Callsign ToStrictCallsignOrCoerced(byte fallbackSsid = 1)
    {
        if (TryToStrictCallsign(out var ax25Valid))
        {
            return ax25Valid;
        }

        // Coerce base
        string baseUpper = Base.ToUpperInvariant();
        var sb = new System.Text.StringBuilder(6);
        foreach (char c in baseUpper)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                sb.Append(c);
                if (sb.Length == 6)
                {
                    break;
                }
            }
        }
        if (sb.Length == 0)
        {
            sb.Append('X');
        }

        string coercedBase = sb.ToString();

        // Coerce SSID:
        //   - empty (no SSID present): keep as 0
        //   - parseable numeric in 0..15: use that
        //   - everything else (letters, > 15): use fallback
        byte ssid;
        if (Ssid.Length == 0)
        {
            ssid = 0;
        }
        else if (byte.TryParse(Ssid, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var n) && n <= 15)
        {
            ssid = n;
        }
        else
        {
            ssid = fallbackSsid;
        }
        return new Packet.Core.Callsign(coercedBase, ssid);
    }

    /// <inheritdoc/>
    public override string ToString()
        => string.IsNullOrEmpty(Ssid) ? Base : $"{Base}-{Ssid}";

    private static bool IsValidChar(char c)
        => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');
}
