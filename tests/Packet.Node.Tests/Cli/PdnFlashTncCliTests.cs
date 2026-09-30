using Packet.Node.Cli;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Cli;

/// <summary><c>pdn flash-tnc</c> (#175): the argument and image checks that run before any
/// serial port is touched. The flash itself is hardware-proven through
/// <c>BootloaderNinoTncFirmwareFlasher</c> and <c>packet-tune flash-tnc</c>.</summary>
[Trait("Category", "Node")]
public sealed class PdnFlashTncCliTests : IDisposable
{
    private readonly string dir;

    public PdnFlashTncCliTests()
    {
        dir = TestPaths.NewPath("pdn-flashtnc");
        Directory.CreateDirectory(dir);
    }

    [Fact]
    public async Task Missing_arguments_are_a_usage_error()
    {
        (await PdnFlashTncCli.RunAsync(["flash-tnc"])).Should().Be(2);
        (await PdnFlashTncCli.RunAsync(["flash-tnc", "/dev/ttyACM0"])).Should().Be(2);
        (await PdnFlashTncCli.RunAsync(["flash-tnc", "--yes", "/dev/ttyACM0"])).Should().Be(2);
    }

    [Fact]
    public async Task An_unreadable_or_malformed_image_is_refused_before_the_port_is_opened()
    {
        (await PdnFlashTncCli.RunAsync(["flash-tnc", "/dev/ttyACM0", Path.Combine(dir, "missing.hex"), "--yes"])).Should().Be(2);

        var junk = Path.Combine(dir, "junk.hex");
        await File.WriteAllTextAsync(junk, "this is not an Intel HEX image\n");
        (await PdnFlashTncCli.RunAsync(["flash-tnc", "/dev/ttyACM0", junk, "--yes"])).Should().Be(2);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
