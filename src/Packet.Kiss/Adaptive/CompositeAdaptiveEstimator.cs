namespace Packet.Kiss.Adaptive;

/// <summary>
/// Combine multiple estimators into one. Each child's
/// <see cref="IAdaptiveParameterEstimator.Recommend"/> is queried and the
/// non-null fields of each are stacked via
/// <see cref="KissParameters.Override"/> in order — later children win
/// on overlapping fields, which is rare in practice since each child
/// usually owns a disjoint set of parameters (TXDELAY one, CSMA the
/// others).
/// </summary>
/// <remarks>
/// <para>
/// All children also see every <see cref="Observe"/> call, so each can
/// update its internal state from the same outcome stream.
/// </para>
/// <para>
/// Construct with the children you want to drive. The typical default
/// setup for a single-mode radio is
/// <see cref="TxDelayHillClimbEstimator"/> + <see cref="CsmaContentionEstimator"/>.
/// </para>
/// </remarks>
public sealed class CompositeAdaptiveEstimator : IAdaptiveParameterEstimator
{
    private readonly IAdaptiveParameterEstimator[] children;

    public CompositeAdaptiveEstimator(params IAdaptiveParameterEstimator[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Length == 0)
        {
            throw new ArgumentException("composite needs at least one child estimator", nameof(children));
        }
        this.children = children;
    }

    /// <inheritdoc/>
    public KissParameters Recommend(string peer)
    {
        var combined = new KissParameters(null, null, null, null);
        foreach (var child in children)
        {
            combined = combined.Override(child.Recommend(peer));
        }
        return combined;
    }

    /// <inheritdoc/>
    public void Observe(FrameOutcomeSample sample)
    {
        foreach (var child in children)
        {
            child.Observe(sample);
        }
    }
}
