using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Transport;

namespace Packet.Radio.Tests;

public class RssiTaggingTransportTests
{
    private static readonly RssiTaggingOptions Options = new()
    {
        BusySamplePeriod = TimeSpan.FromMilliseconds(40),
        IdleSamplePeriod = TimeSpan.FromMilliseconds(400),
        BitRateHzProvider = () => 1200,
    };

    /// <summary>A uniform 100 ms poll cadence, so the two-station scene below lands a known
    /// number of samples in each carrier window without depending on the busy/idle switch.</summary>
    private static readonly RssiTaggingOptions TwoStationOptions = new()
    {
        BusySamplePeriod = TimeSpan.FromMilliseconds(100),
        IdleSamplePeriod = TimeSpan.FromMilliseconds(100),
        BitRateHzProvider = () => 1200,
    };

    [Fact]
    public async Task First_Frame_In_A_Window_Gets_Stats_Rise_Burst0_And_PreDataCarrier()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio { ChannelBusy = false, RssiDbm = -128f };
        var transport = new FakeTransport();
        await using var tagged = new RssiTaggingTransport(transport, radio, Options, time);

        // Two idle polls establish the noise floor at -128.
        await radio.WaitForReadsAsync(1);
        time.Advance(TimeSpan.FromMilliseconds(400));
        await radio.WaitForReadsAsync(1);

