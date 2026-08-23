using System.Globalization;
using M0LTE.Tait.Codeplug;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios.Programming;

/// <summary>
/// A validated, normalised programming plan: exactly what a run will write, in the codeplug
/// library's own units and enums. Parsing (<see cref="TryParse"/>) is pure and happens on the
/// request thread, so a typo is a 400 <b>before</b> the port is taken off the air.
/// </summary>
/// <remarks>
/// <para>
/// <b>One channel, by default.</b> The plan replaces the radio's channel table with a single
/// channel, which is what packet.net#779 asked for: a PDN port drives one frequency, and a leftover
/// channel 3 from a previous life is only ever a way to be transmitting somewhere unexpected.
/// Shrinking happens from the top so channel 0 - the one the plan then fills in - is the survivor.
/// </para>
/// <para>
/// <b><see cref="ReplaceChannelTable"/> = false patches channel 0 in place</b> and leaves the rest
/// of the table alone. Resizing a channel table is the newest thing in the codeplug library and the
/// part with the least hardware behind it, so it is worth being able to take it out of the picture:
/// the narrower write is the one the library's own `patch` verb has been run against a radio with.
/// </para>
/// <para>
/// <b>Subaudible tones are cleared</b> on that channel. A packet channel is carrier-squelch: a
/// CTCSS/DCS RX tone inherited from whatever the radio used to do would mute every frame from a peer
/// that does not send it, and the symptom (a radio that hears nothing, with no error anywhere) is
/// horrible to diagnose. This is a deliberate, documented side effect - it is in
/// <see cref="TaitProgramCaveat"/>, in the API docs and on the panel. A deployment that genuinely
/// needs a tone programs it with the <c>tait-codeplug</c> CLI.
/// </para>
/// <para>
/// Everything else on the channel (squelch tightness, TX inhibit, the network number) is left
/// exactly as it was: those are site policy, and this panel has no opinion about them.
/// </para>
/// </remarks>
public sealed record TaitProgramPlan
{
    /// <summary>Receive frequency, Hz.</summary>
    public required long RxFrequencyHz { get; init; }

    /// <summary>Transmit frequency, Hz. Equal to <see cref="RxFrequencyHz"/> on a simplex channel.</summary>
    public required long TxFrequencyHz { get; init; }

    /// <summary>Channel bandwidth.</summary>
    public required Bandwidth Bandwidth { get; init; }

    /// <summary>Transmit power step.</summary>
    public required PowerLevel Power { get; init; }

    /// <summary>The PDN upgrade profile to apply on top of the channel.</summary>
    public required TaitPdnProfile Profile { get; init; }

    /// <summary>Whether to delete the radio's other channels, leaving only the one written.</summary>
    public required bool ReplaceChannelTable { get; init; }

    /// <summary>The lowest frequency any known Tait band split covers - the floor a request has to
    /// clear before the port is touched. The radio's OWN split is checked later, once the
    /// interrogate has read its product code (<see cref="CheckBand"/>).</summary>
    public static long MinPlausibleHz { get; } = TaitBandCatalog.All.Min(b => (long)b.MinHz);

    /// <summary>The highest frequency any known Tait band split covers.</summary>
    public static long MaxPlausibleHz { get; } = TaitBandCatalog.All.Max(b => (long)b.MaxHz);

