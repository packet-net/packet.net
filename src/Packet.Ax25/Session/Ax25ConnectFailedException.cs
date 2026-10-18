using Packet.Core;

namespace Packet.Ax25.Session;

/// <summary>
/// Why an outbound <see cref="Ax25Listener.ConnectAsync(Callsign, CancellationToken)"/> ended
/// without DL-CONNECT confirm, as the data link saw it (packet-net/packet.net#849).
/// </summary>
public enum Ax25ConnectFailure
{
    /// <summary>
    /// The peer answered the SABM(E) with DM: it is there, and it refused the link (busy, or
    /// not accepting connections). Awaiting Connection's DM arm (figc4.2 / figc4.6) raises
    /// DL-DISCONNECT indication with no error before it.
    /// </summary>
    Refused,

    /// <summary>
    /// The peer never answered: T1 expired N2 times. Awaiting Connection's RC == N2 arm raises
    /// DL-ERROR indication (G) and then DL-DISCONNECT indication.
    /// </summary>
    NoAnswer,

    /// <summary>
    /// The link went down before it came up for any other reason: a disconnect from this end
    /// (DL-DISCONNECT confirm), or a teardown with no DM and no retry exhaustion.
    /// </summary>
    LinkReset,
}

/// <summary>
/// An outbound connect that the data link ended before DL-CONNECT confirm, with the reason
/// (<see cref="Failure"/>). A connect that ran out of its own time budget is a
/// <see cref="TimeoutException"/> instead, as before. Derives from
/// <see cref="InvalidOperationException"/>, which is what this failure was thrown as before
/// the reason was carried, so existing handlers still catch it.
/// </summary>
public sealed class Ax25ConnectFailedException : InvalidOperationException
{
    public Ax25ConnectFailedException(Callsign remote, Ax25ConnectFailure failure)
        : base(Describe(remote, failure))
    {
        Remote = remote;
        Failure = failure;
    }

    /// <summary>The station that was dialled.</summary>
    public Callsign Remote { get; }

    /// <summary>Why the connect failed.</summary>
    public Ax25ConnectFailure Failure { get; }

    private static string Describe(Callsign remote, Ax25ConnectFailure failure) => failure switch
    {
        Ax25ConnectFailure.Refused => $"outbound connect to {remote} refused: the peer answered DM (busy or not accepting).",
        Ax25ConnectFailure.NoAnswer => $"outbound connect to {remote} got no answer: T1 expired N2 times.",
        _ => $"outbound connect to {remote} torn down before DL-CONNECT-confirm arrived (link reset).",
    };
}
