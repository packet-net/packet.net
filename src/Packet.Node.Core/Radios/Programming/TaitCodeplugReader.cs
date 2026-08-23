using M0LTE.Tait.Codeplug;

namespace Packet.Node.Core.Radios.Programming;

/// <summary>
/// Reads a codeplug image back out as the settings the panel shows: channel 1's frequency,
/// bandwidth, power and tones, how many channels there are, and which PDN profile the radio's data
/// block already matches. Pure - it never writes to the radio, and the image it is handed is the
/// one read off the radio before any plan is applied.
/// </summary>
internal static class TaitCodeplugReader
{
    /// <summary>
    /// Describe an image. A codeplug whose database version the field map does not cover still
    /// reports its version - that is the fact worth having, because it is exactly what the write
    /// path refuses on - with every interpreted field left null.
    /// </summary>
    /// <param name="image">The codeplug as read off the radio.</param>
    internal static TaitRadioSettings Describe(CodeplugImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        string? database = DatabaseVersion(image);

        if (!CodeplugFields.IsSupported(image))
        {
            return new TaitRadioSettings(null, null, null, null, null, null, database, null, null);
        }

        // Total by construction: this is a nicety on the way to a read or a write, and a codeplug
        // that comes back in a shape the field map trips over must not be the thing that fails the
        // run. Whatever could be read is reported; the rest is null.
        try
        {
            CodeplugFields fields = CodeplugFields.Open(image);
            if (fields.ChannelCount < 1)
            {
                return new TaitRadioSettings(null, null, null, null, null, 0, database, null, null);
            }

            long rx = fields.GetRxFrequencyHz(0);
            long tx = fields.GetSeparateTxFrequency(0) ? fields.GetTxFrequencyHz(0) : rx;
            return new TaitRadioSettings(
                rx,
                tx,
                TaitProgramPlan.BandwidthToWire(fields.GetBandwidth(0)),
                TaitProgramPlan.PowerToWire(fields.GetPowerLevel(0)),
                DetectProfile(image),
                fields.ChannelCount,
                database,
                fields.GetRxSubaudible(0),
                fields.GetTxSubaudible(0));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
            or ArgumentException or FormatException or IndexOutOfRangeException)
        {
            return new TaitRadioSettings(null, null, null, null, null, null, database, null, null);
        }
    }

    /// <summary>The codeplug's database version - from the record when it is there, else the
    /// file header.</summary>
    internal static string? DatabaseVersion(CodeplugImage image) =>
        image.DatabaseVersionFromRecord ?? image.DatabaseVersion;

    /// <summary>
    /// Which PDN profile a codeplug already matches, decided by <b>applying</b> each profile to a
    /// copy and seeing whether anything changed. No second copy of the profiles' field lists lives
    /// here, so a profile that gains a field in a future <c>M0LTE.Tait.Codeplug</c> cannot leave the
    /// detection behind. Extra is checked first because it is a superset of basic.
    /// </summary>
    /// <param name="image">The codeplug as read off the radio.</param>
    private static string DetectProfile(CodeplugImage image)
    {
        if (Unchanged(image, f => f.ApplyPdnExtra()))
        {
            return "pdn-extra";
        }

        return Unchanged(image, f => f.ApplyPdnBasic()) ? "pdn-basic" : "none";
    }

    /// <summary>
    /// Whether applying <paramref name="apply"/> changes nothing. The comparison is made against a
    /// snapshot of the <b>copy's own</b> record bytes rather than against the original image, so a
    /// lossless-but-not-identical .m8p round trip cannot read as a difference. False (rather than a
    /// throw) if the copy cannot be made or applied - profile detection is a nicety and must never
    /// fail a run.
    /// </summary>
    private static bool Unchanged(CodeplugImage image, Action<CodeplugFields> apply)
    {
        try
        {
            CodeplugImage copy = CodeplugImage.LoadM8p(image.ToM8p());
            List<byte[]> before = [.. copy.Records.Select(r => (byte[])r.Data.Clone())];
            apply(CodeplugFields.Open(copy));

            if (copy.Records.Count != before.Count)
            {
                return false;
            }

            for (int i = 0; i < before.Count; i++)
            {
                if (!copy.Records[i].Data.AsSpan().SequenceEqual(before[i]))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException
            or ArgumentException or FormatException)
        {
            return false;
        }
    }
}
