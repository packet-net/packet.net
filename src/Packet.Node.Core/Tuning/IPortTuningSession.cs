using System.Threading.Channels;
using Packet.Node.Core.Api;

namespace Packet.Node.Core.Tuning;

/// <summary>
/// The registry-facing shape of a live tuning session on a port — what the
/// one-session-per-port bookkeeping (<see cref="PortTuningRegistry"/>), the shared
/// SSE feed (<c>GET /api/v1/ports/{id}/tuning/events</c>) and the shared stop verbs
/// need, independent of what the session actually does. Two implementations:
/// <see cref="PortTuningSession"/> (guided deviation tuning) and
/// <see cref="TxDelayMinPortSession"/> (TXDELAY minimisation / apply). Whatever the
/// flavour, a session <b>always restores its port on every exit path</b>.
/// </summary>
public interface IPortTuningSession
{
    /// <summary>The port this session runs on (the registry key).</summary>
    string PortId { get; }

    /// <summary>The API projection of this session's current state.</summary>
    TuningSessionInfo Info { get; }

    /// <summary>Stop the session and restore the port. Returns once the port has been
    /// restored. Idempotent.</summary>
    ValueTask StopAsync();

    /// <summary>Subscribe to the live event feed: full history replay, then live
    /// events; the reader completes when the session is (or already was) terminal.</summary>
    IDisposable Subscribe(out ChannelReader<TuningEvent> reader);
}
