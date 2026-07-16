using Packet.SoundModem.Modems;

namespace Packet.Node.Core.Transports;

/// <summary>
/// The node's per-frame projection of pdn-soundmodem's <see cref="FrameQuality"/> — the
/// receive diagnostics the in-process soundmodem attaches to every decoded frame (the RS
/// correction counts the deframers always computed and used to discard). Timestamped with
/// the frame's capture instant so it lines up with the matching <c>Ax25InboundFrame</c>.
/// </summary>
/// <remarks>
/// This is deliberately NOT a bit-error rate: true BER is unobservable at a receiver (an RS
/// codeword hides how many bits inside a corrected symbol flipped, and frames damaged past the
/// code's reach never decode at all). <see cref="CorrectedBytes"/> per frame is an honest FEC
/// floor on the channel's byte-error rate — zero on a clean link, and a persistently non-zero
/// value is a link quietly spending its error budget before frames start dropping. That is the
/// early-warning signal worth surfacing.
/// </remarks>
/// <param name="ReceivedAt">Capture instant of the frame these diagnostics belong to (the same
/// <c>Ax25InboundFrame.ReceivedAt</c> stamp).</param>
/// <param name="Mode">The mode — and, for a multi-decoder bank, the winning branch — that
/// decoded the frame, e.g. <c>"qpsk2400-il2pc"</c> or <c>"afsk1200@+30Hz+6dB"</c>.</param>
/// <param name="FrameBytes">Decoded AX.25 frame length in bytes.</param>
/// <param name="CorrectedBytes">Bytes forward-error-correction repaired (Reed-Solomon: IL2P /
/// FX.25 framings). <c>null</c> for unprotected framings (classic HDLC), where no error count
/// exists — an FCS pass proves zero residual errors, not an error count. <b>Never coalesced to
/// 0</b>: null (no FEC) and 0 (clean IL2P) are different facts.</param>
/// <param name="CrcValid">IL2P trailing-CRC state: true/false under IL2P+CRC, <c>null</c> where
/// the framing carries no trailer (plain IL2P, HDLC, FX.25).</param>
/// <param name="FrequencyOffsetHz">For a multi-decoder bank, the frequency offset of the branch
/// that decoded the frame; <c>null</c> for single decoders. Only directionally meaningful on
/// marginal signals — on a clean signal several branches decode and the deduper takes whichever
/// finished first, so a small non-zero value is first-past-the-post, not the far station's error.
/// A persistent, larger offset does point at an off-frequency peer.</param>
/// <param name="EmphasisDb">For a multi-decoder bank, the input pre-emphasis (dB/octave) of the
/// winning branch; <c>null</c> for single decoders. Same marginal-signal caveat as
/// <see cref="FrequencyOffsetHz"/>.</param>
public sealed record SoundModemFrameQuality(
    DateTimeOffset ReceivedAt,
    string Mode,
    int FrameBytes,
    int? CorrectedBytes,
    bool? CrcValid,
    double? FrequencyOffsetHz,
    int? EmphasisDb);

/// <summary>
/// A rolling per-port receive-quality summary for a <c>kind: soundmodem</c> port: cumulative FEC
/// counters plus the most recent frames' diagnostics. Read at scrape/status time (the metrics
/// exporter and the port quality API), so a slow consumer never touches the receive path.
/// </summary>
/// <param name="Frames">Frames that reported quality since the port came up (every decoded frame
/// does).</param>
/// <param name="CumulativeCorrectedBytes">Sum of <see cref="SoundModemFrameQuality.CorrectedBytes"/>
/// over frames that carried a count (FEC framings only; HDLC frames contribute nothing).</param>
/// <param name="FramesWithCorrections">Frames whose FEC count was greater than zero — the link
/// spending its error budget.</param>
/// <param name="LastFrameCorrectedBytes">The most recent frame's FEC count. <c>null</c> when the
/// last frame was an unprotected framing (HDLC) or none has decoded yet — <b>distinct from 0</b>
/// (a clean IL2P frame).</param>
/// <param name="Recent">The most recent frames' diagnostics, newest first, capped at a small
/// bound (the waterfall screen's rolling detail).</param>
public sealed record SoundModemQualitySnapshot(
    long Frames,
    long CumulativeCorrectedBytes,
    long FramesWithCorrections,
    int? LastFrameCorrectedBytes,
    IReadOnlyList<SoundModemFrameQuality> Recent);

/// <summary>
/// Thread-safe accumulator behind <see cref="SoundModemFrameTransport.QualitySnapshot"/>: folds
/// each decoded frame's <see cref="FrameQuality"/> into the cumulative counters and a bounded
/// newest-first ring of recent samples. Updated on the receive-pump thread, read on request
/// threads — guarded by a lock (the frame cadence is low; contention is a non-issue).
/// </summary>
internal sealed class SoundModemQualityMeter(int capacity)
{
    private readonly System.Threading.Lock _gate = new();
    private readonly LinkedList<SoundModemFrameQuality> _recent = new();
    private long _frames;
    private long _cumulativeCorrected;
    private long _framesWithCorrections;
    private int? _lastCorrected;

    /// <summary>Folds one decoded frame's quality in and returns the node projection (also handed
    /// to subscribers of <see cref="SoundModemFrameTransport.FrameQualityDecoded"/>).</summary>
    public SoundModemFrameQuality Record(FrameQuality quality, DateTimeOffset receivedAt)
    {
        var sample = new SoundModemFrameQuality(
            receivedAt, quality.Mode, quality.FrameBytes, quality.CorrectedBytes,
            quality.CrcValid, quality.FrequencyOffsetHz, quality.EmphasisDb);

        lock (_gate)
        {
            _frames++;
            // Preserve the null-vs-0 distinction: only a real FEC count moves the corrected
            // totals; a HDLC frame's null leaves them untouched and sets "last" to null.
            _lastCorrected = quality.CorrectedBytes;
            if (quality.CorrectedBytes is int corrected)
            {
                _cumulativeCorrected += corrected;
                if (corrected > 0)
                {
                    _framesWithCorrections++;
                }
            }

            _recent.AddFirst(sample);
            while (_recent.Count > capacity)
            {
                _recent.RemoveLast();
            }
        }

        return sample;
    }

    /// <summary>An immutable point-in-time snapshot for the metrics/status surfaces.</summary>
    public SoundModemQualitySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SoundModemQualitySnapshot(
                _frames, _cumulativeCorrected, _framesWithCorrections, _lastCorrected,
                [.. _recent]);
        }
    }
}
