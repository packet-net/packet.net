using Packet.Radio.Tait.Ccdi;

namespace Packet.Radio.Tait.Tests;

public class TaitRadioHealthMonitorTests
{
    // Wire strings are built with the real codec so checksums are always valid.
    private static string Wire(char ident, string parameters) => new CcdiFrame(ident, parameters).Encode();

    private static string Reply(params string[] lines) =>
        "." + string.Concat(lines.Select(l => l + "\r")) + ".";

    private static void RespondIdle(FakeSerialIo io, string rssiTenths = "-903", int fwd = 15, int rev = 0)
    {
        io.RespondTo(Wire('q', "5063"), Reply(Wire('j', "063" + rssiTenths)));
        io.RespondTo(Wire('q', "5047"), Reply(Wire('j', "04727"), Wire('j', "047470")));
        io.RespondTo(Wire('q', "5318"), Reply(Wire('j', "318" + fwd)));
        io.RespondTo(Wire('q', "5319"), Reply(Wire('j', "319" + rev)));
    }

    private static TaitRadioHealthMonitor StartMonitor(
        TaitCcdiRadio radio, BlockingSamples samples, TaitRadioHealthMonitorOptions options)
    {
        var monitor = new TaitRadioHealthMonitor(radio, options);
        monitor.SampleTaken += (_, s) => samples.Add(s);
        return monitor;
    }

    [Fact]
    public async Task Idle_Tick_Samples_Rssi_Temperature_And_Detector_Offsets()
    {
        using var io = new FakeSerialIo();
        RespondIdle(io, rssiTenths: "-903", fwd: 15, rev: 0);
        await using var radio = TaitCcdiRadio.OpenForTest(io);
        using var samples = new BlockingSamples();
        await using var monitor = StartMonitor(radio, samples, new TaitRadioHealthMonitorOptions
        {
            SampleInterval = TimeSpan.FromSeconds(60),
        });

        var sample = await samples.NextAsync();

        sample.Transmitting.Should().BeFalse();
        sample.RssiDbm.Should().BeApproximately(-90.3f, 0.001f);
        sample.PaTemperatureCelsius.Should().Be(27);
        sample.PaDetectorMillivolts.Should().Be(470);
        sample.ForwardPowerMillivolts.Should().Be(15);
        sample.ReversePowerMillivolts.Should().Be(0);
        sample.TxForwardOverIdleMillivolts.Should().BeNull("idle samples carry no TX trend fields");
        sample.TxReverseForwardRatio.Should().BeNull();
        monitor.ForwardIdleOffsetMillivolts.Should().Be(15);
        monitor.ReverseIdleOffsetMillivolts.Should().Be(0);
    }

    [Fact]
    public async Task Keying_Edge_Takes_A_Tx_Sample_With_Idle_Offset_Correction()
    {
        using var io = new FakeSerialIo();
        RespondIdle(io, fwd: 15, rev: 2);
        await using var radio = TaitCcdiRadio.OpenForTest(io);
        using var samples = new BlockingSamples();
        await using var monitor = StartMonitor(radio, samples, new TaitRadioHealthMonitorOptions
        {
            SampleInterval = TimeSpan.FromSeconds(60),
            KeyedSampleDelay = TimeSpan.FromMilliseconds(1),
        });

        var idle = await samples.NextAsync();
        idle.Transmitting.Should().BeFalse();

        // Keyed: detectors rise (bench figures: VeryLow into the 100 dB pad reads 388/172 mV).
        io.RespondTo(Wire('q', "5318"), Reply(Wire('j', "318388")));
        io.RespondTo(Wire('q', "5319"), Reply(Wire('j', "319174")));
        io.Enqueue(Wire('p', "07") + "\r"); // PROGRESS: PTT activated

        var keyed = await samples.NextAsync();

        keyed.Transmitting.Should().BeTrue();
        keyed.RssiDbm.Should().BeNull("own-RSSI reads the muted receiver during TX");
        keyed.ForwardPowerMillivolts.Should().Be(388);
        keyed.ReversePowerMillivolts.Should().Be(174);
        keyed.ForwardIdleOffsetMillivolts.Should().Be(15);
        keyed.ReverseIdleOffsetMillivolts.Should().Be(2);
        keyed.TxForwardOverIdleMillivolts.Should().Be(373);
        keyed.TxReverseOverIdleMillivolts.Should().Be(172);
        keyed.TxReverseForwardRatio.Should().BeApproximately(172.0 / 373.0, 0.0001);

        // The averaged-RSSI query went out exactly once — for the idle tick, not the keyed one.
        CountOf(io.WrittenAscii, Wire('q', "5063")).Should().Be(1);

        // TX detector readings must not pollute the idle-offset estimate.
        monitor.ForwardIdleOffsetMillivolts.Should().Be(15);
        monitor.ReverseIdleOffsetMillivolts.Should().Be(2);
    }

