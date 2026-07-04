using Packet.Node.Core.Radios;

namespace Packet.Node.Tests.Radios;

/// <summary>
/// <see cref="SerialByIdResolver"/> against a temp directory of symlinks — the udev by-id resolution
/// (Linux-only). It must resolve a unique symlink but refuse an ambiguous one (two links to the same
/// device, as the shared-USB-serial CP2102 CCDI dongles produce).
/// </summary>
[Trait("Category", "Node")]
public sealed class SerialByIdResolverTests : IDisposable
{
    private readonly string dir;
    private readonly string byIdDir;
    private readonly string devDir;

    public SerialByIdResolverTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-byid-" + Guid.NewGuid().ToString("N"));
        byIdDir = Path.Combine(dir, "by-id");
        devDir = Path.Combine(dir, "dev");
        Directory.CreateDirectory(byIdDir);
        Directory.CreateDirectory(devDir);
    }

    private string Device(string name)
    {
        var path = Path.Combine(devDir, name);
        File.WriteAllText(path, string.Empty);   // stand-in for a /dev/ttyUSBn node
        return path;
    }

    private void Link(string linkName, string target)
        => File.CreateSymbolicLink(Path.Combine(byIdDir, linkName), target);

    [SkippableFact]
    public void Resolves_the_unique_by_id_symlink_for_a_device()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "by-id resolution is Linux-only");
        var dev = Device("ttyUSB0");
        Link("usb-Tait_Ltd-if00", dev);

        new SerialByIdResolver(byIdDir).Resolve(dev)
            .Should().Be(Path.Combine(byIdDir, "usb-Tait_Ltd-if00"));
    }

    [SkippableFact]
    public void Returns_null_when_two_symlinks_point_at_the_same_device()
    {
        // Two CP2102 dongles that share a USB serial produce colliding by-id links → ambiguous.
        Skip.IfNot(OperatingSystem.IsLinux(), "by-id resolution is Linux-only");
        var dev = Device("ttyUSB0");
        Link("usb-Silicon_Labs_CP2102_0001-if00", dev);
        Link("usb-Silicon_Labs_CP2102_0001-if00-port0", dev);

        new SerialByIdResolver(byIdDir).Resolve(dev).Should().BeNull();
    }

    [SkippableFact]
    public void Returns_null_when_no_symlink_matches_the_device()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "by-id resolution is Linux-only");
        var dev = Device("ttyUSB0");
        Link("usb-Other-if00", Device("ttyUSB1"));

        new SerialByIdResolver(byIdDir).Resolve(dev).Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
