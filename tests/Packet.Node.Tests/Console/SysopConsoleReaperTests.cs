using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Console;

/// <summary>
/// The idle reaper on <see cref="SysopConsoleManager"/> (review item C062, #694): a browser that
/// closes, crashes, or loses the network never sends <c>DELETE /api/v1/console/{id}</c>, so an
/// abandoned node command console used to keep its command service running until host shutdown.
/// Driven by <see cref="FakeTimeProvider"/> - no sleeping, no wall clock.
/// </summary>
[Trait("Category", "Node")]
public sealed class SysopConsoleReaperTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task An_abandoned_console_is_closed_once_the_idle_timeout_passes()
    {
        var clock = new FakeTimeProvider();
        await using var manager = new SysopConsoleManager(logger: null, clock, Idle);
        var connection = new DriveableConnection("console", NodeTransportKind.Telnet);

        manager.Open("console:abandoned", connection, reapWhenIdle: true);
        manager.IsManaged("console:abandoned").Should().BeTrue();

        // Just short of the timeout: still there.
        clock.Advance(Idle - TimeSpan.FromSeconds(1));
        manager.IsManaged("console:abandoned").Should().BeTrue("the idle timeout has not elapsed yet");

        // Past it: the manager's own timer sweeps and closes the session.
        clock.Advance(TimeSpan.FromSeconds(2));
        manager.IsManaged("console:abandoned").Should().BeFalse("an abandoned console is reaped");

        // Reaping tears the connection down, so the command service on the other end sees EOF.
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_watched_console_is_never_reaped_however_quiet_it_is()
    {
        var clock = new FakeTimeProvider();
        await using var manager = new SysopConsoleManager(logger: null, clock, Idle);
        manager.Open("console:watched", new DriveableConnection("console", NodeTransportKind.Telnet), reapWhenIdle: true);

        // A browser is streaming it (an open SSE subscription) but nobody is typing.
        using var sub = manager.Subscribe("console:watched", out _, out _);
        sub.Should().NotBeNull();

        clock.Advance(Idle * 4);

        manager.IsManaged("console:watched").Should().BeTrue("an attached subscriber is activity in itself");
    }

    [Fact]
    public async Task Typed_input_restarts_the_countdown()
    {
        var clock = new FakeTimeProvider();
        await using var manager = new SysopConsoleManager(logger: null, clock, Idle);
        manager.Open("console:typing", new DriveableConnection("console", NodeTransportKind.Telnet), reapWhenIdle: true);

        clock.Advance(Idle - TimeSpan.FromMinutes(1));
        await manager.WriteAsync("console:typing", "ports\r"u8.ToArray());

        // Without the input stamp this would now be well past the timeout.
        clock.Advance(Idle - TimeSpan.FromMinutes(1));
        manager.IsManaged("console:typing").Should().BeTrue("input inside the window restarts the countdown");

        clock.Advance(Idle);
        manager.IsManaged("console:typing").Should().BeFalse("and it is reaped once the new window elapses");
    }

    [Fact]
    public async Task The_countdown_starts_when_the_last_subscriber_leaves()
    {
        var clock = new FakeTimeProvider();
        await using var manager = new SysopConsoleManager(logger: null, clock, Idle);
        manager.Open("console:closed-tab", new DriveableConnection("console", NodeTransportKind.Telnet), reapWhenIdle: true);

        var sub = manager.Subscribe("console:closed-tab", out _, out _);
        clock.Advance(Idle * 2);          // watched all this time
        sub!.Dispose();                   // the browser tab went away

        clock.Advance(Idle - TimeSpan.FromSeconds(1));
        manager.IsManaged("console:closed-tab").Should().BeTrue("the countdown restarts when the watcher leaves");

        clock.Advance(TimeSpan.FromSeconds(2));
        manager.IsManaged("console:closed-tab").Should().BeFalse();
    }

    [Fact]
    public async Task An_adopted_connect_out_is_never_reaped()
    {
        var clock = new FakeTimeProvider();
        await using var manager = new SysopConsoleManager(logger: null, clock, Idle);

        // A sysop connect-out (the "{portId}:{peer}" id) is a real RF link the operator owns:
        // it opts OUT of reaping and only ends on DELETE or when the peer goes away.
        manager.Open("vhf:GB7RDG-1", new DriveableConnection("GB7RDG-1", NodeTransportKind.Ax25));

        clock.Advance(Idle * 10);

        manager.IsManaged("vhf:GB7RDG-1").Should().BeTrue();
    }

    [Fact]
    public async Task A_zero_timeout_disables_the_reaper_entirely()
    {
        var clock = new FakeTimeProvider();
        await using var manager = new SysopConsoleManager(logger: null, clock, TimeSpan.Zero);
        manager.Open("console:forever", new DriveableConnection("console", NodeTransportKind.Telnet), reapWhenIdle: true);

        clock.Advance(TimeSpan.FromDays(1));
        manager.ReapIdle();   // even an explicit sweep is a no-op

        manager.IsManaged("console:forever").Should().BeTrue("idleTimeoutMinutes: 0 keeps the pre-#694 behaviour");
    }
}
