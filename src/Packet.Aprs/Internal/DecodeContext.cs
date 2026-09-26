namespace Packet.Aprs.Internal;

/// <summary>Carries the options and collects diagnostics while one packet is decoded.</summary>
internal sealed class DecodeContext(AprsParseOptions options)
{
    public AprsParseOptions Options { get; } = options;

    /// <summary>Third-party nesting depth (0 for the outer packet).</summary>
    public int Depth { get; init; }

    public List<AprsDiagnostic> Diagnostics { get; } = [];

    public void Info(AprsDiagnosticCode code, string message, int? offset = null) =>
        Diagnostics.Add(new AprsDiagnostic(code, AprsDiagnosticSeverity.Info, message, offset));

    public void Warn(AprsDiagnosticCode code, string message, int? offset = null) =>
        Diagnostics.Add(new AprsDiagnostic(code, AprsDiagnosticSeverity.Warning, message, offset));

    public void Error(AprsDiagnosticCode code, string message, int? offset = null) =>
        Diagnostics.Add(new AprsDiagnostic(code, AprsDiagnosticSeverity.Error, message, offset));

    /// <summary>
    /// A real-world deviation governed by a named <see cref="AprsParseOptions"/> flag. When the
    /// flag allows it, records a warning and returns true so decoding carries on; otherwise records
    /// an error and returns false so the caller abandons the data.
    /// </summary>
    public bool Tolerate(bool allowed, AprsDiagnosticCode code, string message, int? offset = null)
    {
        if (allowed)
        {
            Warn(code, message, offset);
            return true;
        }

        Error(code, message, offset);
        return false;
    }

    public bool HasErrors => Diagnostics.Exists(d => d.Severity == AprsDiagnosticSeverity.Error);
}