    /// <summary>
    /// Validate a request into a plan. Returns false with an operator-facing
    /// <paramref name="error"/> (the API's 400 body) rather than throwing - a bad form field is a
    /// normal outcome here, not an exception.
    /// </summary>
    /// <param name="request">The request body, which may be null (a malformed POST).</param>
    /// <param name="plan">The validated plan on success.</param>
    /// <param name="error">Why the request was refused, on failure.</param>
    public static bool TryParse(TaitProgramRequest? request, out TaitProgramPlan plan, out string error)
    {
        plan = null!;
        error = string.Empty;

        if (request is null)
        {
            error = "a programming request body is required";
            return false;
        }

        if (request.RxFrequencyHz is not { } rx || !IsPlausible(rx))
        {
            error = $"rxFrequencyHz must be a frequency a Tait TM8100/TM8200 can cover ({Describe(MinPlausibleHz)}-{Describe(MaxPlausibleHz)})";
            return false;
        }

        long tx = request.TxFrequencyHz ?? rx;
        if (!IsPlausible(tx))
        {
            error = $"txFrequencyHz must be a frequency a Tait TM8100/TM8200 can cover ({Describe(MinPlausibleHz)}-{Describe(MaxPlausibleHz)}), or be omitted for simplex";
            return false;
        }

        if (!TryParseBandwidth(request.Bandwidth, out var bandwidth))
        {
            error = "bandwidth must be one of: narrow, medium, wide";
            return false;
        }

        if (!TryParsePower(request.Power, out var power))
        {
            error = "power must be one of: verylow, low, medium, high";
            return false;
        }

        if (!TryParseProfile(request.Profile, out var profile))
        {
            error = "profile must be one of: none, pdn-basic, pdn-extra";
            return false;
        }

        plan = new TaitProgramPlan
        {
            RxFrequencyHz = rx,
            TxFrequencyHz = tx,
            Bandwidth = bandwidth,
            Power = power,
            Profile = profile,
            ReplaceChannelTable = request.ReplaceChannelTable ?? true,
        };
        return true;
    }

    /// <summary>
    /// Check this plan's frequencies against the band split of the radio that actually answered -
    /// its product code, read by the interrogate (e.g. <c>TMAB12-B100_0201</c> → <c>B1</c> →
    /// 136-174 MHz). Returns null when the plan fits, or the refusal reason when it does not.
    /// </summary>
    /// <remarks>
    /// An unrecognised or absent product code returns null: the codeplug write path has its own
    /// database-version guard, and refusing to program a radio whose model string we could not
    /// parse would be a worse failure than letting a deliberate operator through. Typing a 70 cm
    /// frequency into a 2 m radio - the mistake actually worth catching - is caught.
    /// </remarks>
    /// <param name="productCode">The radio's product code, from the codeplug identity block.</param>
    public string? CheckBand(string? productCode)
    {
        if (!TaitBandCatalog.TryParseProductCode(productCode, out var band))
        {
            return null;
        }

        foreach (var (label, hz) in new[] { ("rx", RxFrequencyHz), ("tx", TxFrequencyHz) })
        {
            if (hz < band.MinHz || hz > band.MaxHz)
            {
                return $"the radio is a {band.Code} band split ({Describe(band.MinHz)}-{Describe(band.MaxHz)}" +
                    (band.AmateurBand is { } amateur ? $", {amateur}" : string.Empty) +
                    $"), which does not cover the {label} frequency {Describe(hz)}. Nothing was written.";
            }
        }

        return null;
    }

    /// <summary>
    /// Apply this plan to an open codeplug: shrink the channel table to one channel, write the
    /// channel, then lay the PDN profile (if any) on top. Pure field edits - the caller writes the
    /// image back.
    /// </summary>
    /// <param name="fields">The codeplug read off the radio.</param>
    public void ApplyTo(CodeplugFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        // Shrink from the top: RemoveChannel shifts the ones above it down, and it refuses to
        // remove the last one, so this converges on channel 0 whatever the radio arrived with.
        if (ReplaceChannelTable)
        {
            while (fields.ChannelCount > 1)
            {
                fields.RemoveChannel(fields.ChannelCount - 1);
            }
        }

        // Order matters: the split-TX flag decides whether the TX frequency field is honoured at
        // all, so set it before the frequency it governs.
        fields.SetRxFrequencyHz(0, RxFrequencyHz);
        fields.SetSeparateTxFrequency(0, TxFrequencyHz != RxFrequencyHz);
        fields.SetTxFrequencyHz(0, TxFrequencyHz);
        fields.SetBandwidth(0, Bandwidth);
        fields.SetPowerLevel(0, Power);

        // Carrier squelch - see the type-level remarks.
        fields.SetRxSubaudibleNone(0);
        fields.SetTxSubaudibleNone(0);

        switch (Profile)
        {
            case TaitPdnProfile.Basic:
                fields.ApplyPdnBasic();
                break;
            case TaitPdnProfile.Extra:
                fields.ApplyPdnExtra();
                break;
            case TaitPdnProfile.None:
            default:
                break;
        }
    }

