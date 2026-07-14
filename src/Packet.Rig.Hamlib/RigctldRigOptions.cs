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

    /// <summary>
    /// The dBm value of S9 used by <see cref="RigctldRig.ReadSignalStrengthDbmAsync"/>: hamlib's
    /// <c>STRENGTH</c> level is calibrated dB relative to S9, so converting to dBm needs an S9
    /// reference. −73 dBm is the IARU Region 1 HF convention; VHF/UHF stations conventionally
    /// use −93 — set this accordingly.
    /// </summary>
    public double S9ReferenceDbm { get; init; } = -73.0;

    /// <summary>Clock used for timeout scheduling. Tests inject <c>FakeTimeProvider</c>;
    /// production leaves the system clock.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
