namespace Packet.Ax25.Monitor;

/// <summary>How much an <see cref="Ax25LinkObserver"/> remembers.</summary>
public sealed record Ax25LinkObserverOptions
{
    /// <summary>Frames kept per link for <see cref="Ax25LinkSnapshot.Recent"/>. Default 100.</summary>
    public int RecentPerLink { get; init; } = 100;

    /// <summary>Links kept at once; the quietest is dropped to make room. Default 200.</summary>
    public int MaxLinks { get; init; } = 200;

    /// <summary>
    /// A link with nothing heard on it for this long is forgotten the next time a frame is
    /// observed. Measured against the timestamps the caller supplies, not a clock, so a replay
    /// of logged frames ages links by the log's time and not by the replay's. Default 6 hours.
    /// </summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long a call (SABM/SABME) or a hang-up (DISC) may go unanswered before
    /// <see cref="Ax25LinkObserver.Expire"/> gives up on it and takes the link to be down. A
    /// station retries a call N2 times at T1 (ten times at ten seconds is the common default,
    /// and with linear backoff the last gap is under two minutes), and every retry heard
    /// restarts this clock, so silence for this long after the last one means the caller has
    /// stopped, or the answer went by unheard. Measured against the timestamps the caller
    /// supplies, like <see cref="Lifetime"/>. Default 3 minutes.
    /// </summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(3);
}
