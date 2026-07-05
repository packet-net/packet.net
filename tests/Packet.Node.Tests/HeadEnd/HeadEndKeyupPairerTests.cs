using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Radios;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// The keyup-pairing routine (<see cref="HeadEndKeyupPairer"/>) — the operator-initiated RF action that
/// resolves the PHYSICAL modem↔radio map by keying each NinoTNC and watching which Tait's PTT fires.
/// Driven ENTIRELY with in-memory fakes: no serial port and no socket to real hardware is ever opened,
/// nothing transmits. A fake "keyer" fires the fake watches its NinoTNC is wired to; the routine reads
/// the map back.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndKeyupPairerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Tiny timings — the fakes fire synchronously, so no real waiting is needed.
    private static readonly HeadEndKeyupOptions Fast = new()
    {
        ObservationWindow = TimeSpan.FromMilliseconds(1),
        SettleBetween = TimeSpan.Zero,
    };

    // A Tait watcher fake: latches when its wired keyer "fires" it; cleared by Reset between keyups.
    private sealed class FakeWatch : IKeyupWatch
    {
        private int observed;
        public bool ObservedKeyup => Volatile.Read(ref observed) != 0;
        public void Reset() => Volatile.Write(ref observed, 0);
        public void Fire() => Volatile.Write(ref observed, 1);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A NinoTNC keyer fake: keying it fires the watches it is physically "cabled" to. A null wiring
    // list models a TNC whose radio is off / not cabled to any scanned Tait.
    private sealed class FakeModem : IKeyupModem
    {
        private readonly IReadOnlyList<FakeWatch> firesOnKey;
        public FakeModem(IReadOnlyList<FakeWatch> firesOnKey) => this.firesOnKey = firesOnKey;

        public Task KeyAsync(CancellationToken cancellationToken)
        {
            foreach (var w in firesOnKey)
            {
                w.Fire();
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopScanner : IHeadEndRadioScanner
    {
        public Task<HeadEndScan> ScanAsync(NodeConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HeadEndScan([], []));
    }

    private static KeyupTarget T(string deviceId) => new("127.0.0.1", 0, 0, deviceId);

    // Build a run over a fixed device wiring: which watches each modem fires, and any device ids whose
    // open should throw (to model open/keyup failures).
    private static Task<HeadEndKeyupResult> RunAsync(
        IReadOnlyList<string> tncIds,
        IReadOnlyList<string> radioIds,
        Dictionary<string, string[]> wiring,
        HashSet<string>? failOpen = null)
    {
        failOpen ??= [];
        var watches = radioIds.ToDictionary(id => id, _ => new FakeWatch(), StringComparer.Ordinal);

        OpenKeyupWatch openWatch = (target, ct) => failOpen.Contains(target.DeviceId)
            ? throw new InvalidOperationException($"watch open failed for {target.DeviceId}")
            : Task.FromResult<IKeyupWatch>(watches[target.DeviceId]);

        OpenKeyupModem openModem = (target, ct) => failOpen.Contains(target.DeviceId)
            ? throw new InvalidOperationException($"modem open failed for {target.DeviceId}")
            : Task.FromResult<IKeyupModem>(new FakeModem(
                (wiring.TryGetValue(target.DeviceId, out var fired) ? fired : [])
                    .Select(r => watches[r]).ToList()));

        var pairer = new HeadEndKeyupPairer(new NoopScanner());
        return pairer.RunKeyupAsync(
            "pi-shack", tncIds.Select(T).ToList(), radioIds.Select(T).ToList(), openModem, openWatch, Fast,
            CancellationToken.None);
    }

    [Fact]
    public async Task Keying_each_tnc_resolves_the_tait_it_is_cabled_to()
    {
        var result = await RunAsync(
            tncIds: ["ninoX", "ninoY"],
            radioIds: ["taitA", "taitB"],
            wiring: new Dictionary<string, string[]> { ["ninoX"] = ["taitA"], ["ninoY"] = ["taitB"] })
            .WaitAsync(Timeout);

        result.Reachable.Should().BeTrue();
        result.Pairs.Should().BeEquivalentTo(
        [
            new HeadEndKeyupPair("ninoX", "taitA"),
            new HeadEndKeyupPair("ninoY", "taitB"),
        ]);
        result.UnpairedTncs.Should().BeEmpty();
        result.UnpairedRadios.Should().BeEmpty();
        result.Ambiguous.Should().BeEmpty();
        result.Caveat.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_tnc_whose_keyup_fires_no_tait_is_unpaired()
    {
        var result = await RunAsync(
            tncIds: ["ninoX", "ninoY"],
            radioIds: ["taitA"],
            wiring: new Dictionary<string, string[]> { ["ninoX"] = ["taitA"], ["ninoY"] = [] })
            .WaitAsync(Timeout);

        result.Pairs.Should().ContainSingle().Which.Should().Be(new HeadEndKeyupPair("ninoX", "taitA"));
        result.UnpairedTncs.Should().Equal("ninoY");
        result.Ambiguous.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tnc_that_fires_more_than_one_tait_is_flagged_ambiguous_not_guessed()
    {
        var result = await RunAsync(
            tncIds: ["ninoX"],
            radioIds: ["taitA", "taitB"],
            wiring: new Dictionary<string, string[]> { ["ninoX"] = ["taitA", "taitB"] })
            .WaitAsync(Timeout);

        result.Pairs.Should().BeEmpty();
        var ambiguity = result.Ambiguous.Should().ContainSingle().Subject;
        ambiguity.TncDeviceId.Should().Be("ninoX");
        ambiguity.RadioDeviceIds.Should().BeEquivalentTo(["taitA", "taitB"]);
        // Both radios are accounted for by the ambiguity, so neither is reported as unpaired.
        result.UnpairedRadios.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tait_that_no_keyup_fires_is_reported_unpaired()
    {
        var result = await RunAsync(
            tncIds: ["ninoX"],
            radioIds: ["taitA", "taitB"],
            wiring: new Dictionary<string, string[]> { ["ninoX"] = ["taitA"] })
            .WaitAsync(Timeout);

        result.Pairs.Should().ContainSingle().Which.Should().Be(new HeadEndKeyupPair("ninoX", "taitA"));
        result.UnpairedRadios.Should().Equal("taitB");
    }

    [Fact]
    public async Task A_radio_that_re_fires_for_a_second_tnc_is_not_double_claimed()
    {
        // Physically impossible but modelled: taitA "fires" for both ninoX and ninoY. The first keyup
        // claims it; the second must not re-pair it — ninoY is left unpaired instead.
        var result = await RunAsync(
            tncIds: ["ninoX", "ninoY"],
            radioIds: ["taitA"],
            wiring: new Dictionary<string, string[]> { ["ninoX"] = ["taitA"], ["ninoY"] = ["taitA"] })
            .WaitAsync(Timeout);

        result.Pairs.Should().ContainSingle().Which.Should().Be(new HeadEndKeyupPair("ninoX", "taitA"));
        result.UnpairedTncs.Should().Equal("ninoY");
        result.UnpairedRadios.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tnc_that_fails_to_open_is_unpaired_and_the_others_still_pair()
    {
        var result = await RunAsync(
            tncIds: ["ninoBad", "ninoX"],
            radioIds: ["taitA"],
            wiring: new Dictionary<string, string[]> { ["ninoX"] = ["taitA"] },
            failOpen: new HashSet<string> { "ninoBad" })
            .WaitAsync(Timeout);

        result.Pairs.Should().ContainSingle().Which.Should().Be(new HeadEndKeyupPair("ninoX", "taitA"));
        result.UnpairedTncs.Should().Equal("ninoBad");
    }

    [Fact]
    public async Task A_tait_watcher_that_fails_to_open_is_excluded_and_left_unpaired()
    {
        var result = await RunAsync(
            tncIds: ["ninoX"],
            radioIds: ["taitBad", "taitA"],
            wiring: new Dictionary<string, string[]> { ["ninoX"] = ["taitA"] },
            failOpen: new HashSet<string> { "taitBad" })
            .WaitAsync(Timeout);

        result.Pairs.Should().ContainSingle().Which.Should().Be(new HeadEndKeyupPair("ninoX", "taitA"));
        result.UnpairedRadios.Should().Equal("taitBad");
    }

    // The full public path: scan → free devices → inventory (tcpPort) → keyup, all with fake opens so
    // nothing transmits. A canned scan lists one free NinoTNC + one free Tait; the stub head-end serves
    // their inventory; the fake keyer for nino0 fires tait0's watch.
    private sealed class OneFreePairScanner : IHeadEndRadioScanner
    {
        public Task<HeadEndScan> ScanAsync(NodeConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HeadEndScan(
            [
                new HeadEndInstanceScan(
                    "pi-shack", "127.0.0.1", 7300, "mdns", Reachable: true, Error: null,
                    Devices:
                    [
                        new HeadEndDeviceScan("nino0", HeadEndDeviceKind.NinoTnc, null, "3.41", null, 57600, Free: true),
                        new HeadEndDeviceScan("tait0", HeadEndDeviceKind.TaitCcdi, "Tait TM8110", "03.02", "1G000123", 28800, Free: true,
                            BandCode: "B1", AmateurBand: "2m"),
                    ],
                    ProposedPairs: [new HeadEndPairProposal("nino0", "tait0", Auto: true)],
                    PairingAmbiguous: false),
            ], Conflicts: []));
    }

    [Fact]
    public async Task PairByKeyupAsync_scans_reads_the_inventory_and_resolves_the_physical_pair()
    {
        var taitWatch = new FakeWatch();
        OpenKeyupWatch watchOverride = (target, ct) =>
        {
            target.DeviceId.Should().Be("tait0");
            return Task.FromResult<IKeyupWatch>(taitWatch);
        };
        OpenKeyupModem modemOverride = (target, ct) =>
        {
            target.DeviceId.Should().Be("nino0");
            return Task.FromResult<IKeyupModem>(new FakeModem([taitWatch]));
        };

        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports =
            [
                new HeadEndPortInfo { Id = "nino0", TcpPort = 7401, Baud = 57600, UsbVid = "04d8" },
                new HeadEndPortInfo { Id = "tait0", TcpPort = 7402, Baud = 28800, UsbVid = "10c4" },
            ],
        });

        var pairer = new HeadEndKeyupPairer(
            new OneFreePairScanner(),
            clientFactory: uri => new HeadEndClient(uri, new HttpClient(handler)),
            timeProvider: null,
            loggerFactory: null,
            options: Fast,
            modemOverride: modemOverride,
            watchOverride: watchOverride);

        var config = new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1" } };
        var result = await pairer.PairByKeyupAsync(config, "pi-shack").WaitAsync(Timeout);

        result.Reachable.Should().BeTrue();
        result.Pairs.Should().ContainSingle().Which.Should().Be(new HeadEndKeyupPair("nino0", "tait0"));
        result.Caveat.Should().Contain("RF");
    }

    [Fact]
    public async Task PairByKeyupAsync_reports_a_scanned_device_missing_from_the_inventory_as_unpaired()
    {
        // The scan lists nino0 + tait0 as free, but the inventory (fetched a beat later) only lists
        // tait0 — nino0 raced away. It must be reported unpaired, not silently dropped.
        var taitWatch = new FakeWatch();
        OpenKeyupWatch watchOverride = (target, ct) => Task.FromResult<IKeyupWatch>(taitWatch);
        OpenKeyupModem modemOverride = (target, ct) => Task.FromResult<IKeyupModem>(new FakeModem([]));

        var handler = new StubHeadEndHandler(new HeadEndInventory
        {
            InstanceId = "pi-shack",
            Ports = [new HeadEndPortInfo { Id = "tait0", TcpPort = 7402, Baud = 28800, UsbVid = "10c4" }],
        });

        var pairer = new HeadEndKeyupPairer(
            new OneFreePairScanner(),
            clientFactory: uri => new HeadEndClient(uri, new HttpClient(handler)),
            timeProvider: null, loggerFactory: null, options: Fast,
            modemOverride: modemOverride, watchOverride: watchOverride);

        var config = new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1" } };
        var result = await pairer.PairByKeyupAsync(config, "pi-shack").WaitAsync(Timeout);

        result.Reachable.Should().BeTrue();
        result.Pairs.Should().BeEmpty();
        result.UnpairedTncs.Should().Contain("nino0");
        result.UnpairedRadios.Should().Contain("tait0");
    }

    [Fact]
    public async Task PairByKeyupAsync_reports_an_unknown_instance_without_transmitting()
    {
        var pairer = new HeadEndKeyupPairer(new NoopScanner());
        var config = new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1" } };

        var result = await pairer.PairByKeyupAsync(config, "ghost").WaitAsync(Timeout);

        result.Reachable.Should().BeFalse();
        result.Error.Should().Contain("no such head-end");
        result.Pairs.Should().BeEmpty();
    }
}