    [Fact]
    public async Task Tx_Sample_Below_The_Forward_Floor_Gets_No_Ratio()
    {
        using var io = new FakeSerialIo();
        RespondIdle(io, fwd: 0, rev: 0);
        await using var radio = TaitCcdiRadio.OpenForTest(io);
        using var samples = new BlockingSamples();
        await using var monitor = StartMonitor(radio, samples, new TaitRadioHealthMonitorOptions
        {
            SampleInterval = TimeSpan.FromSeconds(60),
            KeyedSampleDelay = TimeSpan.FromMilliseconds(1),
            MinimumForwardForRatioMillivolts = 50,
        });

        await samples.NextAsync(); // idle tick establishes zero offsets

        io.RespondTo(Wire('q', "5318"), Reply(Wire('j', "31830")));
        io.RespondTo(Wire('q', "5319"), Reply(Wire('j', "31910")));
        io.Enqueue(Wire('p', "07") + "\r");

        var keyed = await samples.NextAsync();

        keyed.Transmitting.Should().BeTrue();
        keyed.TxForwardOverIdleMillivolts.Should().Be(30);
        keyed.TxReverseOverIdleMillivolts.Should().Be(10);
        keyed.TxReverseForwardRatio.Should().BeNull("below the forward floor the ratio is detector-offset noise");
    }

    [Fact]
    public async Task Keying_Mid_Sample_Discards_The_Detector_Readings()
    {
        using var io = new FakeSerialIo();
        // No canned 047 response: the temperature read times out, holding the sample open long
        // enough to inject a keying edge into its middle deterministically.
        io.RespondTo(Wire('q', "5063"), Reply(Wire('j', "063-903")));
        io.RespondTo(Wire('q', "5318"), Reply(Wire('j', "31815")));
        io.RespondTo(Wire('q', "5319"), Reply(Wire('j', "3190")));
        await using var radio = TaitCcdiRadio.OpenForTest(
            io, new TaitCcdiRadioOptions { TransactionTimeout = TimeSpan.FromMilliseconds(250) });
        using var samples = new BlockingSamples();
        await using var monitor = StartMonitor(radio, samples, new TaitRadioHealthMonitorOptions
        {
            SampleInterval = TimeSpan.FromSeconds(60),
            SampleOnKeying = false,
        });

        await WaitForWriteAsync(io, Wire('q', "5047")); // the sample is now mid-flight
        io.Enqueue(Wire('p', "07") + "\r");             // PTT activated mid-sample

        var sample = await samples.NextAsync();

        sample.Transmitting.Should().BeFalse("classified by the state at sample start");
        sample.RssiDbm.Should().BeApproximately(-90.3f, 0.001f);
        sample.PaTemperatureCelsius.Should().BeNull("the read timed out");
        sample.ForwardPowerMillivolts.Should().BeNull("a mid-sample keying edge makes detector readings unattributable");
        sample.ReversePowerMillivolts.Should().BeNull();
        monitor.ForwardIdleOffsetMillivolts.Should().BeNull("discarded readings must not seed the offsets");
    }

