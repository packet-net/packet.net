namespace Packet.Rig.Hamlib;

/// <summary>How <see cref="RigctldRig"/> finds and paces its rigctld.</summary>
public sealed record RigctldRigOptions
{
    /// <summary>rigctld's stock listen port.</summary>
    public const int DefaultPort = 4532;

    /// <summary>Host running rigctld. Default loopback — rigctld has no authentication, so
    /// exposing it beyond localhost is a station-owner decision, not a library default.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>rigctld TCP port. Default <see cref="DefaultPort"/> (4532).</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>TCP dial budget.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Per-command reply budget. Generous by default because rigctld itself is fronting a
    /// serial rig that can be slow to answer (hamlib's own rig-side timeout shows up as
    /// <c>RPRT -5</c> before this fires in the common case).
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Clock used for timeout scheduling. Tests inject <c>FakeTimeProvider</c>;
    /// production leaves the system clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
