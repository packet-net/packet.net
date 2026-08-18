using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Packet.Node.Api;

namespace Packet.Node.Tests.Api;

/// <summary>
/// Proves the runtime log-level override actually takes effect <b>live</b> - the whole point of
/// the capability. The hard requirement is that a logger created at startup (before any override
/// exists) sees its <see cref="ILogger.IsEnabled"/> change the instant an override is set, with no
/// restart and no re-creation of the logger. Wired exactly as <c>Program.cs</c> wires it:
/// <see cref="DynamicLogLevelOverrides"/> as both an <see cref="IConfigureOptions{TOptions}"/> and
/// an <see cref="IOptionsChangeTokenSource{TOptions}"/> of <see cref="LoggerFilterOptions"/>.
/// </summary>
[Trait("Category", "Node")]
public sealed class DynamicLogLevelOverridesTests
{
    private static (ILoggerFactory factory, DynamicLogLevelOverrides dyn, ServiceProvider sp) BuildPipeline()
    {
        var dyn = new DynamicLogLevelOverrides();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            // A logging provider must be present for IsEnabled to be meaningful - a factory with
            // zero providers reports IsEnabled=false unconditionally (no sink to enable). The real
            // host always has the console provider; this fake stands in for it.
            b.AddProvider(new CollectingProvider());
        });
        // Mirror Program.cs: the same instance is the configurator AND the change-token source.
        services.AddSingleton<IConfigureOptions<LoggerFilterOptions>>(dyn);
        services.AddSingleton<IOptionsChangeTokenSource<LoggerFilterOptions>>(dyn);
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<ILoggerFactory>(), dyn, sp);
    }

    // A minimal always-on provider so the MEL filter rules (which our overrides drive) are the
    // only thing gating IsEnabled.
    private sealed class CollectingProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new AlwaysOn();

        public void Dispose() { }

        private sealed class AlwaysOn : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        }
    }

    [Fact]
    public void Setting_an_override_raises_IsEnabled_for_an_already_created_logger_live()
    {
        var (factory, dyn, sp) = BuildPipeline();
        using (sp)
        {
            // Logger created BEFORE any override - the already-created-logger case.
            var log = factory.CreateLogger("Packet.Ax25.Session");
            log.IsEnabled(LogLevel.Debug).Should().BeFalse("default min level is Information");

            // (a) Setting an override raises IsEnabled for that category live.
            dyn.Set("Packet.Ax25", LogLevel.Debug);
            log.IsEnabled(LogLevel.Debug).Should().BeTrue(
                "the override must take effect on the existing logger with no restart");

            // (b) Clearing restores the prior (configured) behaviour.
            dyn.Clear("Packet.Ax25");
            log.IsEnabled(LogLevel.Debug).Should().BeFalse("clearing the override restores the default floor");
        }
    }

    [Fact]
    public void Longest_prefix_wins_and_unrelated_categories_are_untouched()
    {
        var (factory, dyn, sp) = BuildPipeline();
        using (sp)
        {
            var session = factory.CreateLogger("Packet.Ax25.Session");
            var other = factory.CreateLogger("Packet.NetRom.L3");

            // A broad rule and a more specific rule: the longest matching prefix must win.
            dyn.Set("Packet", LogLevel.Warning);
            dyn.Set("Packet.Ax25", LogLevel.Trace);

            session.IsEnabled(LogLevel.Trace).Should().BeTrue("Packet.Ax25 (longer prefix) wins over Packet");
            other.IsEnabled(LogLevel.Information).Should().BeFalse("Packet (Warning) applies — no longer prefix matches");
            other.IsEnabled(LogLevel.Warning).Should().BeTrue();

            // A category matching no override keeps the configured default floor.
            var unrelated = factory.CreateLogger("Something.Else.Entirely");
            dyn.Clear("Packet");
            unrelated.IsEnabled(LogLevel.Information).Should().BeTrue();
            unrelated.IsEnabled(LogLevel.Debug).Should().BeFalse();
        }
    }

    [Fact]
    public void Snapshot_reports_active_overrides_sorted()
    {
        var dyn = new DynamicLogLevelOverrides();
        dyn.Snapshot().Should().BeEmpty("default state = no overrides");

        dyn.Set("Packet.NetRom", LogLevel.Debug);
        dyn.Set("Packet.Ax25", LogLevel.Trace);

        var snap = dyn.Snapshot();
        snap.Should().HaveCount(2);
        snap[0].Key.Should().Be("Packet.Ax25", "snapshot is ordinal-ordered by category");
        snap[0].Value.Should().Be(LogLevel.Trace);
        snap[1].Key.Should().Be("Packet.NetRom");

        dyn.Clear("Packet.Ax25");
        dyn.Snapshot().Should().ContainSingle().Which.Key.Should().Be("Packet.NetRom");
    }
}
