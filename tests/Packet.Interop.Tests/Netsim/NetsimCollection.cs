namespace Packet.Interop.Tests.Netsim;

/// <summary>
/// xUnit collection marker shared by every test class that talks to the
/// net-sim container. Tests in the same collection do not run in parallel
/// — that matters because every netsim test attaches to KISS-TCP port
/// 8100 ("node a" — the shared "ours" endpoint), and a second test trying
/// to open the same listener while the first holds it would either get
/// blocked or interleave frames from two unrelated scenarios.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
#pragma warning disable CA1711 // xUnit requires the type name to *be* a "Collection" marker.
public sealed class NetsimCollection
#pragma warning restore CA1711
{
    public const string Name = "Netsim";
}
