using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Api;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Api;

/// <summary>
/// The shared SSE pump (review item C067, #694). The node's six streaming endpoints were six
/// copies of the same loop with three different write-error policies: the spectrum feed caught
/// nothing on the write path and the rig feed caught only cancellation, so a client-gone
/// <see cref="IOException"/> escaped there while it was swallowed everywhere else. One helper,
/// one policy, one test.
/// </summary>
[Trait("Category", "Node")]
public sealed class SseWriterTests
{
    /// <summary>A response body that records what was written, safely for a poller to read
    /// while the pump is still running.</summary>
    private sealed class CapturingStream : Stream
    {
        private readonly List<byte> written = [];
        private readonly Lock gate = new();

        public string Text
        {
            get
            {
                lock (gate)
                {
                    return Encoding.UTF8.GetString(written.ToArray());
                }
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (gate)
            {
                written.AddRange(buffer.ToArray());
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }
    }

    /// <summary>A response body that fails the way Kestrel's does once the peer has gone.</summary>
    private sealed class BrokenPipeStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }

        public override void Flush() => throw new IOException("broken pipe");
        public override Task FlushAsync(CancellationToken ct) => throw new IOException("broken pipe");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("broken pipe");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => throw new IOException("broken pipe");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => throw new IOException("broken pipe");
    }

    private static (DefaultHttpContext Ctx, CapturingStream Body) Context()
    {
        var body = new CapturingStream();
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = body;
        return (ctx, body);
    }

    [Fact]
    public void Begin_sets_the_sse_envelope()
    {
        var (ctx, _) = Context();

        SseWriter.Begin(ctx);

        ctx.Response.Headers.ContentType.ToString().Should().Be("text/event-stream");
        ctx.Response.Headers.CacheControl.ToString().Should().Be("no-cache");
        ctx.Response.Headers["X-Accel-Buffering"].ToString().Should().Be("no");
    }

    [Fact]
    public async Task The_pump_writes_one_event_per_item_and_ends_when_the_source_completes()
    {
        var (ctx, body) = Context();
        var channel = Channel.CreateUnbounded<string>();
        channel.Writer.TryWrite("one");
        channel.Writer.TryWrite("two");
        channel.Writer.Complete();

        await SseWriter.PumpAsync(ctx, channel.Reader, new FakeTimeProvider(), "frame", s => $"\"{s}\"",
            CancellationToken.None);

        body.Text.Should().Be("event: frame\ndata: \"one\"\n\nevent: frame\ndata: \"two\"\n\n");
    }

    [Fact]
    public async Task A_client_that_vanishes_mid_write_is_a_normal_teardown_not_a_fault()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new BrokenPipeStream();
        var channel = Channel.CreateUnbounded<string>();
        channel.Writer.TryWrite("into the void");
        channel.Writer.Complete();

        // Before C067 this was the spectrum feed's behaviour: the IOException escaped the
        // handler. Every feed now swallows it identically.
        var act = async () => await SseWriter.PumpAsync(
            ctx, channel.Reader, new FakeTimeProvider(), "spectrum", s => s, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_cancelled_request_ends_the_pump_quietly()
    {
        var (ctx, _) = Context();
        var channel = Channel.CreateUnbounded<string>();
        using var cts = new CancellationTokenSource();

        var pump = SseWriter.PumpAsync(ctx, channel.Reader, new FakeTimeProvider(), "frame", s => s, cts.Token);
        await cts.CancelAsync();

        await pump;   // no OperationCanceledException escapes
    }

    [Fact]
    public async Task A_quiet_stream_gets_a_heartbeat_on_the_injected_clock()
    {
        var (ctx, body) = Context();
        var clock = new FakeTimeProvider();
        var channel = Channel.CreateUnbounded<string>();
        using var cts = new CancellationTokenSource();

        var pump = SseWriter.PumpAsync(ctx, channel.Reader, clock, "frame", s => s, cts.Token);
        clock.Advance(SseWriter.HeartbeatInterval);

        await Wait.ForAsync(
            () => body.Text.Contains(": ping", StringComparison.Ordinal),
            "the heartbeat fires on the injected clock, not the wall clock");

        await cts.CancelAsync();
        await pump;
    }
}
