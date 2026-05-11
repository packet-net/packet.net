namespace Packet.Core;

/// <summary>
/// CRC-16-CCITT as specified for the AX.25 frame check sequence (FCS).
/// </summary>
/// <remarks>
/// AX.25 v2.2 §3.7 names ISO 3309 CRC-CCITT with polynomial 0x1021. The
/// per-byte processing is LSB-first (AX.25 v2.2 §3.8 transmits data
/// "least-significant bit first") while the resulting FCS is transmitted
/// MSB-first. In CRC catalogue terms this is CRC-16/X-25:
/// <list type="bullet">
///   <item>Polynomial: 0x1021 (x^16 + x^12 + x^5 + 1)</item>
///   <item>Init:       0xFFFF</item>
///   <item>RefIn:      true</item>
///   <item>RefOut:     true</item>
///   <item>XorOut:     0xFFFF</item>
/// </list>
/// Standard test vector: "123456789" → 0x906E.
/// </remarks>
public static class Crc16Ccitt
{
    // 0x8408 is the bit-reverse of 0x1021 — used because RefIn/RefOut=true
    // is implemented by reflecting the polynomial and shifting right.
    private const ushort ReflectedPolynomial = 0x8408;

    /// <summary>
    /// Computes the AX.25 FCS over <paramref name="data"/>.
    /// </summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 1) != 0)
                {
                    crc = (ushort)((crc >> 1) ^ ReflectedPolynomial);
                }
                else
                {
                    crc >>= 1;
                }
            }
        }
        return (ushort)(crc ^ 0xFFFF);
    }
}
