using Packet.Node.Core.Configuration;
using Packet.SoundModem.FlexRadio;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Shared helpers for opening a FlexRadio device string (<c>flex:&lt;radio&gt;[:slice][@station]</c>)
/// as a soundmodem audio backend. Kept in one place so the soundmodem transport and the
/// ARDOP/POCSAG audio services all treat Flex devices identically.
/// </summary>
internal static class SoundModemFlexDevice
{
    /// <summary>The DAX buffer depth for a packet (KISS/paging) client, matching the daemon.</summary>
    public const int PacketBuffer = 3;

    /// <summary>The deeper DAX buffer an ARDOP client uses, matching the daemon.</summary>
    public const int ArdopBuffer = 6;

    /// <summary>True if <paramref name="device"/> is a <c>flex:</c> device string.</summary>
    public static bool IsFlex(string device) => FlexDevice.IsFlex(device);

    /// <summary>
    /// Opens the FlexRadio slice as a runtime exposing <c>Input</c>/<c>Output</c>/<c>Ptt</c> at the
    /// DSP rate. The returned <see cref="FlexRuntime"/> owns those seams — dispose it (not them).
    /// </summary>
    public static Task<FlexRuntime> OpenAsync(
        string device,
        int dspRate,
        SoundModemFlexConfig? tuning,
        int packetBuffer,
        CancellationToken cancellationToken) =>
        FlexDevice.OpenAsync(device, dspRate, packetBuffer, ToFlexTuning(tuning), cancellationToken);

    /// <summary>Maps the node's <see cref="SoundModemFlexConfig"/> to the library's tuning record
    /// (defaults when none is configured — headless 14.100 MHz / ANT1 / DIGU / DAX 1).</summary>
    public static FlexTuning ToFlexTuning(SoundModemFlexConfig? flex) => flex is null
        ? new FlexTuning()
        : new FlexTuning
        {
            Frequency = flex.Frequency,
            Antenna = flex.Antenna,
            Mode = flex.Mode,
            DaxChannel = flex.DaxChannel,
        };
}
