using Packet.Node.Core.Modems;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Discovery;

/// <summary>
/// The sysfs USB id lookup behind the modem scan's probe policy.
/// </summary>
/// <remarks>
/// It decides which serial devices are opened and written to, so both answers matter: reading the
/// ids right means a NinoTNC is found, and reading "unknown" for a non-USB or unreadable tty means
/// the caller falls back to the device class rather than probing everything in <c>/dev</c>. The
/// tests build a replica of the real layout out of a temp directory and a symlink, which is the
/// only part of it that is not obvious - <c>/sys/class/tty/ttyACM0/device</c> is a symlink into
/// the USB device tree, and the ids live on a PARENT of what it points at.
/// </remarks>
[Trait("Category", "Node")]
public sealed class UsbSerialIdsTests : IDisposable
{
    private readonly string root;
    private readonly string sysClassTty;

    public UsbSerialIdsTests()
    {
        root = TestPaths.NewPath("pdn-usbids");
        sysClassTty = Path.Combine(root, "class", "tty");
        Directory.CreateDirectory(sysClassTty);
    }

    /// <summary>Build /sys/devices/.../usb1/1-1 with the ids on it and 1-1:1.0 beneath, then point
    /// class/tty/{name}/device at the interface, exactly as udev does.</summary>
    private void Plant(string name, string vid, string pid)
    {
        var usbDevice = Path.Combine(root, "devices", "usb1", "1-1");
        var usbInterface = Path.Combine(usbDevice, "1-1:1.0");
        Directory.CreateDirectory(usbInterface);
        File.WriteAllText(Path.Combine(usbDevice, "idVendor"), vid + "\n");
        File.WriteAllText(Path.Combine(usbDevice, "idProduct"), pid + "\n");

        var ttyDir = Path.Combine(sysClassTty, name);
        Directory.CreateDirectory(ttyDir);
        Directory.CreateSymbolicLink(Path.Combine(ttyDir, "device"), usbInterface);
    }

    [Fact]
    public void The_ids_are_read_from_the_usb_parent_of_the_ttys_device_link()
    {
        Plant("ttyACM0", "04d8", "00dd");

        UsbSerialIds.Read("/dev/ttyACM0", sysClassTty).Should().Be(((ushort)0x04D8, (ushort)0x00DD));
    }

    [Fact]
    public void A_tty_with_no_device_link_reads_as_unknown()
    {
        // A plain serial port (a real UART, a pty): not USB, so there is nothing to read, and the
        // caller must not treat "unknown" as "not a NinoTNC".
        Directory.CreateDirectory(Path.Combine(sysClassTty, "ttyS0"));

        UsbSerialIds.Read("/dev/ttyS0", sysClassTty).Should().BeNull();
    }

    [Fact]
    public void An_absent_tty_reads_as_unknown_rather_than_throwing()
    {
        UsbSerialIds.Read("/dev/ttyACM9", sysClassTty).Should().BeNull();
    }

    [Fact]
    public void A_device_tree_with_no_ids_reads_as_unknown()
    {
        var usbInterface = Path.Combine(root, "devices", "platform", "serial0");
        Directory.CreateDirectory(usbInterface);
        var ttyDir = Path.Combine(sysClassTty, "ttyAMA0");
        Directory.CreateDirectory(ttyDir);
        Directory.CreateSymbolicLink(Path.Combine(ttyDir, "device"), usbInterface);

        UsbSerialIds.Read("/dev/ttyAMA0", sysClassTty).Should().BeNull();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
    }
}
