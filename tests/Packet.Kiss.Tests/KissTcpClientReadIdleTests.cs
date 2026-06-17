using System.IO.Pipelines;
using Microsoft.Extensions.Time.Testing;
using Packet.Kiss;

namespace Packet.Kiss.Tests;

/// <summary>
/// The #464 fix at its root: a half-open TCP link — the peer rebooted, the
/// cable was pulled, net-sim was restarted — sends no FIN, so a plain
/// <c>ReadAsync</c> blocks forever and the port silently dies until someone
/// restarts the service. <see cref="KissTcpClient"/>'s read-idle timeout
/// converts that hang into an end-of-stream (the same signal a graceful close
/// produces), which is what lets <c>ReconnectingKissModem</c> re-dial.
/// </summary>
/// <remarks>
/// Driven over a loopback duplex stream via the internal stream-injecting ctor,
/// with a <see cref="FakeTimeProvider"/> so the idle window is advanced
/// deterministically rather than slept through.
/// </remarks>
public sealed class KissTcpClientReadIdleTests
{
    private static KissFrame Data(params byte[] payload) => new(0, KissCommand.Data, payload);

    [Fact]
    public async Task A_half_open_link_ends_the_stream_after_the_idle_timeout()
    {
        var time = new FakeTimeProvider();
        var idle = TimeSpan.FromMinutes(5);

        // peerToClient is never written to and never closed → a real half-open
        // socket: ReadAsync would block forever. The idle timeout must fire.
        var peerToClient = new Pipe();
        var clientToPeer = new Pipe();
        var clientStream = new DuplexStream(peerToClient.Reader.AsStream(), clientToPeer.Writer.AsStream());
        await using var client = new KissTcpClient(clientStream, readIdleTimeout: idle, timeProvider: time);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var got = new List<KissFrame>();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in client.ReadFramesAsync(cts.Token))
            {
                got.Add(f);
            }
        });

        // Let the pump reach the read (so its idle timer is registered on the
        // fake clock) before advancing past the idle window. Advancing must trip
        // the liveness timeout → IOException → the stream ends cleanly. Poll-and-
        // advance defends against the read not yet having armed its timer.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!pump.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            time.Advance(idle + TimeSpan.FromSeconds(1));
        }

        await pump.WaitAsync(TimeSpan.FromSeconds(10));
        got.Should().BeEmpty("the link was idle and never delivered a frame");
    }

    [Fact]
    public async Task Inbound_data_resets_the_idle_window_so_a_busy_link_is_not_dropped()
    {
        var time = new FakeTimeProvider();
        var idle = TimeSpan.FromMinutes(5);

        var peerToClient = new Pipe();
        var clientToPeer = new Pipe();
        var clientStream = new DuplexStream(peerToClient.Reader.AsStream(), clientToPeer.Writer.AsStream());
        var peer = new DuplexStream(clientToPeer.Reader.AsStream(), peerToClient.Writer.AsStream());
        await using var client = new KissTcpClient(clientStream, readIdleTimeout: idle, timeProvider: time);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var got = new List<KissFrame>();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in client.ReadFramesAsync(cts.Token))
            {
                got.Add(f);
                if (got.Count == 2) break;
            }
        });

        // First frame arrives well inside the window.
        time.Advance(idle - TimeSpan.FromSeconds(30));
        await WriteFrameAsync(peer, Data(0x01));
        await WaitForCountAsync(() => got.Count, 1);

        // A second frame arrives after another sub-window: because the first read
        // completed, the idle clock restarted — a busy link must NOT be dropped.
        time.Advance(idle - TimeSpan.FromSeconds(30));
        await WriteFrameAsync(peer, Data(0x02));
        await WaitForCountAsync(() => got.Count, 2);

        await pump.WaitAsync(TimeSpan.FromSeconds(10));
        got.Should().HaveCount(2);
        got[0].Payload.Should().Equal(new byte[] { 0x01 });
        got[1].Payload.Should().Equal(new byte[] { 0x02 });
    }

    [Fact]
    public async Task Idle_detection_disabled_blocks_forever_like_the_pre_fix_behaviour()
    {
        var time = new FakeTimeProvider();

        var peerToClient = new Pipe();
        var clientToPeer = new Pipe();
        var clientStream = new DuplexStream(peerToClient.Reader.AsStream(), clientToPeer.Writer.AsStream());
        // InfiniteTimeSpan (the default for the stream ctor) disables idle checks.
        await using var client = new KissTcpClient(clientStream, readIdleTimeout: Timeout.InfiniteTimeSpan, timeProvider: time);

        using var cts = new CancellationTokenSource();
        var ended = false;
        var pump = Task.Run(async () =>
        {
            await foreach (var _ in client.ReadFramesAsync(cts.Token)) { }
            ended = true;
        });

        // Advancing the clock arbitrarily far must NOT end the stream — there is
        // no liveness timer when idle detection is off.
        time.Advance(TimeSpan.FromHours(24));
        await Task.Delay(100);
        ended.Should().BeFalse("with idle detection disabled the read blocks until cancellation/close");

        cts.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task WriteFrameAsync(Stream peer, KissFrame frame)
    {
        var wire = KissEncoder.Encode(frame.Port, frame.Command, frame.Payload);
        await peer.WriteAsync(wire);
        await peer.FlushAsync();
    }

    // Spin briefly until the pump has surfaced the expected number of frames.
    private static async Task WaitForCountAsync(Func<int> count, int target)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (count() < target)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"expected {target} frames; saw {count()}");
            await Task.Delay(10);
        }
    }
}
