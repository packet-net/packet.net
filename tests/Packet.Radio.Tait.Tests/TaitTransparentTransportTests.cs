using Packet.Ax25.Transport;
using Packet.Kiss;
using Packet.Radio.Tait.Ccdi;

namespace Packet.Radio.Tait.Tests;

/// <summary>
/// Unit tests for <see cref="TaitTransparentTransport"/> over a scripted <see cref="ISerialIo"/>:
/// KISS-SLIP round-trip, frame-boundary reassembly across chunk splits, TX/RX timing computation,
/// and Transparent-mode exit (with baud restore) on dispose — all without hardware.
/// </summary>
/// <remarks>
/// These run on the real clock: the CCDI enter transaction settles on a prompt-error grace timer
/// (a few tens of ms) that a frozen fake clock would stall forever, so timing assertions check
/// the derived <em>relationships</em> (which are exact, being computed from the options) rather
/// than absolute wall-clock instants.
/// </remarks>
public class TaitTransparentTransportTests
{
    // A sample AX.25-ish frame body whose bytes include FEND (0xC0), FESC (0xDB) and 0x00, so the
    // SLIP escaping is genuinely exercised (the "8-bit clean" proof).
    private static readonly byte[] SampleAx25 =
    [
        0x9C, 0x94, 0x6E, 0xA0, 0x40, 0x40, 0xE0, // dest
        0x9C, 0x94, 0x6E, 0xA0, 0x40, 0x40, 0x61, // source
        0x03, 0xF0,                               // UI + no-L3
        0xC0, 0xDB, 0x00, (byte)'H', (byte)'i',   // info incl. escapable bytes
    ];

    // The exact CCDI wire command the transport issues to enter Transparent mode with the default
    // '+' escape (param "+0"): computed by the real codec so the checksum is never guessed.
    private static string EnterCommand(char escape = '+') => new CcdiFrame('t', $"{escape}0").Encode();

    private static (FakeSerialIo Io, TaitCcdiRadio Radio) OpenRadio()
    {
        var io = new FakeSerialIo();
        io.RespondTo(EnterCommand(), "."); // the enter transaction completes on the CCDI prompt
        var radio = TaitCcdiRadio.OpenForTest(io, new TaitCcdiRadioOptions { KeepAliveInterval = null });
        return (io, radio);
    }

    [Fact]
    public async Task Enter_ReClocks_Port_When_Transparent_Baud_Differs()
    {
        var (io, radio) = OpenRadio();
        var options = new TaitTransparentTransportOptions { CommandBaud = 28800, TransparentBaud = 19200 };
        await using var transport = new TaitTransparentTransport(radio, options);

        await transport.EnterTransparentModeAsync();

        io.WrittenAscii.Should().Contain(EnterCommand());
        io.BaudRates.Should().Equal(19200);
    }

    [Fact]
    public async Task Enter_Does_Not_ReClock_When_Bauds_Match()
    {
        var (io, radio) = OpenRadio();
        var options = new TaitTransparentTransportOptions { CommandBaud = 28800, TransparentBaud = 28800 };
        await using var transport = new TaitTransparentTransport(radio, options);

        await transport.EnterTransparentModeAsync();

        io.BaudRates.Should().BeEmpty();
    }

    [Fact]
    public async Task Send_Slip_Frames_The_Ax25_Over_The_Byte_Pipe()
    {
        var (io, radio) = OpenRadio();
        await using var transport = new TaitTransparentTransport(radio, new TaitTransparentTransportOptions());
        await transport.EnterTransparentModeAsync();

        int baseLen = io.WrittenBytes.Length; // everything after this is the SLIP frame
        await transport.SendAsync(SampleAx25);

        byte[] onAir = io.WrittenBytes[baseLen..];
        onAir[0].Should().Be(KissFraming.Fend, "a SLIP frame is FEND-delimited so the peer finds boundaries");
        onAir[^1].Should().Be(KissFraming.Fend);

        // De-SLIP it back and confirm the AX.25 body survived (escaping round-trips).
        var frames = new KissDecoder().Push(onAir);
        frames.Should().ContainSingle();
        frames[0].Command.Should().Be(KissCommand.Data);
        frames[0].Payload.Should().Equal(SampleAx25);
    }

    [Fact]
    public async Task Receive_Deframes_A_Whole_Slip_Frame_Into_Ax25()
    {
        var (io, radio) = OpenRadio();
        await using var transport = new TaitTransparentTransport(radio, new TaitTransparentTransportOptions());
        await transport.EnterTransparentModeAsync();

        byte[] slip = KissEncoder.Encode(0, KissCommand.Data, SampleAx25);
        io.Enqueue(slip);

        var frame = await FirstFrameAsync(transport);
        frame.Ax25.ToArray().Should().Equal(SampleAx25);
    }

    [Fact]
    public async Task Receive_Reassembles_A_Frame_Split_Across_Chunks()
    {
        var (io, radio) = OpenRadio();
        await using var transport = new TaitTransparentTransport(radio, new TaitTransparentTransportOptions());
        await transport.EnterTransparentModeAsync();

        // The radio pump delivers whatever chunk boundaries the wire produced — split one SLIP
        // frame into three arbitrary chunks and the stateful decoder must still find one frame.
        byte[] slip = KissEncoder.Encode(0, KissCommand.Data, SampleAx25);
        io.Enqueue(slip[..3]);
        io.Enqueue(slip[3..7]);
        io.Enqueue(slip[7..]);

        var frame = await FirstFrameAsync(transport);
        frame.Ax25.ToArray().Should().Equal(SampleAx25);
    }

