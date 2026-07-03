using System.Collections.Concurrent;
using Packet.Radio;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// Drives <see cref="SdmTuningLink"/> over a fake <see cref="IRadioSideChannel"/>:
/// receipt-gated retries with backoff, DCD-busy send deferral, prompt
/// event-triggered buffer reads, sequence-number dedupe, and the side
/// channel's payload budget.
/// </summary>
public class SdmTuningLinkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static SdmTuningLinkOptions FastOptions => new()
    {
        MaxAttempts = 3,
        RetryBackoff = TimeSpan.FromMilliseconds(20),
        ReceiptTimeout = TimeSpan.FromMilliseconds(300),
        ChannelClearTimeout = TimeSpan.FromMilliseconds(500),
        ChannelClearPollInterval = TimeSpan.FromMilliseconds(10),
        ReceivePollInterval = TimeSpan.FromMilliseconds(100),
        PostReceiveGuard = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task A_positively_receipted_send_completes_first_try()
    {
        var channel = new FakeSdmChannel { AckPlan = [true] };
        await using var link = new SdmTuningLink(channel, "PDN00001", FastOptions);

        await link.SendAsync(new TuningTelegram(1, TuningVerb.Hello, "meter")).WaitAsync(Timeout);

        channel.Sent.Should().HaveCount(1);
        channel.Sent[0].Destination.Should().Be("PDN00001");
        channel.Sent[0].Message.Should().Be("V1|1|HI|meter");
    }

    [Fact]
    public async Task A_failed_receipt_retries_then_succeeds()
    {
        var channel = new FakeSdmChannel { AckPlan = [false, true] };
        var log = new List<string>();
        await using var link = new SdmTuningLink(channel, "PDN00001", FastOptions) { Log = log.Add };

        await link.SendAsync(new TuningTelegram(2, TuningVerb.BurstRequest, "5")).WaitAsync(Timeout);

        channel.Sent.Should().HaveCount(2, "the first attempt got a negative receipt");
        log.Should().Contain(l => l.Contains("NOT acknowledged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retries_exhausted_throws_TuningLinkException()
    {
        var channel = new FakeSdmChannel { AckPlan = [false, false, false] };
        await using var link = new SdmTuningLink(channel, "PDN00001", FastOptions);

        var act = async () => await link.SendAsync(new TuningTelegram(3, TuningVerb.Hello, "tuned")).WaitAsync(Timeout);

        await act.Should().ThrowAsync<TuningLinkException>().WithMessage("*3 attempts*");
        channel.Sent.Should().HaveCount(3);
    }

    [Fact]
    public async Task Sending_waits_for_the_channel_to_clear_and_gives_up_if_it_never_does()
    {
        var channel = new FakeSdmChannel { AckPlan = [true], Busy = true };
        await using var link = new SdmTuningLink(channel, "PDN00001", FastOptions);

        var act = async () => await link.SendAsync(new TuningTelegram(4, TuningVerb.Hello, "meter")).WaitAsync(Timeout);

        await act.Should().ThrowAsync<TuningLinkException>().WithMessage("*busy*");
        channel.Sent.Should().BeEmpty("the link must never transmit over a busy channel");
    }

    [Fact]
    public async Task MS_telegrams_are_sent_in_the_compact_form()
    {
        var channel = new FakeSdmChannel { AckPlan = [true] };
        await using var link = new SdmTuningLink(channel, "PDN00001", FastOptions);
        var report = new MeterReport(10, 10, 480, 0, -90.4);

        await link.SendAsync(new TuningTelegram(9, TuningVerb.Measurement, report.ToArgs())).WaitAsync(Timeout);

        channel.Sent[0].Message.Should().Be("V1|9|MS|10/10|f480|c0|r-90.4");
        channel.Sent[0].Message.Length.Should().BeLessThanOrEqualTo(32);
    }

    [Fact]
    public async Task An_arrival_event_triggers_a_prompt_buffer_read_and_a_telegram()
    {
        var channel = new FakeSdmChannel();
        await using var link = new SdmTuningLink(channel, "PDN00002", FastOptions);

        channel.Deliver("V1|7|RQ|5");

        var received = new List<TuningTelegram>();
        await foreach (var telegram in link.ReceiveAsync().WithTimeout(Timeout))
        {
            received.Add(telegram);
            break;
        }
        received.Should().ContainSingle().Which.Should().Be(new TuningTelegram(7, TuningVerb.BurstRequest, "5"));
    }

    [Fact]
    public async Task Duplicate_sequence_numbers_are_deduped()
    {
        var channel = new FakeSdmChannel();
        await using var link = new SdmTuningLink(channel, "PDN00002", FastOptions);

        channel.Deliver("V1|7|RQ|5");   // original
        await Task.Delay(150);
        channel.Deliver("V1|7|RQ|5");   // transport retry (receipt was lost)
        await Task.Delay(150);
        channel.Deliver("V1|8|AD|OK");  // next real telegram

        var received = new List<TuningTelegram>();
        await foreach (var telegram in link.ReceiveAsync().WithTimeout(Timeout))
        {
            received.Add(telegram);
            if (received.Count == 2)
            {
                break;
            }
        }

        received.Should().Equal(
            new TuningTelegram(7, TuningVerb.BurstRequest, "5"),
            new TuningTelegram(8, TuningVerb.Advice, "OK"));
    }

    [Fact]
    public async Task A_send_right_after_an_arrival_waits_out_the_post_receive_guard()
    {
        // The radio auto-acks a received SDM over the air; transmitting while
        // that ack is in flight wedges the TM8110's ack engine — so the link
        // must hold sends back for the guard interval after every arrival.
        var channel = new FakeSdmChannel { AckPlan = [true] };
        var options = FastOptions with { PostReceiveGuard = TimeSpan.FromMilliseconds(400) };
        await using var link = new SdmTuningLink(channel, "PDN00001", options);

        channel.Deliver("V1|5|RQ|3");
        await foreach (var _ in link.ReceiveAsync().WithTimeout(Timeout))
        {
            break; // arrival processed
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await link.SendAsync(new TuningTelegram(1, TuningVerb.Hello, "tuned")).WaitAsync(Timeout);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(200),
            "the send must wait for the radio's auto-ack of the just-received telegram");
        channel.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task Non_telegram_sdm_traffic_is_ignored()
    {
        var channel = new FakeSdmChannel();
        await using var link = new SdmTuningLink(channel, "PDN00002", FastOptions);

        channel.Deliver("WEATHER AT THE CLUBHOUSE: FINE");
        await Task.Delay(150);
        channel.Deliver("V1|1|HI|tuned");

        var received = new List<TuningTelegram>();
        await foreach (var telegram in link.ReceiveAsync().WithTimeout(Timeout))
        {
            received.Add(telegram);
            break;
        }
        received.Should().ContainSingle().Which.Verb.Should().Be(TuningVerb.Hello);
    }

    [Fact]
    public async Task A_telegram_over_the_side_channels_payload_budget_is_refused()
    {
        var channel = new FakeSdmChannel { PayloadBudget = 16 };
        await using var link = new SdmTuningLink(channel, "PDN00001", FastOptions);

        var act = async () => await link
            .SendAsync(new TuningTelegram(1, TuningVerb.Hello, "a-role-name-far-too-long-for-a-tiny-budget"))
            .WaitAsync(Timeout);

        await act.Should().ThrowAsync<TuningLinkException>().WithMessage("*16-character budget*");
        channel.Sent.Should().BeEmpty();
    }

    /// <summary>Scripted <see cref="IRadioSideChannel"/>: records sends, answers
    /// receipts from a plan, and delivers inbound messages through the
    /// one-deep buffer + arrival event like the real radio.</summary>
    private sealed class FakeSdmChannel : IRadioSideChannel
    {
        private readonly ConcurrentQueue<bool> ackPlan = new();
        private string? buffered;

        public IReadOnlyList<bool> AckPlan
        {
            get => [.. ackPlan];
            init
            {
                foreach (bool ack in value)
                {
                    ackPlan.Enqueue(ack);
                }
            }
        }

        public bool Busy { get; set; }

        public int PayloadBudget { get; init; } = 32;

        public List<(string Destination, string Message)> Sent { get; } = [];

        public int MaxPayloadLength => PayloadBudget;

        public bool? ChannelBusy => Busy;

        public event EventHandler? DatagramArrived;

        public event EventHandler<bool>? DeliveryReceipt;

        public Task SendAsync(string destinationId, string payload, CancellationToken cancellationToken = default)
        {
            lock (Sent)
            {
                Sent.Add((destinationId, payload));
            }
            if (ackPlan.TryDequeue(out bool ack))
            {
                // Receipt arrives asynchronously, like the radio's PROGRESS 1D.
                _ = Task.Run(
                    async () =>
                    {
                        await Task.Delay(10, CancellationToken.None);
                        DeliveryReceipt?.Invoke(this, ack);
                    },
                    CancellationToken.None);
            }
            return Task.CompletedTask;
        }

        public Task<string?> ReadBufferedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Exchange(ref buffered, null));

        public void Deliver(string message)
        {
            Interlocked.Exchange(ref buffered, message);
            DatagramArrived?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Timeout wrapper for async-enumerable assertions.</summary>
internal static class AsyncEnumerableTestExtensions
{
    public static async IAsyncEnumerable<T> WithTimeout<T>(
        this IAsyncEnumerable<T> source, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var item in source.WithCancellation(cts.Token))
        {
            yield return item;
        }
    }
}
