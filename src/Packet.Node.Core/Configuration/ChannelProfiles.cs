namespace Packet.Node.Core.Configuration;

/// <summary>
/// Named, <b>opt-in</b> per-port channel-tuning profiles. A profile is a small,
/// documented bundle of AX.25 timer + KISS CSMA defaults suited to a class of
/// physical channel. It only ever <em>fills in fields the operator left unset</em>
/// — an explicit value on the port always wins.
/// <para>
/// A profile captures <em>channel</em> properties (timing under contention, CSMA,
/// TX warm-up). It deliberately does <b>not</b> set a non-zero TX tail: the need for
/// one is a <em>modem + radio-audio-path</em> property (software modems and latency
/// audio paths need one; a fully analogue path doesn't) that the node can't infer
/// from the channel — so a non-zero <c>kiss.txTail</c> stays an explicit per-port
/// operator override. The default tail is an <b>implicit 0</b> sent to the modem on
/// every apply (#465); the resolver supplies that 0 when neither the operator nor
/// the profile set one, so a profiled port still gets a deterministic explicit tail.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, and why it is opt-in.</b> The AX.25 spec's default T1 (6 s)
/// stalls connected-mode on a slow half-duplex AFSK channel: two stations whose T1
/// timers are equal and phase-locked collide on every poll and the QSO never
/// converges (the net-sim lab finding, packet.net #292; reproduced by
/// <c>HalfDuplexContentionTests</c>). The cure is a channel-appropriate, slightly
/// <em>asymmetric</em> T1 (so the two stations drift in and out of phase and find
/// clear air) plus sane CSMA.
/// </para>
/// <para>
/// We do <b>not</b> bake this into the engine — the engine stays spec-compliant by
/// default (working agreement §2). Nor do we apply it as a silent node-wide
/// default: T1 and TXDELAY are properties of the <em>physical channel</em>, and a
/// single node can host fast ports (AXUDP, 9600, full-duplex) and slow ports
/// (AFSK1200 half-duplex) at once — a blanket default would be wrong for the fast
/// ones. So tuning is a <em>named, per-port</em> choice the operator makes
/// (<c>profile: slow-afsk1200</c>), with a documented effect. Absent a profile, a
/// port gets exact spec defaults (or whatever the operator set explicitly).
/// </para>
/// <para>
/// This is the §2 pattern applied at the node-host layer: spec-compliant by
/// default; pragmatism behind a name.
/// </para>
/// </remarks>
public static class ChannelProfiles
{
    /// <summary>The profile name for a slow, half-duplex 1200-baud AFSK channel —
    /// the classic VHF packet channel the #292 stall was found on.</summary>
    public const string SlowAfsk1200 = "slow-afsk1200";

    /// <summary>
    /// Resolve a port's <em>effective</em> AX.25 + KISS parameters by overlaying its
    /// explicit values on top of its profile's defaults. Explicit wins; the profile
    /// fills only the gaps. A null / empty / unknown profile is a pure pass-through
    /// (returns the port's own params unchanged — spec defaults downstream).
    /// </summary>
    /// <param name="port">The port whose params to resolve.</param>
    /// <returns>The effective <see cref="Ax25PortParams"/> and <see cref="KissParams"/>
    /// to feed the listener / modem (either may be null if neither the profile nor the
    /// operator set anything).</returns>
    public static (Ax25PortParams? Ax25, KissParams? Kiss) Resolve(PortConfig port)
    {
        ArgumentNullException.ThrowIfNull(port);
        var defaults = DefaultsFor(port.Profile);
        if (defaults is null)
        {
            return (port.Ax25, port.Kiss);
        }
        return (OverlayAx25(port.Ax25, defaults.Value.Ax25), OverlayKiss(port.Kiss, defaults.Value.Kiss));
    }

