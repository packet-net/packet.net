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
