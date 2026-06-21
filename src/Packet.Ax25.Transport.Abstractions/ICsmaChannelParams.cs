namespace Packet.Ax25.Transport;

/// <summary>
/// Optional capability: the CSMA channel-access knobs of a shared, half-duplex radio channel
/// (the KISS TXDELAY/PERSIST/SLOTTIME/TXTAIL parameters). A facet, not core, because they are
/// meaningful ONLY on a CSMA radio channel — AXUDP, AGW (the server owns the radio), and VARA
/// have nothing to set, so they simply don't implement this and stop pretending with no-ops.
/// </summary>
/// <remarks>
/// These stay a runtime capability rather than construction-time config because they have
/// three live drivers: bring-up (applying configured values), the operator (a live set via the
/// control API), and the adaptive layer (re-tuning per-peer before a transmit). A consumer
/// feature-detects with <c>transport is ICsmaChannelParams</c>; when absent, a "set channel
/// param" request honestly reports the transport has no such control rather than a no-op success.
/// </remarks>
public interface ICsmaChannelParams
{
    /// <summary>TXDELAY (KISS 0x01), units of 10 ms — keyup-to-data delay.</summary>
    Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default);

    /// <summary>PERSIST (KISS 0x02), 0..255 — the p-persistent CSMA probability.</summary>
    Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default);

    /// <summary>SLOTTIME (KISS 0x03), units of 10 ms — the CSMA slot interval.</summary>
    Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default);

    /// <summary>
    /// TXTAIL (KISS 0x04), units of 10 ms. Modern modems generally ignore it (the KISS TNC
    /// spec recommends 0); kept so the adaptive layer can drive it on experimental setups.
    /// </summary>
    Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default);
}
