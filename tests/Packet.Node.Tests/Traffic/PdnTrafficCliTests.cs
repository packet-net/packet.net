using Packet.Ax25;
using Packet.Core;
using Packet.Node.Cli;
using Packet.Node.Core.Traffic;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Traffic;

/// <summary><c>pdn traffic export</c> and <c>pdn traffic replay</c> (SP-003, #178): the log
/// out as a capture, oldest first, and the capture back in.</summary>
[Trait("Category", "Node")]
public sealed class PdnTrafficCliTests : IDisposable
{
    private static readonly Callsign A = new("M0AAA", 1);
    private static readonly Callsign B = new("M0BBB", 2);
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 20, 0, 0, TimeSpan.Zero);

    private readonly string dir;

    public PdnTrafficCliTests()
    {
        dir = TestPaths.NewPath("pdn-trafficcli");
        Directory.CreateDirectory(dir);
    }

    private static TrafficRecord Record(int ms, string dir, Ax25Frame frame, string kind) =>
        new(T0 + TimeSpan.FromMilliseconds(ms), "vhf-1", dir, frame.Source.ToString(), frame.Destination.ToString(), kind,
            null, null, 1, frame.Control, null, 0, frame.ToBytes());

    [Fact]
    public async Task Export_writes_the_log_oldest_first_and_replay_reads_it_back()
    {
        var trafficDb = Path.Combine(dir, "traffic.db");
        var store = new SqliteTrafficStore(trafficDb);
        store.Append([
            Record(0, "tx", Ax25Frame.Sabm(B, A), "SABM"),
            Record(400, "rx", Ax25Frame.Ua(A, B, finalBit: true), "UA"),
        ]).Should().BeTrue();

        var capture = Path.Combine(dir, "capture.jsonl");
        var exit = await PdnTrafficCli.RunAsync(["traffic", "export", "--traffic-db", trafficDb, "--out", capture]);
        exit.Should().Be(0);

        var frames = TrafficCapture.Read(new StringReader(File.ReadAllText(capture))).ToList();
        frames.Should().HaveCount(2);
        frames[0].At.Should().Be(T0, "oldest first, whatever order the store answers in");
        frames[0].Transmitted.Should().BeTrue();
        frames[1].Transmitted.Should().BeFalse();

        (await PdnTrafficCli.RunAsync(["traffic", "replay", capture])).Should().Be(0);
        (await PdnTrafficCli.RunAsync(["traffic", "replay", Path.Combine(dir, "missing.jsonl")])).Should().Be(1);
    }

    [Fact]
    public async Task Bad_arguments_are_a_usage_error_and_a_missing_log_is_a_failure()
    {
        (await PdnTrafficCli.RunAsync(["traffic"])).Should().Be(2);
        (await PdnTrafficCli.RunAsync(["traffic", "nope"])).Should().Be(2);
        (await PdnTrafficCli.RunAsync(["traffic", "replay"])).Should().Be(2);
        (await PdnTrafficCli.RunAsync(["traffic", "export", "--traffic-db", Path.Combine(dir, "none.db")])).Should().Be(1);
        (await PdnTrafficCli.RunAsync(["traffic", "export", "--traffic-db", Path.Combine(dir, "none.db"), "--limit", "x"])).Should().Be(1);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
