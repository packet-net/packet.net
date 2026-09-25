using Packet.Aprs.Internal;

namespace Packet.Aprs;

/// <summary>
/// Base-91 comment telemetry: <c>|ss11223344556677|</c> in the comment of a position report
/// (APRS12c §13 APRS Base91 Comment Telemetry). Each value is two base-91 characters, 0-8280.
/// </summary>
/// <remarks>
/// It must come after the free-form comment text and before any <c>!DAO!</c> or Mic-E device
/// suffix. It holds a sequence number and 1 to 5 analog channels, then optionally the 8
/// digital channels packed into one value (bit 0 is B1). Channel meanings, units and scaling come
/// from the station's telemetry metadata messages.
/// </remarks>
public sealed record AprsCommentTelemetry
{
    /// <summary>The largest value two base-91 characters can hold.</summary>
    public const int MaxValue = 8280;

    /// <summary>Sequence counter, 0-8280 (senders usually wrap at 8191 or 255).</summary>
    public required int Sequence { get; init; }

    /// <summary>1 to 5 analog channel values, each 0-8280.</summary>
    public required IReadOnlyList<int> Analog { get; init => field = EquatableList<int>.Of(value); }

    /// <summary>The 8 digital channels, bit 0 = B1, or null if not sent. Can only be sent with all
    /// 5 analog channels.</summary>
    public byte? Digital { get; init; }

    internal void Validate(string paramName)
    {
        if (Sequence is < 0 or > MaxValue || Analog.Count is < 1 or > 5 || Analog.Any(v => v is < 0 or > MaxValue))
        {
            throw new ArgumentOutOfRangeException(paramName, "comment telemetry: sequence and 1-5 analog values, each 0-8280");
        }

        if (Digital is not null && Analog.Count != 5)
        {
            throw new ArgumentException("comment telemetry digital bits can only follow all 5 analog channels", paramName);
        }
    }
}
