namespace Packet.Aprs.Internal;

/// <summary>
/// APRS base-91 (APRS12c §5 Base-91 Notation): each character is a digit 0 to 90 written as the
/// ASCII code minus 33, most significant digit first. Not the same as the internet "basE91".
/// </summary>
internal static class Base91
{
    public const int Radix = 91;

    public static bool IsDigit(byte b) => b is >= 33 and <= 33 + 90;

    public static bool TryDecode(ReadOnlySpan<byte> digits, out long value)
    {
        value = 0;
        foreach (byte b in digits)
        {
            if (!IsDigit(b))
            {
                return false;
            }

            value = (value * Radix) + (b - 33);
        }

        return true;
    }

    public static void Encode(long value, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        for (int i = destination.Length - 1; i >= 0; i--)
        {
            destination[i] = (byte)((value % Radix) + 33);
            value /= Radix;
        }

        if (value != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "value does not fit in the requested number of base-91 digits");
        }
    }

    public static long MaxValue(int digits)
    {
        long max = 1;
        for (int i = 0; i < digits; i++)
        {
            max *= Radix;
        }

        return max - 1;
    }
}
