using Packet.Ax25.Transport;
using Packet.Kiss.Serial;

namespace Packet.Kiss.Serial.Tests;

public class KissSerialModemTests
{
    [Fact]
    public void Open_Rejects_Null_Port_Name()
    {
        Assert.Throws<ArgumentNullException>(() => KissSerialModem.Open(null!));
    }

    [Fact]
    public void Open_Rejects_Empty_Port_Name()
    {
        Assert.Throws<ArgumentException>(() => KissSerialModem.Open(""));
    }

    [Fact]
    public void Implements_IAx25Transport()
    {
        typeof(KissSerialModem).Should().Implement<IAx25Transport>();
    }

    [Fact]
    public void Implements_ICsmaChannelParams()
    {
        typeof(KissSerialModem).Should().Implement<ICsmaChannelParams>();
    }

    [Fact]
    public void Implements_IAsyncDisposable()
    {
        typeof(KissSerialModem).Should().Implement<IAsyncDisposable>();
    }

    [Fact]
    public void Implements_IDisposable()
    {
        typeof(KissSerialModem).Should().Implement<IDisposable>();
    }

    [Fact]
    public void DefaultBaudRate_Is_57600()
    {
        KissSerialModem.DefaultBaudRate.Should().Be(57600);
    }
}
