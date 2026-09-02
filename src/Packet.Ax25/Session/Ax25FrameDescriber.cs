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
        Ax25FrameType type = frame.FrameType;

        if (type == Ax25FrameType.I)
        {
            return $"I N(S)={frame.Ns} N(R)={frame.Nr} P={B(frame.PollFinal)} pid=0x{frame.Pid:X2} len={frame.Info.Length}";
        }

        if (type.IsSupervisory())
        {
            string pf = frame.IsCommand ? "P" : "F";
            return $"{type.Mnemonic()} N(R)={frame.Nr} {pf}={B(frame.PollFinal)}";
        }

        if (type == Ax25FrameType.Ui)
        {
            return $"UI pid=0x{frame.Pid:X2} len={frame.Info.Length}";
        }

        string uType = type == Ax25FrameType.Unknown ? $"U(0x{frame.Control:X2})" : type.Mnemonic();
        string pfLabel = frame.IsCommand ? "P" : "F";
        string suffix = frame.Info.Length > 0 ? $" len={frame.Info.Length}" : "";
        return $"{uType} {pfLabel}={B(frame.PollFinal)}{suffix}";
    }

    private static string B(bool v) => v ? "1" : "0";
}
