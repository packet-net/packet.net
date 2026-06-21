using System.Threading;
using System.Threading.Tasks;
using Packet.Ax25.Transport;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// <see cref="KissParamWriter"/> — the MCP <c>set_kiss_param</c> write path. It owns
/// the settable-param set + range validation and dispatches to the matching
/// <see cref="ICsmaChannelParams"/> setter so the value reaches the transport. These tests
/// prove each settable param is dispatched (not a no-op), and that bad input is rejected
/// with a clear message instead of being silently dropped or faked as success.
/// </summary>
public sealed class KissParamWriterTests
{
    [Theory]
    [InlineData("txdelay", 40)]
    [InlineData("TXDELAY", 40)]   // case-insensitive
    [InlineData(" persist ", 63)] // trimmed
    public async Task Settable_params_are_dispatched_to_the_modem(string param, int value)
    {
        var modem = new RecordingModem();

        var result = await KissParamWriter.ApplyAsync(modem, param, value);

        result.Accepted.Should().BeTrue();
        result.RequiresRestart.Should().BeFalse();
    }

    [Fact]
    public async Task Txdelay_reaches_the_modem()
    {
        var modem = new RecordingModem();
        var result = await KissParamWriter.ApplyAsync(modem, "txdelay", 40);
        result.Accepted.Should().BeTrue();
        modem.TxDelay.Should().Be(40);
        modem.Persistence.Should().BeNull();
        modem.SlotTime.Should().BeNull();
        modem.TxTail.Should().BeNull();
    }

    [Fact]
    public async Task Persist_reaches_the_modem()
    {
        var modem = new RecordingModem();
        var result = await KissParamWriter.ApplyAsync(modem, "persist", 200);
        result.Accepted.Should().BeTrue();
        modem.Persistence.Should().Be(200);
    }

    [Fact]
    public async Task Slottime_reaches_the_modem()
    {
        var modem = new RecordingModem();
        var result = await KissParamWriter.ApplyAsync(modem, "slottime", 10);
        result.Accepted.Should().BeTrue();
        modem.SlotTime.Should().Be(10);
    }

    [Fact]
    public async Task Txtail_reaches_the_modem()
    {
        var modem = new RecordingModem();
        var result = await KissParamWriter.ApplyAsync(modem, "txtail", 5);
        result.Accepted.Should().BeTrue();
        modem.TxTail.Should().Be(5);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(1000)]
    public async Task Out_of_range_value_is_rejected_without_touching_the_modem(int value)
    {
        var modem = new RecordingModem();

        var result = await KissParamWriter.ApplyAsync(modem, "txdelay", value);

        result.Accepted.Should().BeFalse();
        result.Message.Should().Contain("out of range");
        modem.TxDelay.Should().BeNull(); // never dispatched
    }

    [Theory]
    [InlineData("fullduplex")] // a real KISS knob, but not on the ICsmaChannelParams surface — not settable here
    [InlineData("sethardware")]
    [InlineData("bogus")]
    public async Task Unknown_param_is_rejected_with_the_settable_set_listed(string param)
    {
        var modem = new RecordingModem();

        var result = await KissParamWriter.ApplyAsync(modem, param, 1);

        result.Accepted.Should().BeFalse();
        result.Message.Should().Contain("txdelay");
        modem.TxDelay.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_param_name_is_rejected(string param)
    {
        var modem = new RecordingModem();

        var result = await KissParamWriter.ApplyAsync(modem, param, 1);

        result.Accepted.Should().BeFalse();
    }

    [Fact]
    public void Settable_param_set_is_the_four_csma_knobs()
    {
        KissParamWriter.SettableParams.Should()
            .BeEquivalentTo(new[] { "txdelay", "persist", "slottime", "txtail" });
    }

    [Fact]
    public async Task Boundary_values_0_and_255_are_accepted()
    {
        var lo = new RecordingModem();
        var hi = new RecordingModem();

        (await KissParamWriter.ApplyAsync(lo, "persist", 0)).Accepted.Should().BeTrue();
        (await KissParamWriter.ApplyAsync(hi, "persist", 255)).Accepted.Should().BeTrue();

        lo.Persistence.Should().Be(0);
        hi.Persistence.Should().Be(255);
    }

    /// <summary>A minimal <see cref="IAx25Transport"/> with the <see cref="ICsmaChannelParams"/>
    /// capability that records the last value set per param.</summary>
    private sealed class RecordingModem : IAx25Transport, ICsmaChannelParams
    {
        public byte? TxDelay { get; private set; }
        public byte? Persistence { get; private set; }
        public byte? SlotTime { get; private set; }
        public byte? TxTail { get; private set; }

        public Task SetTxDelayAsync(byte v, CancellationToken cancellationToken = default) { TxDelay = v; return Task.CompletedTask; }
        public Task SetPersistenceAsync(byte v, CancellationToken cancellationToken = default) { Persistence = v; return Task.CompletedTask; }
        public Task SetSlotTimeAsync(byte v, CancellationToken cancellationToken = default) { SlotTime = v; return Task.CompletedTask; }
        public Task SetTxTailAsync(byte v, CancellationToken cancellationToken = default) { TxTail = v; return Task.CompletedTask; }

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
