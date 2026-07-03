using Packet.Core;
using Packet.Kiss.NinoTnc;

namespace Packet.Tune.Core;

/// <summary>
/// <see cref="IBurstStimulus"/> over a NinoTNC: transmits the burst as
/// sequential ACKMODE sends of <see cref="TuningBurst"/> frames, so the call
/// returns only after the TNC has finished keying (and the count reflects
/// frames that really went to air, not just into the queue).
/// </summary>
public sealed class NinoTncBurstStimulus : IBurstStimulus
{
    private readonly NinoTncSerialPort tnc;
    private readonly Callsign source;
    private readonly TimeSpan perFrameTimeout;

    /// <summary>Create over an open TNC connection (lifetime stays the caller's).</summary>
    /// <param name="tnc">The TNC that transmits the bursts.</param>
    /// <param name="source">Source callsign for the burst frames.</param>
    /// <param name="perFrameTimeout">ACKMODE completion timeout per frame. Null = 20 s.</param>
    public NinoTncBurstStimulus(NinoTncSerialPort tnc, Callsign source, TimeSpan? perFrameTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(tnc);
        this.tnc = tnc;
        this.source = source;
        this.perFrameTimeout = perFrameTimeout ?? TimeSpan.FromSeconds(20);
    }

    /// <inheritdoc/>
    public async Task<int> FireBurstAsync(int frames, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        int completed = 0;
        for (int i = 1; i <= frames; i++)
        {
            byte[] wire = TuningBurst.BuildFrame(source, i, frames).ToBytes();
            try
            {
                await tnc.SendFrameWithAckAsync(wire, perFrameTimeout, sequenceTag: null, cancellationToken)
                    .ConfigureAwait(false);
                completed++;
            }
            catch (TimeoutException)
            {
                // No TX-completion echo — the frame may still have gone out,
                // but it cannot be counted as confirmed. Keep going.
            }
        }
        return completed;
    }
}
