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
        exception.WithMessage("*Firmware flashing is not yet supported*");
    }
}
