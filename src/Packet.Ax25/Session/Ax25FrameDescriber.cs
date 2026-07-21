namespace Packet.Ax25.Session;

/// <summary>
/// Renders an <see cref="Ax25Frame"/> into a compact human-readable string
/// for debug logging. Produces descriptions like:
/// <code>
///   SABM P=1
///   UA F=1
///   I N(S)=2 N(R)=5 P=0 pid=0xF0 len=128
///   RR N(R)=3 F=1
///   UI pid=0xCF len=42
/// </code>
/// </summary>
internal static class Ax25FrameDescriber
{
    public static string Describe(Ax25Frame frame)
    {
        byte control = frame.Control;

        // I frame: bit 0 == 0
        if ((control & 0x01) == 0)
        {
            return $"I N(S)={frame.Ns} N(R)={frame.Nr} P={B(frame.PollFinal)} pid=0x{frame.Pid:X2} len={frame.Info.Length}";
        }

        // S frame: bits 1-0 == 01
        if ((control & 0x03) == 0x01)
        {
            string sType = (control & 0x0F) switch
            {
                0x01 or 0x03 => "RR",
                0x05 or 0x07 => "RNR",
                0x09 or 0x0B => "REJ",
                0x0D or 0x0F => "SREJ",
                _ => $"S(0x{control:X2})",
            };
            string pf = frame.IsCommand ? "P" : "F";
            return $"{sType} N(R)={frame.Nr} {pf}={B(frame.PollFinal)}";
        }

        // U frame: bits 1-0 == 11
        string uType = (control & 0xEF) switch
        {
            0x2F => "SABM",
            0x6F => "SABME",
            0x43 => "DISC",
            0x63 => "UA",
            0x0F => "DM",
            0x87 => "FRMR",
            0xAF => "XID",
            0xE3 => "TEST",
            0x03 => "UI",
            _ => $"U(0x{control:X2})",
        };

        if ((control & 0xEF) == 0x03)
        {
            // UI frame
            return $"UI pid=0x{frame.Pid:X2} len={frame.Info.Length}";
        }

        string pfLabel = frame.IsCommand ? "P" : "F";
        string suffix = frame.Info.Length > 0 ? $" len={frame.Info.Length}" : "";
        return $"{uType} {pfLabel}={B(frame.PollFinal)}{suffix}";
    }

    private static string B(bool v) => v ? "1" : "0";
}
