using System.IO.Ports;
using System.Runtime.InteropServices;
using Xunit;

namespace Packet.Interop.Tests.Hardware;

/// <summary>
/// Phase 0 hardware-loop hello-world. The full target is "TX a UI frame on
/// TNC-A, observe it on TNC-B" - but that needs Packet.Kiss (Phase 1) and the
/// AX.25 framer (Phase 1/2). For Phase 0 this is just an enumeration probe:
/// confirm we can SEE two USB-attached NinoTNCs on the dev host.
/// </summary>
/// <remarks>
/// The tests are tagged <c>Category=HardwareLoop</c> so the default CI
/// matrix skips them. The self-hosted hardware-loop runner does
/// <c>--filter "Category=HardwareLoop"</c> instead.
/// </remarks>
[Trait("Category", "HardwareLoop")]
public class NinoTncEnumeration
{
    [SkippableFact]
    public void FindsAtLeastTwoSerialDevices()
    {
        var devices = EnumerateLikelyTncPorts().ToList();

        Skip.If(
            devices.Count < 2,
            $"Hardware-loop probe: expected ≥2 NinoTNC-class serial devices, " +
            $"found {devices.Count}. Connect both TNCs over USB and re-run.");

        (devices.Count >= 2).Should().BeTrue();
    }

    [SkippableFact]
    public void CanOpenBothDevicesAt57600()
    {
        var devices = EnumerateLikelyTncPorts().Take(2).ToList();

        Skip.If(
            devices.Count < 2,
            $"Hardware-loop probe: expected ≥2 NinoTNC-class serial devices, " +
            $"found {devices.Count}.");

        foreach (var portName in devices)
        {
            using var port = new SerialPort(portName, 57600, Parity.None, 8, StopBits.One);
            port.Open();
            port.IsOpen.Should().BeTrue($"port {portName} should be open");
        }
    }

    /// <summary>
    /// Enumerate serial devices likely to be NinoTNCs. Platform-specific:
    /// on Linux we look at <c>/dev/serial/by-id/</c> (preferred) and then
    /// <c>/dev/ttyACM*</c>; on Windows / macOS we fall back to
    /// <see cref="SerialPort.GetPortNames"/>.
    /// </summary>
    /// <remarks>
    /// Phase 0 intentionally does NOT match on VID/PID - at this point we're
    /// happy with anything that looks like a USB-CDC serial device. Real
    /// NinoTNC identification (firmware TX-test parsing) lands in Phase 3.
    /// </remarks>
    private static IEnumerable<string> EnumerateLikelyTncPorts()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            const string byId = "/dev/serial/by-id";
            if (Directory.Exists(byId))
            {
                foreach (var link in Directory.EnumerateFileSystemEntries(byId).OrderBy(p => p, StringComparer.Ordinal))
                {
                    yield return link;
                }
                yield break;
            }

            // Fall back to raw /dev/ttyACM* if no by-id symlinks (some minimal
            // udev configurations skip them).
            foreach (var dev in Directory.EnumerateFiles("/dev", "ttyACM*").OrderBy(p => p, StringComparer.Ordinal))
            {
                yield return dev;
            }
            yield break;
        }

        foreach (var name in SerialPort.GetPortNames())
        {
            yield return name;
        }
    }
}
