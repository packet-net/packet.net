using Packet.Kiss.NinoTnc.Firmware;

namespace Packet.Kiss.NinoTnc.Tests.Firmware;

public class UnsupportedFirmwareFlasherTests
{
    [Fact]
    public async Task FlashAsync_Throws_NotSupportedException_With_A_Helpful_Pointer()
    {
        var flasher = new UnsupportedFirmwareFlasher();
        var act = async () => await flasher.FlashAsync("COM6", new byte[] { 0x01 });

        var exception = await act.Should().ThrowAsync<NotSupportedException>();
        exception.WithMessage("*Firmware flashing is not supported on this host*");
    }

    [Fact]
    public async Task FlashAsync_Throws_Even_With_A_Cancelled_Token_And_Progress_Reporter()
    {
        // Calling the stub with every-optional-arg set should still
        // throw the same NotSupportedException — not swallow it because
        // the token happened to be cancelled or because a progress
        // reporter was passed. The point of the stub is that no flash
        // path is wired up; callers must see the unsupported signal.
        var flasher = new UnsupportedFirmwareFlasher();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var progress = new Progress<NinoTncFlashProgress>(_ => { });

        var act = async () => await flasher.FlashAsync(
            portName: "COM6",
            hexImage: new byte[] { 0x01 },
            progress: progress,
            cancellationToken: cts.Token);

        await act.Should().ThrowAsync<NotSupportedException>();
    }
}