    [Fact]
    public async Task Failed_Reads_Null_The_Fields_And_The_Loop_Carries_On()
    {
        using var io = new FakeSerialIo();
        RespondIdle(io);
        io.RespondTo(Wire('q', "5063"), Reply(Wire('e', "001"))); // RSSI answers ERROR
        await using var radio = TaitCcdiRadio.OpenForTest(io);
        using var samples = new BlockingSamples();
        await using var monitor = StartMonitor(radio, samples, new TaitRadioHealthMonitorOptions
        {
            SampleInterval = TimeSpan.FromMilliseconds(50),
        });

        var first = await samples.NextAsync();
        var second = await samples.NextAsync();

        first.RssiDbm.Should().BeNull();
        first.PaTemperatureCelsius.Should().Be(27);
        second.PaTemperatureCelsius.Should().Be(27, "one failing metric must not stop the sampler");
    }

    [Fact]
    public async Task Summarize_Reduces_The_Window_To_Min_Median_Max()
    {
        using var io = new FakeSerialIo();
        RespondIdle(io, rssiTenths: "-903");
        await using var radio = TaitCcdiRadio.OpenForTest(io);
        using var samples = new BlockingSamples();
        await using var monitor = StartMonitor(radio, samples, new TaitRadioHealthMonitorOptions
        {
            SampleInterval = TimeSpan.FromMilliseconds(150),
        });

        await samples.NextAsync();
        io.RespondTo(Wire('q', "5063"), Reply(Wire('j', "063-897")));
        await samples.NextAsync();
        io.RespondTo(Wire('q', "5063"), Reply(Wire('j', "063-911")));
        await samples.NextAsync();

        var summary = monitor.Summarize();

        summary.SampleCount.Should().Be(3);
        summary.TransmitSampleCount.Should().Be(0);
        summary.From!.Value.Should().BeOnOrBefore(summary.To!.Value);
        summary.RssiDbm.Should().NotBeNull();
        summary.RssiDbm!.Value.Min.Should().BeApproximately(-91.1, 0.001);
        summary.RssiDbm!.Value.Median.Should().BeApproximately(-90.3, 0.001);
        summary.RssiDbm!.Value.Max.Should().BeApproximately(-89.7, 0.001);
        summary.RssiDbm!.Value.Count.Should().Be(3);
        summary.PaTemperatureCelsius!.Value.Median.Should().Be(27);
        summary.TxForwardOverIdleMillivolts.Should().BeNull("no transmit samples in the window");
    }

    [Fact]
    public async Task The_Summary_Window_Is_Bounded()
    {
        using var io = new FakeSerialIo();
        RespondIdle(io);
        await using var radio = TaitCcdiRadio.OpenForTest(io);
        using var samples = new BlockingSamples();
        await using var monitor = StartMonitor(radio, samples, new TaitRadioHealthMonitorOptions
        {
            SampleInterval = TimeSpan.FromMilliseconds(50),
            SummaryWindowSize = 2,
        });

        await samples.NextAsync();
        await samples.NextAsync();
        await samples.NextAsync();

        monitor.Summarize().SampleCount.Should().Be(2);
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static async Task WaitForWriteAsync(FakeSerialIo io, string commandWithoutCr)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!io.WrittenAscii.Contains(commandWithoutCr + "\r", StringComparison.Ordinal))
        {
            DateTimeOffset.UtcNow.Should().BeBefore(deadline, $"the driver should have sent {commandWithoutCr}");
            await Task.Delay(10);
        }
    }

    /// <summary>FIFO of observed samples with awaitable arrival.</summary>
    private sealed class BlockingSamples : IDisposable
    {
        private readonly Queue<TaitRadioHealthSample> queue = new();
        private readonly SemaphoreSlim arrived = new(0);

        public void Dispose() => arrived.Dispose();

        public void Add(TaitRadioHealthSample sample)
        {
            lock (queue)
            {
                queue.Enqueue(sample);
            }
            arrived.Release();
        }

        public async Task<TaitRadioHealthSample> NextAsync()
        {
            (await arrived.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("a sample should arrive");
            lock (queue)
            {
                return queue.Dequeue();
            }
        }
    }
}
