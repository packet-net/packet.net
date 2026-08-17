using System.Runtime.CompilerServices;
using M0LTE.Radio.Audio;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Transports;

/// <summary>
/// Which transport the KISS-param write and the spectrum feed must look at (review item C027,
/// #694): on an RSSI-capable radio-attached port the modem chain is wrapped in an
/// <c>RssiTaggingTransport</c> / <c>InboundRadioTap</c> that implements <see cref="IAx25Transport"/>
/// only — it forwards neither <see cref="ICsmaChannelParams"/> nor the concrete modem type. Both
/// call sites type-tested <c>RunningPort.Transport</c>, so <c>set_kiss_param</c> reported "not
/// settable" and the waterfall 404'd on exactly the ports that have a radio, while
/// <c>/quality</c> (already on <c>ModemTransport</c>) worked. These pin the seam with a
/// pass-through decorator standing in for the RSSI wrapper.
/// </summary>
[Trait("Category", "Node")]
public sealed class ModemTransportSelectionTests
{
    private const int DspRate = 12000;

    /// <summary>A pass-through decorator that implements <see cref="IAx25Transport"/> and
    /// NOTHING else — exactly what the RSSI-tagging wrapper looks like from outside.</summary>
    private sealed class PassThroughTransport(IAx25Transport inner) : IAx25Transport
    {
        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
            => inner.SendAsync(ax25, cancellationToken);

        public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default)
            => inner.ReceiveAsync(cancellationToken);

        // The wrapper does not own the chain it decorates (RunningPort disposes InnerTransport).
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A modem that DOES accept KISS CSMA params, recording what was pushed.</summary>
    private sealed class CsmaModem : IAx25Transport, ICsmaChannelParams
    {
        public byte? TxDelay { get; private set; }
        public byte? Persistence { get; private set; }
        public byte? SlotTime { get; private set; }
        public byte? TxTail { get; private set; }

        public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        {
            TxDelay = tenMsUnits;
            return Task.CompletedTask;
        }

        public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default)
        {
            Persistence = value;
            return Task.CompletedTask;
        }

        public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        {
            SlotTime = tenMsUnits;
            return Task.CompletedTask;
        }

        public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        {
            TxTail = tenMsUnits;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SilentCapture : ISoundModemCapture
    {
        public int SampleRate => DspRate;
        public int Read(Span<short> buffer) => 0;   // 0 = idle; the pump backs off
        public void Dispose() { }
    }

    private sealed class DiscardingOutput : IAudioOutput
    {
        public int SampleRate => DspRate;
        public void Write(ReadOnlySpan<float> samples) { }
        public void Drain() { }
    }

    private static RunningPort WrappedPort(IAx25Transport modem, out IAx25Transport wrapper)
    {
        var decorated = new PassThroughTransport(modem);
        wrapper = decorated;
        return new RunningPort
        {
            Id = "vhf",
            Config = new PortConfig { Id = "vhf", Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } },
            Transport = decorated,
            InnerTransport = modem,
            Listener = new Ax25Listener(
                decorated,
                new Ax25ListenerOptions { MyCall = new Callsign("M0LTE", 1) },
                TimeProvider.System),
        };
    }

    [Fact]
    public async Task A_kiss_param_write_reaches_the_modem_under_the_rssi_wrapper()
    {
        var modem = new CsmaModem();
        await using var port = WrappedPort(modem, out var wrapper);

        // The bug: the wrapper is the port's Transport and forwards no CSMA interface.
        (wrapper is ICsmaChannelParams).Should().BeFalse("the RSSI wrapper hides the modem's KISS surface");
        port.ModemTransport.Should().BeSameAs(modem);

        var throughWrapper = await KissParamWriter.ApplyAsync(port.Transport, "txdelay", 30);
        throughWrapper.Accepted.Should().BeFalse("this is what set_kiss_param used to do on a radio-attached port");

        var throughModem = await KissParamWriter.ApplyAsync(port.ModemTransport, "txdelay", 30);
        throughModem.Accepted.Should().BeTrue();
        modem.TxDelay.Should().Be(30);
    }

    [Fact]
    public async Task The_spectrum_feed_finds_the_soundmodem_under_the_rssi_wrapper()
    {
        await using var modem = new SoundModemFrameTransport(
            new SoundModemTransportConfig { Mode = "afsk1200", CaptureRate = DspRate },
            new SilentCapture(),
            new DiscardingOutput(),
            new NullPtt());

        var decorated = new PassThroughTransport(modem);
        var port = new RunningPort
        {
            Id = "vhf",
            Config = new PortConfig { Id = "vhf", Enabled = true, Transport = new KissTcpTransport { Host = "mem", Port = 1 } },
            Transport = decorated,
            InnerTransport = modem,
            Listener = new Ax25Listener(
                decorated,
                new Ax25ListenerOptions { MyCall = new Callsign("M0LTE", 1) },
                TimeProvider.System),
        };

        // The endpoint's type test, before and after: Transport is the wrapper (404 - "not a
        // soundmodem port"), ModemTransport is the modem the waterfall streams from.
        (port.Transport is SoundModemFrameTransport).Should().BeFalse();
        (port.ModemTransport is SoundModemFrameTransport).Should().BeTrue();

        await port.Listener.DisposeAsync();
    }
}
