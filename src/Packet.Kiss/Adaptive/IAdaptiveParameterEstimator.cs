namespace Packet.Kiss.Adaptive;

/// <summary>
/// Learns per-peer KISS parameter recommendations from a stream of frame
/// outcomes. Implementations may be naive (a per-peer running success rate
/// driving a TXDELAY hill-climb) or sophisticated (separate timing /
/// CSMA / mode controllers, persistence-backed). The protocol-facing
/// contract is intentionally small so callers can swap in better estimators
/// over time without churning the integration points.
/// </summary>
/// <remarks>
/// <para>
/// Threading: implementations must be safe to call from concurrent callers.
/// <see cref="Recommend"/> is on the hot path (called before every TX), so
/// implementations should keep it lock-free or use a reader-friendly lock.
/// <see cref="Observe"/> is called once per TX outcome, off the hot path.
/// </para>
/// <para>
/// Persistence: estimators that want to survive restarts should accept an
/// <see cref="IPeerStateStore"/> in their constructor; the cold-start tax
/// of relearning every peer's parameters is real, especially for long-haul
/// links that don't carry much traffic.
/// </para>
/// </remarks>
public interface IAdaptiveParameterEstimator
{
    /// <summary>
    /// The estimator's recommendation for outgoing frames to <paramref name="peer"/>.
    /// Returns a parameter set whose non-null fields the caller should configure
    /// on the TNC. Fields the estimator has no opinion on are left <c>null</c>;
    /// callers compose those with their static baseline using
    /// <see cref="KissParameters.Override"/>.
    /// </summary>
    KissParameters Recommend(string peer);

    /// <summary>
    /// Record a frame outcome so the estimator can refine its recommendations.
    /// </summary>
    void Observe(FrameOutcomeSample sample);
}

/// <summary>
/// Optional persistence backing for an <see cref="IAdaptiveParameterEstimator"/>.
/// Lets the estimator survive node restarts without paying the cold-start
/// re-learning tax on every peer.
/// </summary>
/// <remarks>
/// The store is intentionally schema-light: it's a key-value (peer →
/// estimator-defined snapshot bytes) so different estimators can serialise
/// their internal state however they like. Production stores will be SQLite-
/// backed (one row per peer in <c>config.db</c>); tests use an in-memory
/// dictionary.
/// </remarks>
public interface IPeerStateStore
{
    /// <summary>Load an estimator-defined snapshot for <paramref name="peer"/>.</summary>
    byte[]? Load(string peer);

    /// <summary>Persist an estimator-defined snapshot for <paramref name="peer"/>.</summary>
    void Save(string peer, ReadOnlySpan<byte> snapshot);
}
