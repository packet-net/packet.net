using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.Node.Core.Api;

/// <summary>
/// One frame as the web monitor sees it — the serialisable projection of an
/// <see cref="Ax25FrameEventArgs"/> (the <see cref="Ax25Listener.FrameTraced"/>
/// payload). Field names match the web client's <c>MonitorEvent</c>
/// (<c>web/packetnet-ui/src/lib/types.ts</c>); System.Text.Json's web defaults
/// camel-case the PascalCase properties (<c>ClassKind</c> → <c>classKind</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>Raw</c> is <see cref="int"/>[] (not <c>byte[]</c>) on purpose: STJ serialises
/// a <c>byte[]</c> as a Base64 string, but the UI hex-dumps a JSON number array, so
/// the bytes are widened to ints to reach the wire as <c>[129, 3, …]</c>.
/// </para>
/// <para>
/// Produced by <see cref="MonitorEventFactory.From"/> — a pure decode over the
/// frame's bit-level shape (no session state), so it labels a frame the same way on
/// TX and RX and under either modulo.
/// </para>
/// </remarks>
public sealed record MonitorEvent(
    long Seq,
    DateTimeOffset Timestamp,
    string PortId,
    string Direction,        // "in" | "out"
    string Source,
    string Dest,
    string Type,             // "I" | "RR" | "RNR" | "REJ" | "SREJ" | "UI" | "SABM" | …
    string ClassKind,        // "I" | "S" | "U"
    string? Pid,             // "0xCF" style, or null for S/U frames with no PID
    string? PidName,         // "NET/ROM", "No layer 3", … or null
    int? Ns,                 // I-frames only
    int? Nr,                 // I + S frames
    int Pf,                  // poll/final, 0|1
    bool Command,            // command vs response (C-bit)
    int Length,              // total frame length on the wire (no FCS)
    string Summary,
    IReadOnlyList<int> Raw,
    IReadOnlyList<string> Path);

/// <summary>
/// Decodes a parsed <see cref="Ax25Frame"/> (already off the listener's
/// <see cref="Ax25Listener.FrameTraced"/> tap) into a <see cref="MonitorEvent"/>.
/// Pure and allocation-light; safe to call on a listener pump thread.
/// </summary>
public static class MonitorEventFactory
{
    /// <summary>Project a traced frame into the monitor's wire shape.</summary>
    public static MonitorEvent From(long seq, string portId, Ax25FrameEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(e);

        var frame = e.Frame;
        var (type, classKind) = Classify(frame);
        bool isI = classKind == "I";
        bool isS = classKind == "S";

        int? ns = isI ? frame.Ns : null;
        // I + S frames carry N(R); a U-frame's high control bits are not N(R).
        int? nr = (isI || isS) ? frame.Nr : null;

        string? pid = frame.Pid is { } p ? $"0x{p:X2}" : null;
        string? pidName = PidName(frame.Pid);

        var bytes = frame.ToBytes();
        var raw = new int[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            raw[i] = bytes[i];
        }

        int pf = frame.PollFinal ? 1 : 0;
        int infoLen = frame.Info.Length;
        string summary = BuildSummary(type, ns, nr, pf, pid, infoLen);

        IReadOnlyList<string> path = frame.Digipeaters.Count == 0
            ? Array.Empty<string>()
            : frame.Digipeaters.Select(d => d.Callsign.ToString()).ToArray();

        return new MonitorEvent(
            Seq: seq,
            Timestamp: e.Timestamp,
            PortId: portId,
            Direction: e.Direction == FrameDirection.Received ? "in" : "out",
            Source: frame.Source.Callsign.ToString(),
            Dest: frame.Destination.Callsign.ToString(),
            Type: type,
            ClassKind: classKind,
            Pid: pid,
            PidName: pidName,
            Ns: ns,
            Nr: nr,
            Pf: pf,
            Command: frame.IsCommand,
            Length: bytes.Length,
            Summary: summary,
            Raw: raw,
            Path: path);
    }

    /// <summary>
    /// Frame-type + class from the first control octet. Mirrors
    /// <see cref="Ax25FrameClassifier"/>'s bit logic, but always returns the wire
    /// frame TYPE (the monitor labels what's on the air; it doesn't care whether an
    /// info field is permitted), so it never collapses to an error event. The
    /// discriminator bits live in the first octet under both modulo-8 and extended
    /// modulo-128, so this is modulo-independent.
    /// </summary>
    internal static (string Type, string ClassKind) Classify(Ax25Frame frame)
    {
        byte ctrl = frame.Control;

        // I-frame: bit 0 = 0.
        if ((ctrl & 0x01) == 0)
        {
            return ("I", "I");
        }

        // S-frame: bits 1-0 = 01; SS bits at 3-2 pick the subtype.
        if ((ctrl & 0x03) == 0x01)
        {
            string s = (ctrl & 0x0C) switch
            {
                0x00 => "RR",
                0x04 => "RNR",
                0x08 => "REJ",
                0x0C => "SREJ",
                _ => "S",
            };
            return (s, "S");
        }

        // U-frame: bits 1-0 = 11. Mask out P/F (bit 4) to get the base control octet.
        byte uBase = (byte)(ctrl & 0xEF);
        string u = uBase switch
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
            _ => "U",
        };
        return (u, "U");
    }

    // PID → human name, matching the web client's PIDS map.
    private static string? PidName(byte? pid) => pid switch
    {
        Ax25Frame.PidNoLayer3 => "No layer 3",   // 0xF0
        Ax25Frame.PidNetRom => "NET/ROM",        // 0xCF
        0xCC => "ARPA IP",
        Ax25Frame.PidSegmented => "Segmentation", // 0x08
        _ => null,
    };

    // A journald-ish one-liner, matching the mock's style so the monitor reads the
    // same against a live node as it does in demo mode.
    private static string BuildSummary(string type, int? ns, int? nr, int pf, string? pid, int infoLen)
    {
        return type switch
        {
            "I" => $"I N(S)={ns} N(R)={nr} P={pf} pid={pid} len={infoLen}",
            "RR" or "RNR" or "REJ" or "SREJ" => $"{type} N(R)={nr}{(pf == 1 ? " P/F" : "")}",
            "UI" => $"UI pid={pid} len={infoLen}",
            "SABM" or "SABME" => $"{type} request (connect)",
            "UA" => "UA (acknowledge)",
            "DISC" => "DISC (disconnect)",
            "DM" => "DM (disconnected mode)",
            "FRMR" => "FRMR (frame reject)",
            "XID" => "XID (parameter negotiation)",
            "TEST" => "TEST (loopback)",
            _ => type,
        };
    }
}
