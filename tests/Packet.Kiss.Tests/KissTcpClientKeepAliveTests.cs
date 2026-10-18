using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using Packet.Kiss;

namespace Packet.Kiss.Tests;

/// <summary>
/// #935: a kiss-tcp link to a quiet channel used to be dropped and redialled every
/// 5 minutes, because silence tripped the read-idle timeout that #464 added for
/// half-open links. Dead-link detection is now tuned TCP keepalive on the real
/// socket, and the read-idle budget is only a fallback for a platform that won't
/// take the keepalive settings.
/// </summary>
/// <remarks>
/// Real loopback sockets, so the socket options are the ones production gets. The
/// read-idle timer runs on the injected <see cref="FakeTimeProvider"/>, so "an hour
/// of silence" is advanced rather than slept through. A genuinely unanswered
/// keepalive can't be produced on loopback (the peer kernel always answers), so the
/// dead-link side is covered by checking the armed settings, a reset peer, and the
/// fallback rule (whose mechanics <see cref="KissTcpClientReadIdleTests"/> covers).
/// </remarks>
public sealed class KissTcpClientKeepAliveTests
{
    [Fact]
    public async Task A_quiet_healthy_link_stays_up_well_past_the_old_idle_timeout()
    {
        var time = new FakeTimeProvider();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();
        await using var client = await KissTcpClient.ConnectAsync(
            "127.0.0.1", port, KissTcpClient.DefaultReadIdleTimeout, time);
        using var peer = await acceptTask;

        client.KeepAliveArmed.Should().BeTrue("Linux, Windows and macOS all take the keepalive settings");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var got = new List<KissFrame>();
        var pump = Task.Run(async () =>
        {
            await foreach (var f in client.ReadFramesAsync(cts.Token))
            {
                got.Add(f);
                break;
            }
        });

        // An hour of channel silence, in fake time, with real gaps so any idle timer
        // the read had armed would get the chance to fire.
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(20);
            time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        }

        pump.IsCompleted.Should().BeFalse("silence on the channel is not a dead link");

        // And the link still works: the next frame from the peer is delivered.
        var wire = KissEncoder.Encode(0, KissCommand.Data, new byte[] { 0x42 });
        await peer.GetStream().WriteAsync(wire);
        await pump.WaitAsync(TimeSpan.FromSeconds(10));
        got.Should().ContainSingle().Which.Payload.Should().Equal(new byte[] { 0x42 });
    }

    [Fact]
    public async Task Keepalive_is_armed_so_a_dead_peer_is_found_in_about_80_seconds()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();
        await using var client = await KissTcpClient.ConnectAsync("127.0.0.1", port);
        using var peer = await acceptTask;

        var socket = client.Socket!;
        ((int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!).Should().NotBe(0);
        ((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!).Should().Be(30);
        ((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!).Should().Be(10);
        ((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!).Should().Be(5);
        KissTcpClient.KeepAliveDetectionTime.Should().Be(TimeSpan.FromSeconds(80));

        if (OperatingSystem.IsLinux())
        {
            // TCP_USER_TIMEOUT: a frame sent into a half-open link is given up on in
            // 90 s rather than the ~15 minutes plain retransmission takes.
            var value = new byte[4];
            socket.GetRawSocketOption(6, 18, value);
            BitConverter.ToInt32(value).Should().Be(KissTcpClient.UserTimeoutMilliseconds);
        }
    }

    [Fact]
    public async Task A_peer_that_resets_the_connection_ends_the_stream_promptly()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = listener.AcceptTcpClientAsync();
        await using var client = await KissTcpClient.ConnectAsync("127.0.0.1", port);
        var peer = await acceptTask;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pump = Task.Run(async () =>
        {
            await foreach (var _ in client.ReadFramesAsync(cts.Token)) { }
        });

        // A rebooted peer answers our next segment (or keepalive probe) with a RST;
        // an abortive close sends one straight away.
        peer.Client.LingerState = new LingerOption(true, 0);
        peer.Close();

        await pump.WaitAsync(TimeSpan.FromSeconds(10));
        cts.IsCancellationRequested.Should().BeFalse("the reset, not the test timeout, ended the stream");
    }

    [Fact]
    public void The_read_idle_budget_applies_only_when_keepalive_could_not_be_armed()
    {
        var requested = TimeSpan.FromMinutes(5);
        KissTcpClient.EffectiveReadIdleTimeout(keepAliveArmed: true, requested).Should().Be(Timeout.InfiniteTimeSpan);
        KissTcpClient.EffectiveReadIdleTimeout(keepAliveArmed: false, requested).Should().Be(requested);
    }
}
