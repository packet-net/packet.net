using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using M0LTE.Radio;
using Packet.Ax25.Transport;

namespace Packet.Ax25.Radio.Tests;

/// <summary>Scripted <see cref="IRadioControl"/>: RSSI comes from a settable value, and busy
/// state and carrier-sense edges are driven by the test. Awaiting the sampler's progress is
/// <see cref="SamplerClock"/>'s job, not this type's - see the note there.</summary>
internal sealed class FakeRadio : IRadioControl
{
    public RadioCapabilities Capabilities { get; init; } =
        RadioCapabilities.RssiRead | RadioCapabilities.CarrierSense | RadioCapabilities.TransmitterControl;

    public float RssiDbm { get; set; } = -128f;

    public bool? ChannelBusy { get; set; }

    public bool? Transmitting { get; private set; }

    public event EventHandler<CarrierSenseChange>? CarrierSenseChanged;

    public ValueTask<float> ReadRssiDbmAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(RssiDbm);

    public ValueTask SetTransmitterAsync(bool transmit, CancellationToken cancellationToken = default)
    {
        Transmitting = transmit;
        return ValueTask.CompletedTask;
    }

    public void RaiseCarrierSense(bool busy, DateTimeOffset at)
    {
        ChannelBusy = busy;
        CarrierSenseChanged?.Invoke(this, new CarrierSenseChange(busy, at));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
/// <summary>
/// A <see cref="FakeTimeProvider"/> that also tells the test WHEN the code under test is parked
/// on its next delay, so advancing the clock can never miss a tick.
/// </summary>
/// <remarks>
/// <para>The sampler loop is <c>read -&gt; record -&gt; await Task.Delay(period, clock)</c>. Tests
/// used to drive it by awaiting "the radio has been read" and then calling <c>Advance</c> - but
/// the read is observable BEFORE the loop re-arms its delay, so an <c>Advance</c> landing in
/// that gap moved a clock no timer was registered on. That tick was simply lost: the sampler
/// then slept forever on a due time the test had already passed, and the next wait ran out its
/// real-time guard. It failed only when the loop's continuation was slow to be scheduled, i.e.
/// on a loaded CI box, and it took out three different tests in this class on three consecutive
/// runs (plan §17, 2026-08-21).</para>
/// <para><c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c> arms its delay by calling
/// <see cref="CreateTimer"/>, so counting those calls counts the sampler parking. Waiting for
/// that signal before advancing makes the rendezvous an EVENT rather than a bet on scheduling
/// latency: <see cref="TickAsync"/> fires a timer that is provably registered, and returns once
/// the sampler has taken its next sample and parked again. Nothing here decides anything by the
/// wall clock - the guard in <see cref="WaitUntilParkedAsync"/> exists only to turn a genuine
/// hang into a failure instead of a hung test run, and does not fire in correct operation.</para>
/// <para>Only usable with code that arms ONE timer at a time on this clock;
/// <c>RssiTaggingTransport</c>'s single <c>Task.Delay</c> is exactly that.</para>
/// </remarks>
internal sealed class SamplerClock : TimeProvider
{
    private readonly FakeTimeProvider inner = new();
    // Unbounded so an arm is never dropped, and so this type owns nothing disposable
    // (tests construct it as a plain local, no `using` ceremony).
    private readonly Channel<bool> parked = Channel.CreateUnbounded<bool>();

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override long GetTimestamp() => inner.GetTimestamp();

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // Create FIRST: the timer must be registered on the inner clock before a test can be
        // told it is safe to advance. Releasing before this returns would reopen the very gap
        // this type exists to close.
        var timer = inner.CreateTimer(callback, state, dueTime, period);
        parked.Writer.TryWrite(true);
        return timer;
    }

    /// <summary>Await the code under test parking on its next delay. After construction that
    /// means "the first sample has been taken and recorded".</summary>
    public async Task WaitUntilParkedAsync()
    {
        // Generous, and never reached while the loop is alive: correctness comes from the
        // CreateTimer signal above, not from this number. It only turns a dead sampler into a
        // failed test instead of a hung run.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await parked.Reader.ReadAsync(guard.Token);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(
                "the sampler should arm its next delay - it appears to have stopped");
        }
    }

    /// <summary>Advance the clock past the delay the sampler is parked on, then await it having
    /// taken its next sample and parked again. Requires the sampler to be parked already, which
    /// is what the previous <see cref="WaitUntilParkedAsync"/> / <see cref="TickAsync"/>
    /// guarantees.</summary>
    public async Task TickAsync(TimeSpan by)
    {
        inner.Advance(by);
        await WaitUntilParkedAsync();
    }

    /// <summary>Move the clock WITHOUT expecting a sample - for nudging the instant a
    /// carrier-sense edge is stamped with, where no poll is due.</summary>
    public void AdvanceWithoutSampling(TimeSpan by) => inner.Advance(by);
}
/// <summary>Push-driven <see cref="IAx25Transport"/>: tests push inbound frames, and sends are
/// recorded with a completion the test can hold open.</summary>
internal sealed class FakeTransport : IAx25Transport
{
    private readonly Channel<Ax25InboundFrame> inbound = Channel.CreateUnbounded<Ax25InboundFrame>();

    public List<ReadOnlyMemory<byte>> Sent { get; } = [];

    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        lock (Sent)
        {
            Sent.Add(ax25);
        }
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
        inbound.Reader.ReadAllAsync(cancellationToken);

    public void Push(Ax25InboundFrame frame) => inbound.Writer.TryWrite(frame);

    public ValueTask DisposeAsync()
    {
        inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
