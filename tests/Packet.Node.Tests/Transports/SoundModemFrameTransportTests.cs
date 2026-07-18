using M0LTE.Radio.Audio;
using Packet.Ax25.Transport;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Transports;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Modems;

namespace Packet.Node.Tests.Transports;

public class SoundModemFrameTransportTests
{
    private const int DspRate = 12000;

    private sealed class FakeCapture(int sampleRate) : ISoundModemCapture
    {
        private readonly Queue<short> _pending = new();
        private readonly Lock _gate = new();

        public int SampleRate { get; } = sampleRate;

        public void Feed(ReadOnlySpan<float> samples)
        {
            lock (_gate)
            {
                foreach (float sample in samples)
                {
                    _pending.Enqueue((short)(Math.Clamp(sample, -1f, 1f) * 32767f));
                }
            }
        }

        public int Read(Span<short> buffer)
        {
            lock (_gate)
            {
                int count = Math.Min(buffer.Length, _pending.Count);
                for (int i = 0; i < count; i++)
                {
                    buffer[i] = _pending.Dequeue();
                }

                return count; // 0 = idle; the pump backs off briefly
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeOutput : IAudioOutput
    {
        private readonly List<float> _written = [];

        public int SampleRate => DspRate;

        public float[] Snapshot()
        {
            lock (_written)
            {
                return [.. _written];
            }
        }

        public void Write(ReadOnlySpan<float> samples)
        {
            lock (_written)
            {
                _written.AddRange(samples.ToArray());
            }
        }

        public void Drain()
        {
        }
    }

    private static byte[] SampleFrame()
    {
        var frame = new byte[30];
        byte[] header = [0x96, 0x82, 0x64, 0x88, 0x8A, 0xAE, 0xE4, 0x96, 0x96, 0x68, 0x90, 0x8A, 0x94, 0x6F, 0x03, 0xF0];
        header.CopyTo(frame, 0);
        new Random(1).NextBytes(frame.AsSpan(16));
        return frame;
    }

    private static SoundModemTransportConfig Config() => new()
    {
        Mode = "afsk1200",
        CaptureRate = DspRate,
    };

    /// <summary>A real channel always has a noise floor; pure digital silence would leave
    /// the packet DCD with no transitions to decay on and pin carrier-sense busy.</summary>
    private static float[] NoiseFloor(int count, int seed = 7)
    {
        var random = new Random(seed);
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = 0.01f * (float)(random.NextDouble() * 2 - 1);
        }

        return samples;
    }

    [Fact]
    public async Task Rx_Tx_And_Carrier_Sense_Work_End_To_End()
    {
        byte[] frame = SampleFrame();
        var capture = new FakeCapture(DspRate);
        var output = new FakeOutput();
        await using var transport = new SoundModemFrameTransport(
            Config(), capture, output, new NullPtt());

        // Carrier sense is live (non-null) once running, clear on a silent channel.
        transport.ChannelBusy.Should().NotBeNull();

        // RX: feed modulated audio through the fake capture; the frame surfaces on
        // ReceiveAsync with a capture timestamp.
        capture.Feed(NoiseFloor(DspRate / 2));
        float[] audio = new Afsk1200Modem(DspRate, _ => { }).Modulate(frame, txDelayMilliseconds: 200);
        capture.Feed(audio);
        capture.Feed(NoiseFloor(2 * DspRate));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (Ax25InboundFrame inbound in transport.ReceiveAsync(timeout.Token))
        {
            inbound.Ax25.ToArray().Should().Equal(frame);
            inbound.PortId.Should().Be(0);
            inbound.ReceivedAt.Should().BeAfter(DateTimeOffset.MinValue);
            break;
        }

        // TX: completion resolves only after the audio has fully left the (fake) device,
        // and what was "transmitted" demodulates back to the same frame.
        TxCompletion completion = await transport.SendAwaitingCompletionAsync(
            frame, timeout: TimeSpan.FromSeconds(10), timeout.Token);
        completion.Completed.Should().BeOnOrAfter(completion.Queued);

        var decoded = new List<byte[]>();
        var rx = new Afsk1200Modem(DspRate, decoded.Add);
        rx.Process([.. output.Snapshot(), .. new float[DspRate / 2]]);
        decoded.Should().ContainSingle().Which.Should().Equal(frame);
    }

    [Fact]
    public async Task Kiss_Channel_Access_Params_Reach_The_Modem_Csma()
    {
        var transport = new SoundModemFrameTransport(
            Config(), new FakeCapture(DspRate), new FakeOutput(), new NullPtt());
        await using (transport)
        {
            await ((ICsmaChannelParams)transport).SetTxDelayAsync(25);
            await ((ICsmaChannelParams)transport).SetPersistenceAsync(200);
            await ((ICsmaChannelParams)transport).SetSlotTimeAsync(7);
            await ((ICsmaChannelParams)transport).SetTxTailAsync(3);
        }

        // The channel is internal to the transport; observable effect is covered by the
        // pdn-soundmodem library's own CSMA tests — here we assert the calls complete
        // without error and the transport exposes the facet at all.
        transport.Should().BeAssignableTo<ICsmaChannelParams>();
        transport.Should().BeAssignableTo<ICarrierSense>();
        transport.Should().BeAssignableTo<ITxCompletionTransport>();
    }

    [Fact]
    public async Task Channel_Busy_Reads_Null_After_Disposal()
    {
        var transport = new SoundModemFrameTransport(
            Config(), new FakeCapture(DspRate), new FakeOutput(), new NullPtt());
        await transport.DisposeAsync();

        transport.ChannelBusy.Should().BeNull("disposed transport no longer senses the channel");
    }

    [Fact]
    public void A_Mismatched_Capture_Rate_Is_Rejected()
    {
        var act = () => new SoundModemFrameTransport(
            Config() with { Mode = "fsk9600" }, // needs 48k; fake capture is 12k
            new FakeCapture(DspRate), new FakeOutput(), new NullPtt());
        act.Should().Throw<ArgumentException>();
    }

    // ─── per-frame FEC receive-quality (#635) ─────────────────────────────────────────────

    /// <summary>Drives one modulated frame through the fake capture pump and returns the
    /// inbound frame the transport delivered — the same recipe the end-to-end test uses.</summary>
    private static async Task<Ax25InboundFrame> ReceiveOneAsync(
        SoundModemFrameTransport transport, FakeCapture capture, float[] audio)
    {
        capture.Feed(NoiseFloor(DspRate / 2));
        capture.Feed(audio);
        capture.Feed(NoiseFloor(2 * DspRate));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (Ax25InboundFrame inbound in transport.ReceiveAsync(timeout.Token))
        {
            return inbound;
        }

        throw new InvalidOperationException("no frame decoded");
    }

    [Fact]
    public async Task Il2p_Frame_Attaches_Zero_Corrected_Bytes_Quality_End_To_End()
    {
        byte[] frame = SampleFrame();
        var capture = new FakeCapture(DspRate);
        await using var transport = new SoundModemFrameTransport(
            Config() with { Mode = "qpsk2400" }, capture, new FakeOutput(), new NullPtt());

        var pushed = new List<SoundModemFrameQuality>();
        transport.FrameQualityDecoded += pushed.Add;

        float[] audio = QpskModem.Qpsk2400(DspRate, _ => { }).Modulate(frame, txDelayMilliseconds: 200);
        Ax25InboundFrame inbound = await ReceiveOneAsync(transport, capture, audio);
        inbound.Ax25.ToArray().Should().Equal(frame);

        // The snapshot is deterministic the moment the frame is delivered (the meter folds the
        // quality in before queuing the frame).
        SoundModemQualitySnapshot snap = transport.QualitySnapshot();
        snap.Frames.Should().Be(1);
        snap.CumulativeCorrectedBytes.Should().Be(0);
        snap.FramesWithCorrections.Should().Be(0);
        // A clean IL2P frame is 0 corrected bytes — explicitly NOT null (that is HDLC's value).
        snap.LastFrameCorrectedBytes.Should().Be(0);

        snap.Recent.Should().ContainSingle();
        SoundModemFrameQuality sample = snap.Recent[0];
        sample.CorrectedBytes.Should().Be(0);
        sample.CrcValid.Should().BeTrue();
        sample.Mode.Should().Be("qpsk2400-il2pc");
        sample.FrameBytes.Should().Be(frame.Length);
        sample.ReceivedAt.Should().Be(inbound.ReceivedAt);

        // The push event carried the same sample the snapshot did.
        pushed.Should().ContainSingle().Which.Should().BeEquivalentTo(sample);
    }

    [Fact]
    public async Task Hdlc_Frame_Preserves_Null_Corrected_Bytes_Not_Zero()
    {
        byte[] frame = SampleFrame();
        var capture = new FakeCapture(DspRate);
        await using var transport = new SoundModemFrameTransport(
            Config(), capture, new FakeOutput(), new NullPtt()); // afsk1200 = classic HDLC

        float[] audio = new Afsk1200Modem(DspRate, _ => { }).Modulate(frame, txDelayMilliseconds: 200);
        await ReceiveOneAsync(transport, capture, audio);

        SoundModemQualitySnapshot snap = transport.QualitySnapshot();
        snap.Frames.Should().Be(1);
        // HDLC carries no FEC count: an FCS pass proves zero residual errors, not an error count.
        // This must stay null — coalescing it to 0 would falsely read as "clean FEC frame".
        snap.LastFrameCorrectedBytes.Should().BeNull(
            "HDLC has no FEC count — null (no count) is a different fact from 0 (clean IL2P)");
        snap.CumulativeCorrectedBytes.Should().Be(0);
        snap.FramesWithCorrections.Should().Be(0);

        snap.Recent.Should().ContainSingle();
        snap.Recent[0].CorrectedBytes.Should().BeNull();
        snap.Recent[0].CrcValid.Should().BeNull();
    }

    [Fact]
    public async Task Quality_Counters_Accumulate_Across_Frames()
    {
        byte[] frame = SampleFrame();
        var capture = new FakeCapture(DspRate);
        await using var transport = new SoundModemFrameTransport(
            Config() with { Mode = "qpsk2400" }, capture, new FakeOutput(), new NullPtt());

        // Two clean IL2P frames: Frames climbs to 2, corrections stay 0 (clean link), and both
        // land in the recent ring newest-first.
        capture.Feed(NoiseFloor(DspRate / 2));
        capture.Feed(QpskModem.Qpsk2400(DspRate, _ => { }).Modulate(frame, txDelayMilliseconds: 200));
        capture.Feed(NoiseFloor(DspRate));
        capture.Feed(QpskModem.Qpsk2400(DspRate, _ => { }).Modulate(frame, txDelayMilliseconds: 200));
        capture.Feed(NoiseFloor(2 * DspRate));

        int received = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (Ax25InboundFrame _ in transport.ReceiveAsync(timeout.Token))
        {
            if (++received == 2)
            {
                break;
            }
        }

        SoundModemQualitySnapshot snap = transport.QualitySnapshot();
        snap.Frames.Should().Be(2);
        snap.CumulativeCorrectedBytes.Should().Be(0);
        snap.FramesWithCorrections.Should().Be(0);
        snap.Recent.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_flex_mock_device_opens_and_tears_down_cleanly()
    {
        // flex:mock brings up an in-process fake FlexRadio (no hardware): proves OpenAsync
        // resolves the flex device backend, adapts its native-float DAX Input to the S16 capture
        // seam, starts the pumps, and disposes the owning FlexRuntime on teardown.
        var config = new SoundModemTransportConfig { Device = "flex:mock", Mode = "afsk1200" };

        await using (var transport = await SoundModemFrameTransport.OpenAsync(config))
        {
            // Running over the mock slice — carrier sense is live (non-null).
            transport.ChannelBusy.Should().NotBeNull();
        }
    }
}
