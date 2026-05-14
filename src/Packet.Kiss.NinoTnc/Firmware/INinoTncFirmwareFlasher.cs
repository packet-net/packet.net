namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// The seam where the actual firmware-flash operation will live.
/// <strong>Intentionally no concrete implementation in this package</strong> —
/// the PIC bootloader protocol is risky enough (a failed flash can
/// brick a modem until ICSP recovery) that we want it on its own
/// review cycle, in its own PR, with its own test discipline.
/// </summary>
/// <remarks>
/// <para>
/// Likely future implementations:
/// </para>
/// <list type="bullet">
///   <item><c>BootloaderFirmwareFlasher</c> — native C# implementation
///         of the PIC bootloader protocol that flashtnc.py speaks.</item>
///   <item><c>FlashTncProcessFlasher</c> — shells out to
///         <c>flashtnc.py</c> for hosts that have it installed,
///         honest about the dependency.</item>
/// </list>
/// <para>
/// Until then, the only "implementation" in-tree is
/// <see cref="UnsupportedFirmwareFlasher"/> — a stub that throws so
/// callers can wire to the interface today and only have to revisit
/// the wiring when a real flasher ships.
/// </para>
/// </remarks>
public interface INinoTncFirmwareFlasher
{
    /// <summary>
    /// Flash <paramref name="hexImage"/> onto the modem at
    /// <paramref name="portName"/>. The Intel-HEX image bytes are
    /// expected to be a complete, validated firmware payload for the
    /// modem's chip variant. The implementation is responsible for
    /// driving the bootloader handshake and verifying the result.
    /// </summary>
    /// <param name="portName">Serial port (e.g. <c>"COM6"</c> on
    ///   Windows, <c>"/dev/ttyACM0"</c> on Linux).</param>
    /// <param name="hexImage">Raw Intel-HEX file bytes.</param>
    /// <param name="progress">Optional progress reporter (0..1).</param>
    /// <param name="cancellationToken">Cancellation. Implementations
    ///   should bail safely if practical — the bootloader handshake
    ///   may have a point of no return.</param>
    Task FlashAsync(
        string portName,
        ReadOnlyMemory<byte> hexImage,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The stub flasher. Every method throws
/// <see cref="NotSupportedException"/> with a pointer to the issue /
/// future PR. Lets callers wire the interface today.
/// </summary>
public sealed class UnsupportedFirmwareFlasher : INinoTncFirmwareFlasher
{
    /// <inheritdoc/>
    public Task FlashAsync(
        string portName,
        ReadOnlyMemory<byte> hexImage,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "Firmware flashing is not yet supported. The seam exists so the UI / MCP / " +
            "session layers can wire to INinoTncFirmwareFlasher today; a real " +
            "implementation will arrive in a separate PR (likely a native C# port of " +
            "the flashtnc bootloader protocol, with all safety/recovery considered).");
}