        // Carrier rises; the channel now reads -90. (Nudge the clock so the rise instant is
        // strictly after the last idle sample — inclusive window boundaries.)
        time.Advance(TimeSpan.FromMilliseconds(1));
        var rise = time.GetUtcNow();
        radio.RssiDbm = -90f;
        radio.RaiseCarrierSense(true, rise);
        for (int i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(400)); // covers whichever period is pending
            await radio.WaitForReadsAsync(1);
        }

        // 147-byte frame at 1200 bd -> (147+3)*8/1200 = 1.0 s estimated airtime.
        var receivedAt = time.GetUtcNow();
        transport.Push(new Ax25InboundFrame(new byte[147], 0, receivedAt));

        var frame = await ReadOneAsync(tagged);

        frame.Radio.Should().NotBeNull();
        var radioMeta = frame.Radio!.Value;
        radioMeta.RssiDbm.Should().BeApproximately(-90f, 0.01f);
        radioMeta.NoiseFloorDbm.Should().BeApproximately(-128f, 0.01f);
        radioMeta.SnrDb.Should().BeApproximately(38f, 0.01f);
        radioMeta.RssiMinDbm.Should().BeApproximately(-90f, 0.01f);
        radioMeta.RssiMaxDbm.Should().BeApproximately(-90f, 0.01f);
        radioMeta.RssiSampleCount.Should().BeGreaterThanOrEqualTo(3);
        radioMeta.CarrierRiseAt.Should().Be(rise);
        radioMeta.BurstIndex.Should().Be(0);
        radioMeta.EstimatedAirtime.Should().Be(TimeSpan.FromSeconds(1));
        radioMeta.PreDataCarrier.Should().Be(receivedAt - TimeSpan.FromSeconds(1) - rise);
    }

    [Fact]
    public async Task Later_Frames_In_The_Same_Window_Get_Burst_Indices_And_No_PreDataCarrier()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio { ChannelBusy = false, RssiDbm = -128f };
        var transport = new FakeTransport();
        await using var tagged = new RssiTaggingTransport(transport, radio, Options, time);
        await radio.WaitForReadsAsync(1);

        var rise = time.GetUtcNow();
        radio.RssiDbm = -95f;
        radio.RaiseCarrierSense(true, rise);
        time.Advance(TimeSpan.FromMilliseconds(400));
        await radio.WaitForReadsAsync(1);

        // Three frames delivered inside one carrier window: an AX.25 train.
        transport.Push(new Ax25InboundFrame(new byte[57], 0, time.GetUtcNow()));
        transport.Push(new Ax25InboundFrame(new byte[57], 0, time.GetUtcNow()));

        // Third frame delivered shortly AFTER carrier fall (decode+serial lag) still attributes.
        var fall = time.GetUtcNow() + TimeSpan.FromMilliseconds(10);
        radio.RaiseCarrierSense(false, fall);
        transport.Push(new Ax25InboundFrame(new byte[57], 0, fall + TimeSpan.FromMilliseconds(100)));

        var first = await ReadOneAsync(tagged);
        var second = await ReadOneAsync(tagged);
        var third = await ReadOneAsync(tagged);

        first.Radio!.Value.BurstIndex.Should().Be(0);
        first.Radio!.Value.PreDataCarrier.Should().NotBeNull();
        second.Radio!.Value.BurstIndex.Should().Be(1);
        second.Radio!.Value.PreDataCarrier.Should().BeNull("only the burst's first frame paid the TXDELAY");
        third.Radio!.Value.BurstIndex.Should().Be(2);
        third.Radio!.Value.CarrierRiseAt.Should().Be(rise, "delivery lag must not detach a frame from its window");
    }

    [Fact]
    public async Task Without_CarrierSense_Falls_Back_To_Threshold_Attribution()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio
        {
            Capabilities = RadioCapabilities.RssiRead,
            ChannelBusy = null,
            RssiDbm = -128f,
        };
        var transport = new FakeTransport();
        await using var tagged = new RssiTaggingTransport(transport, radio, Options, time);

        await radio.WaitForReadsAsync(1); // seeds the floor at -128
        radio.RssiDbm = -85f;
        time.Advance(TimeSpan.FromMilliseconds(40));
        await radio.WaitForReadsAsync(1); // one signal sample

        transport.Push(new Ax25InboundFrame(new byte[57], 0, time.GetUtcNow()));
        var frame = await ReadOneAsync(tagged);

        frame.Radio.Should().NotBeNull();
        var radioMeta = frame.Radio!.Value;
        radioMeta.RssiDbm.Should().BeApproximately(-85f, 0.01f);
        radioMeta.BurstIndex.Should().BeNull();
        radioMeta.CarrierRiseAt.Should().BeNull();
        radioMeta.PreDataCarrier.Should().BeNull();
        radioMeta.EstimatedAirtime.Should().Be(TimeSpan.FromSeconds((57 + 3) * 8.0 / 1200));
    }

    [Fact]
    public async Task A_Frame_Delivered_After_The_Next_Station_Keys_Stays_With_Its_Own_Window()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio { ChannelBusy = false, RssiDbm = -128f };
        var transport = new FakeTransport();
        await using var tagged = new RssiTaggingTransport(transport, radio, TwoStationOptions, time);

        var (riseA, fallA, riseB) = await RunTwoStationSceneAsync(time, radio);

        // Two 57-byte frames (400 ms airtime each) that A had on air before it unkeyed. The
        // second is handed over 80 ms after carrier-fall, inside the measured 34-115 ms
        // decode+serial delivery lag, by which time B has been keyed for 30 ms.
        var inWindow = riseA + TimeSpan.FromMilliseconds(479);
        var lateDelivery = fallA + TimeSpan.FromMilliseconds(80);
        lateDelivery.Should().BeAfter(riseB, "the scenario is a frame delivered after the next station keys");
        transport.Push(new Ax25InboundFrame(new byte[57], 0, inWindow));
        transport.Push(new Ax25InboundFrame(new byte[57], 0, lateDelivery));

        var first = await ReadOneAsync(tagged);
        var second = await ReadOneAsync(tagged);

        first.Radio!.Value.CarrierRiseAt.Should().Be(riseA);
        first.Radio!.Value.BurstIndex.Should().Be(0);

        var late = second.Radio!.Value;
        late.CarrierRiseAt.Should().Be(riseA, "the frame was on air before A unkeyed, whatever B is doing now");
        late.BurstIndex.Should().Be(1, "it is the second frame of A's burst, not the first of B's");
        late.RssiDbm.Should().BeApproximately(-80f, 0.01f, "the stats must come from A's carrier, not B's");
        late.PreDataCarrier.Should().BeNull("only a burst's first frame reports the sender's TXDELAY");
    }

    [Fact]
    public async Task A_Frame_That_Fits_Since_The_New_Rise_Belongs_To_The_Open_Window()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio { ChannelBusy = false, RssiDbm = -128f };
        var transport = new FakeTransport();
        await using var tagged = new RssiTaggingTransport(transport, radio, TwoStationOptions, time);

        var (_, fallA, riseB) = await RunTwoStationSceneAsync(time, radio);
        for (int i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await radio.WaitForReadsAsync(1);
        }

        // B's own first frame: 200 ms of TXDELAY then a 27-byte frame (200 ms airtime), so
        // delivery lands 400 ms after B's rise. A's window is still inside its slack and would
        // take the frame on arrival time alone, but the airtime fits entirely after B's rise.
        var receivedAt = riseB + TimeSpan.FromMilliseconds(400);
        (receivedAt - fallA).Should().BeLessThan(
            TwoStationOptions.WindowAttributionSlack, "A's closed window is still eligible");
        transport.Push(new Ax25InboundFrame(new byte[27], 0, receivedAt));

        var frame = await ReadOneAsync(tagged);

        var meta = frame.Radio!.Value;
        meta.CarrierRiseAt.Should().Be(riseB);
        meta.BurstIndex.Should().Be(0);
        meta.RssiDbm.Should().BeApproximately(-100f, 0.01f, "the stats must come from B's carrier");
        meta.EstimatedAirtime.Should().Be(TimeSpan.FromMilliseconds(200));
        meta.PreDataCarrier.Should().Be(TimeSpan.FromMilliseconds(200), "B keyed 200 ms before its data");
    }

    [Fact]
    public async Task With_No_Bit_Rate_A_Late_Delivery_Keeps_The_Window_That_Was_On_Air()
    {
        var time = new FakeTimeProvider();
        var radio = new FakeRadio { ChannelBusy = false, RssiDbm = -128f };
        var transport = new FakeTransport();
        var noBitRate = TwoStationOptions with { BitRateHzProvider = null };
        await using var tagged = new RssiTaggingTransport(transport, radio, noBitRate, time);

        var (riseA, fallA, _) = await RunTwoStationSceneAsync(time, radio);

        // No bit rate means no airtime, so nothing places the frame inside B's window; the
        // window known to have been on air over it keeps it.
        transport.Push(new Ax25InboundFrame(new byte[57], 0, fallA + TimeSpan.FromMilliseconds(80)));

        var frame = await ReadOneAsync(tagged);

        var meta = frame.Radio!.Value;
        meta.CarrierRiseAt.Should().Be(riseA);
        meta.RssiDbm.Should().BeApproximately(-80f, 0.01f);
        meta.EstimatedAirtime.Should().BeNull();
        meta.PreDataCarrier.Should().BeNull("without a bit rate there is nothing to subtract");
    }

    /// <summary>
    /// Plays the two-station scene on the fake clock: A keys, holds the channel for 500 ms at
    /// -80 dBm and unkeys; 50 ms later B keys and reads -100 dBm, so the RSSI a frame comes back
    /// with says which window it was attributed to. Returns A's rise, A's fall and B's rise.
    /// </summary>
    private static async Task<(DateTimeOffset RiseA, DateTimeOffset FallA, DateTimeOffset RiseB)>
        RunTwoStationSceneAsync(FakeTimeProvider time, FakeRadio radio)
    {
        var poll = TimeSpan.FromMilliseconds(100);
        await radio.WaitForReadsAsync(1); // one idle sample seeds the noise floor at -128

        // Nudge the clock so A's rise is strictly after that idle sample (inclusive boundaries).
        time.Advance(TimeSpan.FromMilliseconds(1));
        var riseA = time.GetUtcNow();
        radio.RssiDbm = -80f;
        radio.RaiseCarrierSense(true, riseA);
        for (int i = 0; i < 5; i++)
        {
            time.Advance(poll);
            await radio.WaitForReadsAsync(1);
        }

        var fallA = time.GetUtcNow();
        radio.RssiDbm = -128f;
        radio.RaiseCarrierSense(false, fallA);

        time.Advance(TimeSpan.FromMilliseconds(50));
        var riseB = time.GetUtcNow();
        radio.RssiDbm = -100f;
        radio.RaiseCarrierSense(true, riseB);
        time.Advance(poll);
        await radio.WaitForReadsAsync(1);

        return (riseA, fallA, riseB);
    }

    private static async Task<Ax25InboundFrame> ReadOneAsync(RssiTaggingTransport tagged)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var frame in tagged.ReceiveAsync(cts.Token))
        {
            return frame;
        }
        throw new InvalidOperationException("stream ended without a frame");
    }
}
