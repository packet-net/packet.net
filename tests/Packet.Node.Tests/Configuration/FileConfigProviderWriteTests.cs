using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// Unit tests for the <see cref="IWritableConfigProvider"/> write seam on
/// <see cref="FileConfigProvider"/> (the path a <c>PUT /config</c> persists
/// through). Driven over a temp file with <c>watch: false</c> so the only reloads
/// are the ones the test calls — the deterministic pattern the existing
/// <see cref="FileConfigProviderTests"/> use. Covers a clean apply (Current
/// advances, the file is rewritten, OnChange fires once), an atomic reject (no
/// mutation, no event), Validate-without-side-effects, and the watcher
/// echo-suppression after a self-write.
/// </summary>
public class FileConfigProviderWriteTests : IDisposable
{
    private readonly string dir;
    private readonly string path;

    public FileConfigProviderWriteTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-cfgwrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "node.yaml");
    }

    private const string ValidYaml = """
        schemaVersion: 1
        identity:
          callsign: M0LTE-1
        ports: []
        """;

    private FileConfigProvider Open()
    {
        File.WriteAllText(path, ValidYaml);
        return new FileConfigProvider(path, new FakeTimeProvider(), watch: false);
    }

    [Fact]
    public void TryApply_a_valid_candidate_advances_Current_persists_and_raises_OnChange_once()
    {
        using var provider = Open();
        int onChangeCount = 0;
        NodeConfig? observed = null;
        using var _ = provider.OnChange(c => { onChangeCount++; observed = c; });

        var candidate = provider.Current with
        {
            Identity = provider.Current.Identity with { Grid = "IO91wm" },
        };

        var applied = provider.TryApply(candidate, out var errors);

        applied.Should().BeTrue();
        errors.Should().BeEmpty();
        provider.Current.Identity.Grid.Should().Be("IO91wm");
        File.ReadAllText(path).Should().Contain("IO91wm");        // persisted to disk
        onChangeCount.Should().Be(1);
        observed.Should().NotBeNull();
        observed!.Identity.Grid.Should().Be("IO91wm");
    }

    [Fact]
    public void TryApply_an_invalid_candidate_is_rejected_atomically_no_event()
    {
        using var provider = Open();
        var before = provider.Current;
        int onChangeCount = 0;
        using var _ = provider.OnChange(_ => onChangeCount++);

        // Blank callsign fails validation (Callsign.TryParse).
        var invalid = provider.Current with
        {
            Identity = provider.Current.Identity with { Callsign = "" },
        };

        var applied = provider.TryApply(invalid, out var errors);

        applied.Should().BeFalse();
        errors.Should().NotBeEmpty();
        provider.Current.Should().BeSameAs(before);   // rollback by construction
        onChangeCount.Should().Be(0);
    }

    [Fact]
    public void Validate_returns_the_errors_without_mutating_Current()
    {
        using var provider = Open();
        var before = provider.Current;

        var invalid = provider.Current with
        {
            Identity = provider.Current.Identity with { Callsign = "" },
        };

        var errors = provider.Validate(invalid);

        errors.Should().NotBeEmpty();
        provider.Current.Should().BeSameAs(before);   // pure dry-run, no swap
    }

    [Fact]
    public void Reload_after_a_self_write_is_an_echo_and_does_not_fire_a_second_OnChange()
    {
        using var provider = Open();
        int onChangeCount = 0;
        using var _ = provider.OnChange(_ => onChangeCount++);

        var candidate = provider.Current with
        {
            Identity = provider.Current.Identity with { Grid = "IO91wm" },
        };
        provider.TryApply(candidate, out var applyErrors).Should().BeTrue();
        applyErrors.Should().BeEmpty();
        onChangeCount.Should().Be(1);

        // The watcher (in production) would fire on our own write; re-reading the
        // exact bytes we just wrote must be a no-op — no double-swap, no echo event.
        var reloaded = provider.Reload();

        reloaded.Should().BeFalse();
        onChangeCount.Should().Be(1);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
