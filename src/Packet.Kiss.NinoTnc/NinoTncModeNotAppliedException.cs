namespace Packet.Kiss.NinoTnc;

/// <summary>
/// Thrown by <see cref="NinoTncSerialPort.SetModeAsync"/> when a SETHW mode
/// change never took: every attempt was sent, and the GETALL readback still
/// reported the TNC running a different mode (or a mode the catalog can't
/// resolve, or nothing at all).
/// </summary>
/// <remarks>
/// <para>
/// Throwing — rather than returning <c>false</c> — is deliberate (#633). The
/// entire complaint against fire-and-forget SETHW is that its failure is
/// <em>silent</em>: a bool can be dropped on the floor with no compiler
/// complaint, which reproduces exactly the bug this guards against, one call
/// site at a time. An exception cannot be ignored by accident, and it keeps
/// <see cref="NinoTncSerialPort.SetModeAsync"/>'s signature returning
/// <see cref="Task"/> so existing callers stay source-compatible.
/// </para>
/// <para>
/// <see cref="RunningMode"/> carries what the TNC says it is <em>actually</em>
/// running, which is the fact that turns a day of "why does this mode score
/// zero" into a one-line diagnosis.
/// </para>
/// </remarks>
public sealed class NinoTncModeNotAppliedException : Exception
{
    /// <summary>Create the exception with a caller-supplied message.</summary>
    public NinoTncModeNotAppliedException(string message)
        : base(message)
    {
    }

    /// <summary>Create the exception with a caller-supplied message and inner cause.</summary>
    public NinoTncModeNotAppliedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    internal NinoTncModeNotAppliedException(
        string message, byte requestedMode, NinoTncMode? runningMode, byte? firmwareModeByte, int attempts, Exception? innerException)
        : base(message, innerException)
    {
        RequestedMode = requestedMode;
        RunningMode = runningMode;
        FirmwareModeByte = firmwareModeByte;
        Attempts = attempts;
    }

    /// <summary>The mode that was asked for.</summary>
    public byte RequestedMode { get; }

    /// <summary>
    /// The mode the TNC reported it was actually running on the last readback,
    /// or <c>null</c> if the readback never landed or the firmware byte was
    /// unknown to <see cref="NinoTncCatalog"/>.
    /// </summary>
    public NinoTncMode? RunningMode { get; }

    /// <summary>
    /// The raw firmware mode byte from the last readback (<c>BrdSwchMod</c>'s
    /// low byte), or <c>null</c> if no readback landed. Non-null with a
    /// <c>null</c> <see cref="RunningMode"/> means the catalog needs a row for
    /// this firmware.
    /// </summary>
    public byte? FirmwareModeByte { get; }

    /// <summary>How many SETHW attempts were made.</summary>
    public int Attempts { get; }
}
