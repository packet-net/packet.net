using System.Threading.Channels;
using Packet.Tune.Core;

namespace Packet.Node.Tests.Tuning;

/// <summary>An in-memory <see cref="ITuningLink"/> pair (two crossed channels) for driving a node
/// tuning session against a scripted peer — no radios, no SDMs.</summary>
internal sealed class InMemoryTuningLink : ITuningLink
{
    private readonly Channel<TuningTelegram> outbound;
    private readonly Channel<TuningTelegram> inbound;

    private InMemoryTuningLink(Channel<TuningTelegram> outbound, Channel<TuningTelegram> inbound)
    {
        this.outbound = outbound;
        this.inbound = inbound;
    }

    public static (InMemoryTuningLink A, InMemoryTuningLink B) CreatePair()
    {
        var aToB = Channel.CreateUnbounded<TuningTelegram>();
        var bToA = Channel.CreateUnbounded<TuningTelegram>();
        return (new InMemoryTuningLink(aToB, bToA), new InMemoryTuningLink(bToA, aToB));
    }

    public async Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default) =>
        await outbound.Writer.WriteAsync(telegram, cancellationToken).ConfigureAwait(false);

    public IAsyncEnumerable<TuningTelegram> ReceiveAsync(CancellationToken cancellationToken = default) =>
        inbound.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        outbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>An <see cref="ITuningLink"/> that accepts sends and yields no inbound telegrams, then
/// completes — the tuned/meter loop drops straight out as "link closed without a goodbye" (return 1
/// → an <c>error</c> session), for testing the error exit path.</summary>
internal sealed class DeadTuningLink : ITuningLink
{
    public Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async IAsyncEnumerable<TuningTelegram> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Records requested burst sizes; reports every frame as keyed.</summary>
internal sealed class FakeStimulus : IBurstStimulus
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

/// <summary>Returns a scripted sequence of <see cref="MeterReport"/>s (clamping at the last).</summary>
internal sealed class FakeMeter(params MeterReport[] reports) : IBurstMeter
{
    private int index;

    public int Measured => index;

    public double? IdleAudioLevelDb { get; init; }

    public Task<MeterReport> MeasureBurstAsync(int requestedFrames, CancellationToken cancellationToken = default)
    {
        var report = reports[Math.Min(index, reports.Length - 1)];
        index++;
        return Task.FromResult(report);
    }
}

/// <summary>Answers the tuned-side operator prompt from a scripted list, then finishes.</summary>
internal sealed class ScriptedPrompt(params bool[] answers) : ITuningPrompt
{
    private int index;

    public Task<bool> ContinueAsync(CancellationToken cancellationToken = default)
    {
        bool answer = index < answers.Length && answers[index];
        index++;
        return Task.FromResult(answer);
    }
}
