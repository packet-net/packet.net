using Packet.Node.Core.Mqtt;

namespace Packet.Node.Tests.Mqtt;

/// <summary>
/// The stable per-machine token behind the salted MQTT client id (#582) — a 1:1 port of the Go
/// head-end's <c>machineSuffix</c> (<c>headend/config.go</c>), so these tests mirror
/// <c>headend/config_test.go</c> case for case and pin cross-language parity with literal hash
/// vectors (the same machine must derive the same suffix whichever daemon computes it).
/// </summary>
[Trait("Category", "Node")]
public sealed class MachineSuffixTests
{
    private const string EightHex = "^[0-9a-f]{8}$";

    private static Func<string, string?> FileMap(Dictionary<string, string> files)
        => path => files.TryGetValue(path, out var text) ? text : null;

    private static Func<string, string?> NoFile => _ => null;

    private static Func<IReadOnlyList<string>> NoMac => () => [];

    private static Action<string> WarnMustNotFire => _ => throw new InvalidOperationException(
        "warn should not fire when a machine identity source is available");

    [Fact]
    public void Suffix_is_deterministic_and_distinct_per_machine_id()
    {
        var readA = FileMap(new() { ["/etc/machine-id"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n" });
        var readB = FileMap(new() { ["/etc/machine-id"] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n" });

        var a1 = MachineSuffix.Compute(MachineSuffix.MachineIdFiles, readA, NoMac, WarnMustNotFire);
        var a2 = MachineSuffix.Compute(MachineSuffix.MachineIdFiles, readA, NoMac, WarnMustNotFire);
        var b1 = MachineSuffix.Compute(MachineSuffix.MachineIdFiles, readB, NoMac, WarnMustNotFire);

        a1.Should().MatchRegex(EightHex);
        a2.Should().Be(a1, "the suffix must be deterministic for one machine-id");
        b1.Should().NotBe(a1, "different machine-ids must not collide");
    }

    [Fact]
    public void Suffix_matches_the_go_head_end_hash_byte_for_byte()
    {
        // SHA-256("machine-id:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")[..4] — the exact value the Go
        // head-end's shortHash("machine-id:"+id) derives. Pinned so the two implementations can
        // never silently drift apart on the same host.
        var read = FileMap(new() { ["/etc/machine-id"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n" });
        MachineSuffix.Compute(MachineSuffix.MachineIdFiles, read, NoMac, WarnMustNotFire)
            .Should().Be("36573e8e");
    }

    [Fact]
    public void Dbus_machine_id_is_the_file_fallback()
    {
        // systemd file absent, D-Bus copy present → still derives (not the literal fallback).
        var read = FileMap(new() { ["/var/lib/dbus/machine-id"] = "cccccccccccccccccccccccccccccccc" });
        var got = MachineSuffix.Compute(MachineSuffix.MachineIdFiles, read, NoMac, WarnMustNotFire);

        got.Should().MatchRegex(EightHex);
    }

    [Fact]
    public void Empty_machine_id_file_falls_through_to_the_next_source()
    {
        // A present-but-blank systemd file must not hash to a shared "empty" suffix.
        var read = FileMap(new()
        {
            ["/etc/machine-id"] = "  \n",
            ["/var/lib/dbus/machine-id"] = "dddddddddddddddddddddddddddddddd",
        });
        var got = MachineSuffix.Compute(MachineSuffix.MachineIdFiles, read, NoMac, WarnMustNotFire);

        got.Should().Be(MachineSuffix.ShortHash("machine-id:dddddddddddddddddddddddddddddddd"));
    }

    [Fact]
    public void Mac_fallback_is_deterministic_distinct_and_domain_tagged()
    {
        Func<IReadOnlyList<string>> macs = () => ["", "de:ad:be:ef:00:01"];

        var m1 = MachineSuffix.Compute(MachineSuffix.MachineIdFiles, NoFile, macs, WarnMustNotFire);
        var m2 = MachineSuffix.Compute(MachineSuffix.MachineIdFiles, NoFile, macs, WarnMustNotFire);
        var other = MachineSuffix.Compute(
            MachineSuffix.MachineIdFiles, NoFile, () => ["de:ad:be:ef:00:02"], WarnMustNotFire);

        m1.Should().MatchRegex(EightHex);
        m1.Should().Be("22ccb426", "SHA-256(\"mac:de:ad:be:ef:00:01\")[..4] — the Go head-end's value");
        m2.Should().Be(m1, "the MAC-derived suffix must be deterministic");
        other.Should().NotBe(m1, "different MACs must not collide");
        // The domain tag means a machine-id whose text equals a MAC string still can't collide.
        MachineSuffix.ShortHash("machine-id:de:ad:be:ef:00:01").Should().NotBe(m1);
    }

    [Fact]
    public void Last_resort_is_the_fixed_literal_and_warns()
    {
        var warned = false;
        var got = MachineSuffix.Compute(MachineSuffix.MachineIdFiles, NoFile, NoMac, _ => warned = true);

        got.Should().Be(MachineSuffix.FallbackToken);
        warned.Should().BeTrue("reaching the non-unique literal is an operator-visible condition");
    }
}