    [Fact]
    public async Task Receive_Splits_Two_Frames_Concatenated_In_One_Chunk()
    {
        var (io, radio) = OpenRadio();
        await using var transport = new TaitTransparentTransport(radio, new TaitTransparentTransportOptions());
        await transport.EnterTransparentModeAsync();

        byte[] first = KissEncoder.Encode(0, KissCommand.Data, SampleAx25);
        byte[] second = KissEncoder.Encode(0, KissCommand.Data, [0x01, 0x02, 0x03]);
        io.Enqueue([.. first, .. second]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = transport.ReceiveAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Ax25.ToArray().Should().Equal(SampleAx25);
        (await e.MoveNextAsync()).Should().BeTrue();
        e.Current.Ax25.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task Inbound_Frame_Carries_ReceivedAt_And_Airtime_But_No_Rssi()
    {
        var (io, radio) = OpenRadio();
        var options = new TaitTransparentTransportOptions { FfskBaud = 2400 };
        await using var transport = new TaitTransparentTransport(radio, options);
        await transport.EnterTransparentModeAsync();

        byte[] slip = KissEncoder.Encode(0, KissCommand.Data, SampleAx25);
        io.Enqueue(slip);

        var frame = await FirstFrameAsync(transport);

        frame.ReceivedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10));
        frame.Radio.Should().NotBeNull();
        var radioMeta = frame.Radio!.Value;
        radioMeta.EstimatedAirtime.Should().Be(TimeSpan.FromSeconds(slip.Length * 8.0 / 2400));
        // No CCDI control channel in Transparent mode → no signal telemetry.
        radioMeta.RssiDbm.Should().BeNull();
        radioMeta.SnrDb.Should().BeNull();
        radioMeta.NoiseFloorDbm.Should().BeNull();
        radioMeta.CarrierRiseAt.Should().BeNull();
        radioMeta.BurstIndex.Should().BeNull();
        radioMeta.PreDataCarrier.Should().BeNull();
    }

    [Fact]
    public async Task Tx_Timing_Reports_OnAir_Start_And_End()
    {
        var (io, radio) = OpenRadio();
        var options = new TaitTransparentTransportOptions
        {
            FfskBaud = 2400,
            LeadIn = TimeSpan.FromMilliseconds(120),
        };
        await using var transport = new TaitTransparentTransport(radio, options);
        await transport.EnterTransparentModeAsync();

        TaitTransparentTxTiming? seen = null;
        transport.TxTiming += (_, t) => seen = t;

        var before = DateTimeOffset.UtcNow;
        await transport.SendAsync(SampleAx25);
        var after = DateTimeOffset.UtcNow;

        int framedLen = KissEncoder.Encode(0, KissCommand.Data, SampleAx25).Length;
        var expectedAirtime = TimeSpan.FromSeconds(framedLen * 8.0 / 2400);

        seen.Should().NotBeNull();
        var timing = seen!.Value;
        timing.Queued.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        timing.LeadIn.Should().Be(TimeSpan.FromMilliseconds(120));
        timing.OnAirStart.Should().Be(timing.Queued + TimeSpan.FromMilliseconds(120));
        timing.EstimatedAirtime.Should().Be(expectedAirtime);
        timing.OnAirEnd.Should().Be(timing.OnAirStart + expectedAirtime);
        timing.OnAirByteCount.Should().Be(framedLen);
    }

    [Fact]
    public async Task SendAwaitingCompletion_Returns_Modelled_Airtime()
    {
        var (io, radio) = OpenRadio();
        var options = new TaitTransparentTransportOptions
        {
            FfskBaud = 2400,
            LeadIn = TimeSpan.FromMilliseconds(50),
        };
        await using var transport = new TaitTransparentTransport(radio, options);
        await transport.EnterTransparentModeAsync();

        int framedLen = KissEncoder.Encode(0, KissCommand.Data, SampleAx25).Length;
        var expectedElapsed = TimeSpan.FromMilliseconds(50) + TimeSpan.FromSeconds(framedLen * 8.0 / 2400);

        var completion = await transport.SendAwaitingCompletionAsync(SampleAx25);

        // Completed is computed (queued + lead-in + airtime), so Elapsed is exact regardless of
        // the real wall-clock wait the call incurred.
        completion.Elapsed.Should().Be(expectedElapsed);
        completion.Completed.Should().Be(completion.Queued + expectedElapsed);
    }

    [Fact]
    public async Task Dispose_Exits_Transparent_And_Restores_Command_Baud()
    {
        var (io, radio) = OpenRadio();
        var options = new TaitTransparentTransportOptions { CommandBaud = 28800, TransparentBaud = 19200 };
        var transport = new TaitTransparentTransport(radio, options, ownsRadio: true);
        await transport.EnterTransparentModeAsync();

        // The escape is a real ~4 s protocol dance (2.1 s idle, +++, 2.1 s idle).
        await transport.DisposeAsync();

        // The +++ escape (default '+') went out on the byte pipe...
        io.WrittenAscii.Should().Contain("+++");
        // ...and the port was re-clocked to transparent on enter, then back to command on exit.
        io.BaudRates.Should().Equal(19200, 28800);
    }

    private static async Task<Ax25InboundFrame> FirstFrameAsync(TaitTransparentTransport transport)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var frame in transport.ReceiveAsync(cts.Token))
        {
            return frame;
        }
        throw new InvalidOperationException("no inbound frame arrived");
    }
}
