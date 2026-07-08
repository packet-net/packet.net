using Microsoft.Extensions.Time.Testing;
using Packet.Radio;

namespace Packet.Radio.Tait.Tests;

/// <summary>
/// The #576 fault-path behaviour of <see cref="TaitCcdiRadio"/>: a faulting link clears the
/// cached carrier-sense (a radio that died busy must not latch the CSMA gate into deferring
/// every keyup its full MaxWait), and a busy state latched implausibly long is re-validated with
/// a solicited probe — reset to unknown when the radio does not answer (the lost-DCD-clear
/// bench observation), kept when it does.
/// </summary>
public class TaitCcdiRadioFaultTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const string BusyProgress = ".p0205C9\r.";       // PROGRESS: receiver busy (DCD up)
    private const string RssiQueryWire = "q0450645C";        // CCTM 064 — the watchdog/revalidation probe
    private const string RssiReply = ".j07064-456C9\r.";

    [Fact]
    public async Task A_fault_clears_channel_busy_and_raises_a_final_carrier_clear_edge()
    {
        var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io, new TaitCcdiRadioOptions
        {
            KeepAliveInterval = null,
            StaleBusyRevalidateAfter = null,
        });

        var edges = new List<bool>();
        var carrierSeen = new SemaphoreSlim(0);
        var faulted = new SemaphoreSlim(0);
        radio.CarrierSenseChanged += (_, e) =>
        {
            lock (edges)
            {
                edges.Add(e.Busy);
            }
            carrierSeen.Release();
        };
        radio.ConnectionStateChanged += (_, s) =>
        {
            if (s == TaitConnectionState.Faulted)
            {
                faulted.Release();
            }
        };

        io.Enqueue(BusyProgress);
        (await carrierSeen.WaitAsync(Timeout)).Should().BeTrue("the DCD edge must arrive first");
        radio.ChannelBusy.Should().BeTrue();

        // The head-end socket dies while DCD is up — the pump faults the radio.
        io.FailReads(new IOException("head-end socket died"));
        (await faulted.WaitAsync(Timeout)).Should().BeTrue("the pump must fault the connection state");

        radio.ChannelBusy.Should().BeNull(
            "a faulted link's last DCD report is no longer evidence — unknown fails the CSMA gate open, " +
            "instead of a latched busy deferring every keyup its full MaxWait");
        (await carrierSeen.WaitAsync(Timeout)).Should().BeTrue("the fault raises a final carrier-clear edge");
        lock (edges)
        {
            edges.Should().Equal(new[] { true, false },
                "event-driven consumers must see the channel go quiet when the fault clears the state");
        }
        radio.ConnectionState.Should().Be(TaitConnectionState.Faulted);
    }

    [Fact]
    public async Task A_stale_latched_busy_is_reset_to_unknown_when_the_radio_does_not_answer_the_probe()
    {
        var clock = new FakeTimeProvider();
        var io = new FakeSerialIo();
        await using var radio = TaitCcdiRadio.OpenForTest(io, new TaitCcdiRadioOptions
        {
            // Keep the quiet-link fault path out of this test: only the staleness re-validation
            // should be acting (the two share the watchdog loop).
            KeepAliveInterval = TimeSpan.FromHours(1),
            StaleBusyRevalidateAfter = TimeSpan.FromSeconds(30),
            TransactionTimeout = TimeSpan.FromSeconds(2),
        }, clock);

        var edges = new List<bool>();
        radio.CarrierSenseChanged += (_, e) =>
        {
            lock (edges)
            {
                edges.Add(e.Busy);
            }
        };

        io.Enqueue(BusyProgress);
        await WaitRealAsync(() => radio.ChannelBusy == true, "the DCD-up edge lands");

        // Walk fake time forward: the watchdog re-validates once busy has been latched 30 s
        // (a lost DCD-clear PROGRESS leaves exactly this state on a healthy-looking link), and
        // the unanswered probe times out 2 s later — busy resets to unknown.
        for (int i = 0; i < 120 && radio.ChannelBusy is not null; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(20);
        }

        radio.ChannelBusy.Should().BeNull("an unanswered re-validation probe resets the stuck busy to unknown (fail-open)");
        io.WrittenAscii.Should().Contain(RssiQueryWire, "the re-validation is the watchdog's solicited RSSI probe");
        lock (edges)
        {
            edges.Should().Equal(new[] { true, false }, "the reset raises a final carrier-clear edge");
        }
        radio.ConnectionState.Should().Be(TaitConnectionState.Healthy,
            "one unanswered re-validation probe clears the stale busy without declaring the link faulted");
    }

    [Fact]
    public async Task A_long_busy_on_a_responsive_radio_survives_the_revalidation_probe()
    {
        var clock = new FakeTimeProvider();
        var io = new FakeSerialIo();
        io.RespondTo(RssiQueryWire, RssiReply);   // the radio answers the probe — it is alive
        await using var radio = TaitCcdiRadio.OpenForTest(io, new TaitCcdiRadioOptions
        {
            KeepAliveInterval = TimeSpan.FromHours(1),
            StaleBusyRevalidateAfter = TimeSpan.FromSeconds(30),
            TransactionTimeout = TimeSpan.FromSeconds(2),
        }, clock);

        var edges = new List<bool>();
        radio.CarrierSenseChanged += (_, e) =>
        {
            lock (edges)
            {
                edges.Add(e.Busy);
            }
        };

        io.Enqueue(BusyProgress);
        await WaitRealAsync(() => radio.ChannelBusy == true, "the DCD-up edge lands");

        // Well past the staleness threshold — the probe must have been issued and answered.
        for (int i = 0; i < 24; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(20);
        }
        await WaitRealAsync(() => io.WrittenAscii.Contains(RssiQueryWire, StringComparison.Ordinal),
            "the re-validation probe was issued");

        radio.ChannelBusy.Should().BeTrue(
            "a responsive radio's long busy is genuine (a long carrier) — re-validation must not clear it");
        lock (edges)
        {
            edges.Should().Equal(new[] { true }, "no synthetic clear edge is raised while the radio answers");
        }
    }

    private static async Task WaitRealAsync(Func<bool> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"condition not met within {Timeout.TotalSeconds:0}s: {because}");
            }
            await Task.Delay(10);
        }
    }
}
