namespace Packet.NetRom.Routing;

/// <summary>
/// NET/ROM route-quality arithmetic — the multiplicative per-hop decay from the
/// canonical NET/ROM appendix. Quality is an integer 0 (worst) … 255 (best).
/// </summary>
/// <remarks>
/// <para>
/// When node A hears node B advertise destination D at quality <c>bq</c>, A's
/// quality for the route to D <em>via B</em> is the advertised quality scaled by
/// A's own path quality to B:
/// </para>
/// <code>
///   routequality = (broadcastquality × pathquality + 128) / 256   (integer, rounded)
/// </code>
/// <para>
/// The <c>+ 128</c> is round-to-nearest on the divide-by-256. Quality therefore
/// decays multiplicatively with each hop: a 200-quality direct link is ≈ 156 at
/// two hops (200 × 200 / 256) and ≈ 78 at three (last link 128). The practical
/// per-hop / floor conventions (direct link ~192–203, MINQUAL ~128–180) are
/// <em>de-facto, not normative</em> — they vary per implementation, so they live
/// as configurable knobs on <see cref="NetRomRoutingOptions"/>, never hard-coded
/// here.
/// </para>
/// </remarks>
public static class NetRomQuality
{
    /// <summary>The maximum (best) quality value.</summary>
    public const int Max = 255;

    /// <summary>The minimum (worst) quality value — a quality-0 route is never usable / re-advertised.</summary>
    public const int Min = 0;

    /// <summary>
    /// Combine an advertised broadcast quality with the path quality to the
    /// advertising neighbour, per the canonical multiplicative formula
    /// <c>(broadcastquality × pathquality + 128) / 256</c>, rounded and clamped to
    /// 0..255.
    /// </summary>
    /// <param name="broadcastQuality">The quality the neighbour advertised for the destination (0..255).</param>
    /// <param name="pathQuality">Our path quality to that neighbour (0..255).</param>
    /// <returns>Our derived route quality for the destination via that neighbour (0..255).</returns>
    public static byte Combine(byte broadcastQuality, byte pathQuality)
    {
        // (a × b + 128) / 256, integer. Max input 255 × 255 + 128 = 65153, well
        // within int; result is ≤ 254 so it always fits a byte, but clamp for
        // total safety.
        int combined = ((broadcastQuality * pathQuality) + 128) / 256;
        return (byte)Math.Clamp(combined, Min, Max);
    }
}
