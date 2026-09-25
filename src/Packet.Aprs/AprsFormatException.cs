namespace Packet.Aprs;

/// <summary>
/// Thrown when a packet's envelope (the TNC2 header or AX.25 address field) cannot be parsed, so
/// there is no source or destination to build an <see cref="AprsPacket"/> from. Problems in the
/// information field never throw; they are reported as <see cref="AprsPacket.Diagnostics"/>.
/// </summary>
public sealed class AprsFormatException : FormatException
{
    /// <summary>Creates the exception.</summary>
    public AprsFormatException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public AprsFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public AprsFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal AprsFormatException(IReadOnlyList<AprsDiagnostic> diagnostics)
        : base(string.Join("; ", diagnostics.Where(d => d.Severity == AprsDiagnosticSeverity.Error).Select(d => d.Message)))
    {
        Diagnostics = diagnostics;
    }

    /// <summary>Everything the decoder noticed, including the errors that stopped it.</summary>
    public IReadOnlyList<AprsDiagnostic> Diagnostics { get; } = [];
}
