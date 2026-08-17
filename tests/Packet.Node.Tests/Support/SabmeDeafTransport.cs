using System.Runtime.CompilerServices;
using Packet.Ax25;
using Packet.Ax25.Transport;

namespace Packet.Node.Tests.Support;

/// <summary>
/// An <see cref="IAx25Transport"/> decorator that makes the station behind it <b>deaf to
/// SABME</b>: an inbound v2.2 connect request is silently dropped before the listener ever sees
/// it, while a plain v2.0 SABM passes through and is answered normally.
/// </summary>
/// <remarks>
/// <para>
/// This is the BPQ / LinBPQ behaviour the GB7RDG cutover ran into on air, and the one case the
/// engine cannot handle on its own: a DM (peer refused) or an FRMR degrade to v2.0 inside the SDL
/// (<c>Ax25Spec48</c> / <c>Ax25Spec45</c>), but <b>no answer at all</b> just burns the dial's
/// (N2+1) x T1V budget, and AX.25 v2.2 §6.3.1 forbids the engine degrading silently. Wrapping the
/// peer's transport reproduces it exactly without teaching any real station to misbehave: the
/// SABME is transmitted, reaches the medium, and is ignored.
/// </para>
/// <para>
/// Only the RECEIVE path is filtered - everything the peer sends goes out untouched, so its UA to
/// the SABM is a genuine one from a real <c>Ax25Listener</c>.
/// </para>
/// </remarks>
public sealed class SabmeDeafTransport(IAx25Transport inner) : IAx25Transport
{
    /// <summary>How many SABMEs this station has swallowed - the wire-level proof that the caller
    /// really did offer v2.2 and got nothing back.</summary>
    public int SabmesIgnored => Volatile.Read(ref sabmesIgnored);

    private int sabmesIgnored;

    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        => inner.SendAsync(ax25, cancellationToken);

    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IsSabme(frame.Ax25.Span))
            {
                Interlocked.Increment(ref sabmesIgnored);
                continue;   // the station simply never heard it
            }

            yield return frame;
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    // SABME is the U-frame control octet 0x6F once the P/F bit is masked out. Parse rather than
    // index a fixed offset so a digipeated address field could never shift the control octet out
    // from under the check.
    private static bool IsSabme(ReadOnlySpan<byte> ax25) =>
        Ax25Frame.TryParse(ax25, out var frame) && (frame.Control & 0xEF) == 0x6F;
}
