namespace Packet.Rig.Flrig;

/// <summary>How <see cref="FlrigRig"/> finds and paces its flrig server.</summary>
public sealed record FlrigRigOptions
{
    /// <summary>flrig's stock XML-RPC port.</summary>
    public const int DefaultPort = 12345;

    /// <summary>Host running flrig. Default loopback — flrig's XML-RPC server has no
    /// authentication.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>flrig XML-RPC TCP port. Default <see cref="DefaultPort"/> (12345).</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>Per-request reply budget (covers connect + round-trip; XML-RPC here is one HTTP
    /// POST per command).</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Clock used for timeout scheduling. Tests inject <c>FakeTimeProvider</c>;
    /// production leaves the system clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
