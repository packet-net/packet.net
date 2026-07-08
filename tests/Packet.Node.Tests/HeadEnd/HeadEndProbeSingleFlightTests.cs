using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Radios;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// Single-flight for the head-end probe actions (#581): a fleet scan and a keyup pairing serialise
/// on the shared <see cref="Packet.Node.Core.Radios.HeadEndRadioScanner"/>/<c>HeadEndKeyupPairer</c>
/// probe gate — a scan racing an in-flight pairing would re-clock the UART under the pairer's PTT
/// watcher (wrongly-unpaired radios) while its own probes queue in the head-end's accept backlog
/// (devices misclassified Unknown). Waiters wait rather than fail. Driven entirely with in-memory
/// fakes — nothing transmits, no socket opens.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndProbeSingleFlightTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed class GateProbe : IKeyupWatch, IKeyupModem
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ObservedKeyup => true;

        public void Reset()
        {
        }

        public async Task KeyAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // A discovery that both records when the scan actually started probing and can hold the scan
    // inside its critical section (the gate is taken BEFORE discovery).
    private sealed class BlockingDiscovery : IHeadEndDiscovery
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<DiscoveredHeadEnd>> DiscoverAsync(
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return [];
        }
    }

    private static NodeConfig EmptyConfig() => new() { Identity = new Identity { Callsign = "M0LTE-1" } };

    private static HeadEndRadioScanner ScannerOver(IHeadEndDiscovery discovery) => new(
        discovery,
        clientFactory: uri => new HeadEndClient(uri),
        discoveryTimeout: TimeSpan.FromMilliseconds(200),
        identifyTimeout: TimeSpan.FromMilliseconds(200),
        connectTimeout: TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task A_scan_waits_for_an_in_flight_keyup_pairing_instead_of_running_under_it()
    {
        var probe = new GateProbe();
        var pairer = new HeadEndKeyupPairer(new StubScanner());
        var target = new KeyupTarget("127.0.0.1", 0, 0, "ninoX");
        var radio = new KeyupTarget("127.0.0.1", 0, 0, "taitA");

        // The pairing takes the gate, opens its watcher, and blocks mid-keyup.
        var pairTask = pairer.RunKeyupAsync(
            "pi-shack", [target], [radio],
            openModem: (_, _) => Task.FromResult<IKeyupModem>(probe),
            openWatch: (_, _) => Task.FromResult<IKeyupWatch>(probe),
            new HeadEndKeyupOptions { ObservationWindow = TimeSpan.FromMilliseconds(1), SettleBetween = TimeSpan.Zero },
            CancellationToken.None);
        Task<HeadEndScan> scanTask;
        var discovery = new BlockingDiscovery();
        try
        {
            await probe.Entered.Task.WaitAsync(Timeout);

            // A concurrent scan must queue behind the pairing — its discovery step (inside the
            // gate) must not have started while the keyup is mid-flight.
            discovery.Release.TrySetResult();   // the scan itself is free-running once it enters
            scanTask = ScannerOver(discovery).ScanAsync(EmptyConfig());

            await Task.Delay(250);
            scanTask.IsCompleted.Should().BeFalse("the scan must wait for the in-flight pairing, not interleave with it");
            discovery.Entered.Task.IsCompleted.Should().BeFalse("the scan must not even begin probing under a live pairing");
        }
        finally
        {
            // Always unblock the pairing — a mid-test assertion failure must not leave the
            // process-wide probe gate held (it would cascade into unrelated 60 s timeouts).
            probe.Release.TrySetResult();
        }

        // Let the pairing finish: the queued scan then runs to completion.
        var pairResult = await pairTask.WaitAsync(Timeout);
        pairResult.Pairs.Should().ContainSingle();
        var scan = await scanTask.WaitAsync(Timeout);
        scan.Instances.Should().BeEmpty();
        discovery.Entered.Task.IsCompleted.Should().BeTrue("the queued scan ran once the pairing released the gate");
    }

    [Fact]
    public async Task A_keyup_pairing_waits_for_an_in_flight_scan()
    {
        var discovery = new BlockingDiscovery();
        var scanTask = ScannerOver(discovery).ScanAsync(EmptyConfig());

        var probe = new GateProbe();
        probe.Release.TrySetResult();   // the pairing is free-running once it enters
        var pairer = new HeadEndKeyupPairer(new StubScanner());
        Task<HeadEndKeyupResult> pairTask;
        try
        {
            await discovery.Entered.Task.WaitAsync(Timeout);   // the scan holds the gate

            pairTask = pairer.RunKeyupAsync(
                "pi-shack",
                [new KeyupTarget("127.0.0.1", 0, 0, "ninoX")],
                [new KeyupTarget("127.0.0.1", 0, 0, "taitA")],
                openModem: (_, _) => Task.FromResult<IKeyupModem>(probe),
                openWatch: (_, _) => Task.FromResult<IKeyupWatch>(probe),
                new HeadEndKeyupOptions { ObservationWindow = TimeSpan.FromMilliseconds(1), SettleBetween = TimeSpan.Zero },
                CancellationToken.None);

            await Task.Delay(250);
            pairTask.IsCompleted.Should().BeFalse("the pairing must wait for the in-flight scan");
            probe.Entered.Task.IsCompleted.Should().BeFalse("no keyup may fire while a scan is re-clocking lines");
        }
        finally
        {
            // Always unblock the scan — a mid-test assertion failure must not leave the
            // process-wide probe gate held (it would cascade into unrelated 60 s timeouts).
            discovery.Release.TrySetResult();
        }

        (await scanTask.WaitAsync(Timeout)).Instances.Should().BeEmpty();
        (await pairTask.WaitAsync(Timeout)).Pairs.Should().ContainSingle();
    }

    private sealed class StubScanner : IHeadEndRadioScanner
    {
        public Task<HeadEndScan> ScanAsync(NodeConfig config, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HeadEndScan([], []));
    }
}
