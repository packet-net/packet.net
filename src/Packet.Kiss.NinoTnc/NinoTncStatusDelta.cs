namespace Packet.Kiss.NinoTnc;

/// <summary>
/// Per-register differences between two <see cref="NinoTncStatusFrame"/>
/// snapshots (later minus earlier). Each field is <c>null</c> when either
/// snapshot lacked the register. The registers are since-boot counters, so
/// a delta across a known activity window isolates that window's traffic —
/// e.g. the preamble words spent on one transmission, which is how the
/// effective TXDELAY is measured (see <see cref="PreambleSeconds"/>).
/// </summary>
public sealed record NinoTncStatusDelta
{
    /// <summary>Uptime advance in milliseconds (register 02).</summary>
    public long? UptimeMs { get; init; }

    /// <summary>AX.25 packets received (register 07).</summary>
    public long? Ax25RxPackets { get; init; }

    /// <summary>IL2P packets received and corrected (register 08).</summary>
    public long? Il2pRxCorrectable { get; init; }

    /// <summary>IL2P packets received with uncorrectable errors (register 09).</summary>
    public long? Il2pRxUncorrectable { get; init; }

    /// <summary>Packets transmitted (register 0A).</summary>
    public long? TxPackets { get; init; }

    /// <summary>Preamble words transmitted (register 0B; 16 bits each).</summary>
    public long? PreambleWordCount { get; init; }

    /// <summary>Firmware main-loop cycles (register 0C).</summary>
    public long? LoopCycles { get; init; }

    /// <summary>PTT-asserted milliseconds (register 0D).</summary>
    public long? PttOnMs { get; init; }

    /// <summary>DCD-asserted milliseconds (register 0E).</summary>
    public long? DcdOnMs { get; init; }

    /// <summary>Bytes received (register 0F).</summary>
    public long? RxBytes { get; init; }

    /// <summary>Bytes transmitted (register 10).</summary>
    public long? TxBytes { get; init; }

    /// <summary>IL2P bytes repaired by FEC (register 11).</summary>
    public long? Il2pFecCorrectedBytes { get; init; }

    /// <summary>
    /// Compute the per-register deltas between two snapshots
    /// (<paramref name="after"/> minus <paramref name="before"/>).
    /// </summary>
    public static NinoTncStatusDelta Between(NinoTncStatusFrame before, NinoTncStatusFrame after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return new NinoTncStatusDelta
        {
            UptimeMs = Diff(before.UptimeMs, after.UptimeMs),
            Ax25RxPackets = Diff(before.Ax25RxPackets, after.Ax25RxPackets),
            Il2pRxCorrectable = Diff(before.Il2pRxCorrectable, after.Il2pRxCorrectable),
            Il2pRxUncorrectable = Diff(before.Il2pRxUncorrectable, after.Il2pRxUncorrectable),
            TxPackets = Diff(before.TxPackets, after.TxPackets),
            PreambleWordCount = Diff(before.PreambleWordCount, after.PreambleWordCount),
            LoopCycles = Diff(before.LoopCycles, after.LoopCycles),
            PttOnMs = Diff(before.PttOnMs, after.PttOnMs),
            DcdOnMs = Diff(before.DcdOnMs, after.DcdOnMs),
            RxBytes = Diff(before.RxBytes, after.RxBytes),
            TxBytes = Diff(before.TxBytes, after.TxBytes),
            Il2pFecCorrectedBytes = Diff(before.Il2pFecCorrectedBytes, after.Il2pFecCorrectedBytes),
        };
    }

    /// <summary>
    /// The seconds of preamble represented by <see cref="PreambleWordCount"/>
    /// at the given over-air bit rate: seconds = words × 16 ÷ bit rate.
    /// <c>null</c> when the register was missing on either snapshot.
    /// </summary>
    /// <param name="bitRateHz">The mode's raw bit rate, e.g. 1200 — see <see cref="NinoTncMode.BitRateHz"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Bit rate is zero or negative.</exception>
    public double? PreambleSeconds(int bitRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitRateHz);
        return PreambleWordCount is { } words ? words * 16.0 / bitRateHz : null;
    }

    private static long? Diff(long? before, long? after) =>
        before.HasValue && after.HasValue ? after.Value - before.Value : null;
}
