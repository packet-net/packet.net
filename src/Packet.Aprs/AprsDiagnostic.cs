namespace Packet.Aprs;

/// <summary>How serious a decoding diagnostic is.</summary>
public enum AprsDiagnosticSeverity
{
    /// <summary>Legal but noteworthy, for example an obsolete or not-recommended format.</summary>
    Info,

    /// <summary>A deviation from the spec that was tolerated because an <see cref="AprsParseOptions"/>
    /// flag allowed it. The decoded data is still usable.</summary>
    Warning,

    /// <summary>A deviation that stopped the data from being decoded. <see cref="AprsPacket.Data"/>
    /// is then <see cref="AprsUnrecognizedData"/>.</summary>
    Error,
}

/// <summary>
/// Something the decoder noticed about a packet: a spec violation, a tolerated real-world defect,
/// or the use of an obsolete format.
/// </summary>
/// <param name="Code">Stable identifier; safe to switch on.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">Human-readable explanation in plain ASCII.</param>
/// <param name="Offset">Byte offset into the information field where the problem was found, or
/// null when it concerns the header or the packet as a whole.</param>
public readonly record struct AprsDiagnostic(
    AprsDiagnosticCode Code,
    AprsDiagnosticSeverity Severity,
    string Message,
    int? Offset = null)
{
    /// <summary>Formats as <c>Severity Code @offset: message</c>.</summary>
    public override string ToString() =>
        Offset is { } o ? $"{Severity} {Code} @{o}: {Message}" : $"{Severity} {Code}: {Message}";
}
