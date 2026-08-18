namespace Packet.Interop.Tests.Hardware;

/// <summary>
/// xUnit collection marker shared by every hardware-loop test class. Tests
/// in the same collection do not run in parallel - that matters because
/// all of them grab the same two physical USB-attached NinoTNCs, and a
/// second test trying to open the same COM port while the first holds it
/// throws <see cref="UnauthorizedAccessException"/>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
#pragma warning disable CA1711 // xUnit requires the type name to *be* a "Collection" marker.
public sealed class HardwareLoopCollection
#pragma warning restore CA1711
{
    public const string Name = "HardwareLoop";
}
