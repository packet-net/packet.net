using System.Globalization;

namespace Packet.Node.Core.Modems;

/// <summary>
/// Reads the USB vendor/product ids behind a Linux tty, from sysfs. Used to decide whether a
/// serial device is worth a NinoTNC identify probe (<see cref="ModemScanner.LooksProbeWorthy"/>)
/// without opening it first.
/// </summary>
/// <remarks>
/// <c>/sys/class/tty/ttyACM0/device</c> is the CDC interface; the ids live on its parent, the USB
/// device. Walking up a couple of levels covers both the interface-attached and device-attached
/// layouts without hardcoding either. Everything is best-effort: no sysfs, no permission, a
/// non-USB tty or a non-Linux host all read as "unknown", which the caller treats as
/// "fall back to the device class".
/// </remarks>
public static class UsbSerialIds
{
    /// <summary>The sysfs tty class directory. Overridable so the walk is testable against a
    /// temp-directory replica of the real layout.</summary>
    public const string DefaultSysClassTty = "/sys/class/tty";

    /// <summary>
    /// The (vendor, product) ids for <paramref name="devicePath"/> (e.g. <c>/dev/ttyACM0</c>), or
    /// null when they cannot be read.
    /// </summary>
    /// <param name="devicePath">A device path; only its file name is used.</param>
    /// <param name="sysClassTty">Sysfs tty class root; null uses <see cref="DefaultSysClassTty"/>.</param>
    public static (ushort Vid, ushort Pid)? Read(string devicePath, string? sysClassTty = null)
    {
        ArgumentNullException.ThrowIfNull(devicePath);
        if (!OperatingSystem.IsLinux() && sysClassTty is null)
        {
            return null;
        }

        var root = sysClassTty ?? DefaultSysClassTty;
        var name = Path.GetFileName(devicePath);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        try
        {
            // The tty's `device` entry is a symlink into the USB device tree, so resolve it and
            // walk the REAL parents - a ".." walk cannot climb out of a symlink.
            var dir = ResolveDirectory(Path.Combine(root, name, "device"));

            // Up to four levels: the CDC interface, its USB device, and a little headroom for
            // layouts that nest deeper. It stops the moment both ids are found.
            for (int depth = 0; depth < 4 && dir is not null; depth++)
            {
                if (ReadId(Path.Combine(dir, "idVendor")) is { } vid
                    && ReadId(Path.Combine(dir, "idProduct")) is { } pid)
                {
                    return (vid, pid);
                }
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable sysfs is "unknown", never an error.
        }

        return null;
    }

    private static string? ResolveDirectory(string path)
    {
        try
        {
            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return resolved?.FullName ?? (Directory.Exists(path) ? Path.GetFullPath(path) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ushort? ReadId(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var text = File.ReadAllText(path).Trim();
            return ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