    /// <summary>True if <paramref name="profile"/> names a known profile (case- and
    /// hyphen-insensitive). Null/empty is "no profile" — also valid. Used by the
    /// validator to reject a typo'd profile name.</summary>
    public static bool IsKnown(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return true;   // absent profile is legal
        }
        return DefaultsFor(profile) is not null;
    }

    /// <summary>The set of recognised profile names (for the validator's error
    /// message + docs). Excludes the "no profile" empty case.</summary>
    public static IReadOnlyList<string> Names { get; } = [SlowAfsk1200];

    private static (Ax25PortParams Ax25, KissParams Kiss)? DefaultsFor(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return null;
        }

        return Normalise(profile) switch
        {
            // slow-afsk1200: the #292 channel. A longer T1 (10 s) — deliberately
            // above the spec 6 s AND above common peer defaults (LinBPQ FRACK,
            // direwolf), so two stations' timers run asymmetric and break the
            // phase-lock that stalls a contended half-duplex QSO. N2=15 widens the
            // retry budget for a noisy slow link. CSMA values are the KISS spec
            // defaults (PERSIST=63 / SLOTTIME=10), confirmed good enough for
            // two-station contention by the NinoTNC characterisation; stated here
            // to make the intent explicit. TXDELAY=30 (300 ms) is a generic
            // AFSK1200 TX warm-up — less than the over-conservative 500 ms spec
            // default, comfortably above a fast modem's floor (operator should
            // tune down for a known-fast TNC).
            //
            // NB this profile deliberately does NOT set a non-zero TX tail. The need
            // for one is a property of the MODEM + the radio's audio-path latency, NOT
            // of the channel or the baud rate: a software modem (samoyed / Dire Wolf)
            // needs one, and so does a NinoTNC into a radio with a latency audio path —
            // but a NinoTNC into a fully analogue audio path needs none, even on this
            // exact slow AFSK1200 channel. The node can't infer the audio-path latency,
            // so a non-zero TXTAIL stays an explicit per-port operator override
            // (`kiss.txTail`), documented in the config template. The profile leaving it
            // unset resolves (with the operator also unset) to the implicit 0 default
            // sent on every apply (#465) — not bundled into the channel profile.
            "slowafsk1200" => (
                new Ax25PortParams { T1Ms = 10000, N2 = 15 },
                new KissParams { TxDelay = 30, Persistence = 63, SlotTime = 10 }),
            _ => null,
        };
    }

    // Explicit value wins; otherwise take the profile's. Returns null only when
    // both sides contribute nothing.
    private static Ax25PortParams? OverlayAx25(Ax25PortParams? explicitParams, Ax25PortParams profile)
    {
        var e = explicitParams;
        return new Ax25PortParams
        {
            T1Ms = e?.T1Ms ?? profile.T1Ms,
            T2Ms = e?.T2Ms ?? profile.T2Ms,
            T3Ms = e?.T3Ms ?? profile.T3Ms,
            N2 = e?.N2 ?? profile.N2,
            WindowSize = e?.WindowSize ?? profile.WindowSize,
            N1 = e?.N1 ?? profile.N1,
            MaxCachedPeers = e?.MaxCachedPeers ?? profile.MaxCachedPeers,
        };
    }

    private static KissParams? OverlayKiss(KissParams? explicitParams, KissParams profile)
    {
        var e = explicitParams;
        return new KissParams
        {
            TxDelay = e?.TxDelay ?? profile.TxDelay,
            Persistence = e?.Persistence ?? profile.Persistence,
            SlotTime = e?.SlotTime ?? profile.SlotTime,
            // TxTail has an IMPLICIT default of 0 (#465): a channel profile still does
            // not SET a tail (a non-zero tail is a modem/audio-path-latency property no
            // profile can know — see the class remarks), but the resolved value resolves
            // to a deterministic 0 when neither the operator nor the profile set one, so
            // a profiled port gets an explicit 0 sent to its modem rather than nothing.
            // The explicit per-port override (e?.TxTail) still wins.
            TxTail = e?.TxTail ?? profile.TxTail ?? 0,
            // No profile sets ackMode (no profile knows your link is half-duplex +
            // ACKMODE-capable); carry the explicit per-port choice straight through.
            AckMode = e?.AckMode ?? profile.AckMode,
            // Same story for t1FromTxComplete: ACKMODE capability is a per-link
            // fact no profile can know; the explicit per-port choice carries.
            T1FromTxComplete = e?.T1FromTxComplete ?? profile.T1FromTxComplete,
        };
    }

    private static string Normalise(string raw) =>
        raw.Replace("-", "", StringComparison.Ordinal)
           .Replace("_", "", StringComparison.Ordinal)
           .ToLowerInvariant();
}