    /// <summary>This plan as the API projects it.</summary>
    public TaitProgramPlanInfo ToWire() => new(
        RxFrequencyHz, TxFrequencyHz, BandwidthToWire(Bandwidth), PowerToWire(Power), ProfileToWire(Profile),
        ReplaceChannelTable);

    /// <summary>A one-line summary for the audit log and the run's opening feed line.</summary>
    public override string ToString() =>
        $"rx={Describe(RxFrequencyHz)} tx={Describe(TxFrequencyHz)} " +
        $"bandwidth={BandwidthToWire(Bandwidth)} power={PowerToWire(Power)} profile={ProfileToWire(Profile)} " +
        $"channels={(ReplaceChannelTable ? "replace" : "keep")}";

    /// <summary>A frequency in MHz to 6 dp, trailing zeros trimmed - how an operator reads one.</summary>
    internal static string Describe(long hz) =>
        (hz / 1_000_000.0).ToString("0.######", CultureInfo.InvariantCulture) + " MHz";

    private static bool IsPlausible(long hz) => hz >= MinPlausibleHz && hz <= MaxPlausibleHz;

    private static bool TryParseBandwidth(string? value, out Bandwidth bandwidth)
    {
        switch (Normalise(value))
        {
            case "narrow": bandwidth = Bandwidth.Narrow; return true;
            case "medium": bandwidth = Bandwidth.Medium; return true;
            case "wide": bandwidth = Bandwidth.Wide; return true;
            default: bandwidth = default; return false;
        }
    }

    private static bool TryParsePower(string? value, out PowerLevel power)
    {
        // `off` is a real codeplug value and deliberately not accepted: it would program a channel
        // the radio cannot transmit on, which is never what this panel is being asked for.
        switch (Normalise(value))
        {
            case "verylow": power = PowerLevel.VeryLow; return true;
            case "low": power = PowerLevel.Low; return true;
            case "medium": power = PowerLevel.Medium; return true;
            case "high": power = PowerLevel.High; return true;
            default: power = default; return false;
        }
    }

    private static bool TryParseProfile(string? value, out TaitPdnProfile profile)
    {
        switch (Normalise(value))
        {
            case "": case "none": profile = TaitPdnProfile.None; return true;
            case "pdn-basic": case "pdnbasic": case "basic": profile = TaitPdnProfile.Basic; return true;
            case "pdn-extra": case "pdnextra": case "extra": profile = TaitPdnProfile.Extra; return true;
            default: profile = default; return false;
        }
    }

    private static string Normalise(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>The wire spelling of a bandwidth (shared with <see cref="TaitCodeplugReader"/>).</summary>
    internal static string BandwidthToWire(Bandwidth bandwidth) => bandwidth switch
    {
        Bandwidth.Narrow => "narrow",
        Bandwidth.Medium => "medium",
        Bandwidth.Wide => "wide",
        _ => "unknown",
    };

    /// <summary>The wire spelling of a power step (shared with <see cref="TaitCodeplugReader"/>).</summary>
    internal static string PowerToWire(PowerLevel power) => power switch
    {
        PowerLevel.Off => "off",
        PowerLevel.VeryLow => "verylow",
        PowerLevel.Low => "low",
        PowerLevel.Medium => "medium",
        PowerLevel.High => "high",
        _ => "unknown",
    };

    private static string ProfileToWire(TaitPdnProfile profile) => profile switch
    {
        TaitPdnProfile.Basic => "pdn-basic",
        TaitPdnProfile.Extra => "pdn-extra",
        _ => "none",
    };
}
