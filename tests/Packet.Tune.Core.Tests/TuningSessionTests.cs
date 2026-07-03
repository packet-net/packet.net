using System.Threading.Channels;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// The assistant loop end-to-end over a fake in-memory link pair: HI
/// handshake, RQ-triggered bursts, MS + AD flowing back, operator-prompted
/// re-rounds, and a clean BY finish — no hardware, no sockets.
/// </summary>
public class TuningSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>No pre-burst guard delay — there is no radio to protect in a fake link.</summary>
    private static TuningSessionOptions NoDelay => new() { PreBurstDelay = TimeSpan.Zero };

    [Fact]
    public async Task A_two_round_session_runs_the_full_protocol_and_finishes_with_bye()
    {
        var (tunedLink, meterLink) = InMemoryTuningLink.CreatePair();
        var stimulus = new FakeStimulus();
        var meter = new FakeMeter(
            new MeterReport(5, 5, 0, 0, -90.2),
            new MeterReport(5, 5, 4, 0, -90.5));
        var prompt = new ScriptedPrompt(true, false); // one more round, then finish
        var tunedOut = new StringWriter();
        var meterOut = new StringWriter();

        var tunedRun = TuningSession.RunTunedAsync(tunedLink, stimulus, prompt, NoDelay, tunedOut);
        var meterRun = TuningSession.RunMeterAsync(meterLink, meter, null, meterOut);

        (await tunedRun.WaitAsync(Timeout)).Should().Be(0);
        (await meterRun.WaitAsync(Timeout)).Should().Be(0);

        stimulus.Bursts.Should().Equal(5, 5); // two default-size bursts requested by the meter
        meter.Measured.Should().Be(2);
        prompt.Asked.Should().Be(2);

        string tunedText = tunedOut.ToString();
        tunedText.Should().Contain("5/5", "the trend table shows the decode counts");
        tunedText.Should().Contain("OK", "steady full-decode bursts advise OK");
        tunedText.Should().Contain("-90.2");
        meterOut.ToString().Should().Contain("tuned end said BY");
    }

    [Fact]
    public async Task The_meter_reports_advice_computed_from_the_measurement()
    {
        var (tunedLink, meterLink) = InMemoryTuningLink.CreatePair();
        var stimulus = new FakeStimulus();
        var meter = new FakeMeter(new MeterReport(1, 5, 0, 0, -90.0)); // mostly missed → UP
        var prompt = new ScriptedPrompt(false);
        var tunedOut = new StringWriter();

        var tunedRun = TuningSession.RunTunedAsync(tunedLink, stimulus, prompt, NoDelay, tunedOut);
        var meterRun = TuningSession.RunMeterAsync(meterLink, meter, null, new StringWriter());

        (await tunedRun.WaitAsync(Timeout)).Should().Be(0);
        (await meterRun.WaitAsync(Timeout)).Should().Be(0);
        tunedOut.ToString().Should().Contain("UP");
    }

    [Fact]
    public async Task Custom_burst_size_flows_through_the_rq_telegram()
    {
        var (tunedLink, meterLink) = InMemoryTuningLink.CreatePair();
        var stimulus = new FakeStimulus();
        var meter = new FakeMeter(new MeterReport(3, 3, 0, 0, null));
        var prompt = new ScriptedPrompt(false);

        var tunedRun = TuningSession.RunTunedAsync(tunedLink, stimulus, prompt, NoDelay, new StringWriter());
        var meterRun = TuningSession.RunMeterAsync(
            meterLink, meter, new TuningSessionOptions { BurstFrames = 3 }, new StringWriter());

        (await tunedRun.WaitAsync(Timeout)).Should().Be(0);
        (await meterRun.WaitAsync(Timeout)).Should().Be(0);
        stimulus.Bursts.Should().Equal(3);
    }

    private sealed class FakeStimulus : IBurstStimulus
    {
        public List<int> Bursts { get; } = [];

        public Task<int> FireBurstAsync(int frames, CancellationToken cancellationToken = default)
        {
            lock (Bursts)
            {
                Bursts.Add(frames);
            }
            return Task.FromResult(frames);
        }
    }

    private sealed class FakeMeter(params MeterReport[] reports) : IBurstMeter
    {
        private int index;

        public int Measured => index;

        public Task<MeterReport> MeasureBurstAsync(int requestedFrames, CancellationToken cancellationToken = default)
        {
            var report = reports[Math.Min(index, reports.Length - 1)];
            index++;
            return Task.FromResult(report);
        }
    }

    private sealed class ScriptedPrompt(params bool[] answers) : ITuningPrompt
    {
        private int index;

        public int Asked => index;

        public Task<bool> ContinueAsync(CancellationToken cancellationToken = default)
        {
            bool answer = index < answers.Length && answers[index];
            index++;
            return Task.FromResult(answer);
        }
    }
}

/// <summary>An in-memory <see cref="ITuningLink"/> pair (two crossed channels)
/// for protocol tests.</summary>
internal sealed class InMemoryTuningLink : ITuningLink
{
    private readonly Channel<TuningTelegram> outbound;
    private readonly Channel<TuningTelegram> inboundChannel;

    private InMemoryTuningLink(Channel<TuningTelegram> outbound, Channel<TuningTelegram> inbound)
    {
        this.outbound = outbound;
        inboundChannel = inbound;
    }

    public static (InMemoryTuningLink A, InMemoryTuningLink B) CreatePair()
    {
        var aToB = Channel.CreateUnbounded<TuningTelegram>();
        var bToA = Channel.CreateUnbounded<TuningTelegram>();
        return (new InMemoryTuningLink(aToB, bToA), new InMemoryTuningLink(bToA, aToB));
    }

    public async Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default)
    {
        await outbound.Writer.WriteAsync(telegram, cancellationToken);
    }

    public IAsyncEnumerable<TuningTelegram> ReceiveAsync(CancellationToken cancellationToken = default) =>
        inboundChannel.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        outbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
