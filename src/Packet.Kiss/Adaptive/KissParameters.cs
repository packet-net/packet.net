namespace Packet.Kiss.Adaptive;

/// <summary>
/// A set of KISS modem parameters the host can configure on a TNC (KISS
/// commands 0x01–0x04 — the CSMA/timing knobs documented in the KISS TNC
/// protocol). Nullable fields mean "no opinion, keep the TNC's current
/// value" — useful when only some knobs are adaptive and others are
/// statically configured.
/// </summary>
/// <param name="TxDelayTenMsUnits">
/// TXDELAY (cmd 0x01) — length of the TX preamble in 10 ms units. KISS spec
/// default is 50 (= 500 ms). Adaptive estimators target this most aggressively
/// because it's the largest per-frame cost on slow modes.
/// </param>
/// <param name="Persistence">
/// PERSIST (cmd 0x02) — CSMA persistence parameter (0–255). Higher = more
/// aggressive. Probability of transmitting in any free slot is roughly
/// (P + 1) / 256. KISS spec default is 63.
/// </param>
/// <param name="SlotTimeTenMsUnits">
/// SLOTTIME (cmd 0x03) — CSMA back-off slot time in 10 ms units. KISS spec
/// default is 10 (= 100 ms).
/// </param>
/// <param name="TxTailTenMsUnits">
/// TXTAIL (cmd 0x04) — keep-down period after TX, in 10 ms units. Mostly
/// obsolete on modern modems; the spec recommends 0.
/// </param>
public readonly record struct KissParameters(
    byte? TxDelayTenMsUnits,
    byte? Persistence,
    byte? SlotTimeTenMsUnits,
    byte? TxTailTenMsUnits)
{
    /// <summary>The conservative defaults from the KISS TNC protocol spec.</summary>
    public static KissParameters SpecDefaults => new(
        TxDelayTenMsUnits: 50,
        Persistence: 63,
        SlotTimeTenMsUnits: 10,
        TxTailTenMsUnits: 0);

    /// <summary>
    /// Yield <paramref name="other"/>'s non-null values on top of this set.
    /// Used to combine a static baseline with adaptive overrides.
    /// </summary>
    public KissParameters Override(KissParameters other) => new(
        other.TxDelayTenMsUnits ?? TxDelayTenMsUnits,
        other.Persistence ?? Persistence,
        other.SlotTimeTenMsUnits ?? SlotTimeTenMsUnits,
        other.TxTailTenMsUnits ?? TxTailTenMsUnits);
}
