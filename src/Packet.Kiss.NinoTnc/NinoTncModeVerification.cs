namespace Packet.Kiss.NinoTnc;

/// <summary>
/// How <see cref="NinoTncSerialPort.SetModeAsync"/> proves a SETHW mode change
/// actually took (#633).
/// </summary>
/// <remarks>
/// <para>
/// KISS SETHW is fire-and-forget: the firmware never acknowledges it, and it
/// <em>does</em> silently fail to apply — bench-observed twice on firmware 3.44
/// with DIP 1111, where a SETHW to mode 11 left the TNC running the previously
/// selected mode 8. Nothing errors; the TNC keys up happily in the old mode and
/// everything downstream simply scores zero in both directions, which reads like
/// "this mode is broken" or "the RF is broken" rather than "the mode change was
/// ignored". That misreading cost real debugging time on both occasions.
/// </para>
/// <para>
/// GETALL already reports the running mode (<see cref="NinoTncStatusFrame.RunningMode"/>,
/// resolved from <c>BrdSwchMod</c>'s low byte through <see cref="NinoTncCatalog"/>),
/// so the readback is free — the driver simply never used it. Verification is
/// therefore on by default (<see cref="Default"/>); <see cref="None"/> is the
/// opt-out for callers that genuinely want the old fire-and-forget send.
/// </para>
/// </remarks>
public sealed record NinoTncModeVerification
{
    /// <summary>
    /// Verify the mode took: settle, GETALL, compare, retry — and throw
    /// <see cref="NinoTncModeNotAppliedException"/> if it never takes. The
    /// default for <see cref="NinoTncSerialPort.SetModeAsync"/>, because the
    /// failure this guards against is invisible at every level except the
    /// traffic.
    /// </summary>
    public static readonly NinoTncModeVerification Default = new();

    /// <summary>
    /// Send the SETHW and return without reading back — the pre-#633
    /// fire-and-forget behaviour. Reach for this only when the caller knows it
    /// doesn't care whether the mode took (or is going to verify by itself);
    /// remember that an ignored SETHW looks exactly like broken RF downstream.
    /// </summary>
    public static readonly NinoTncModeVerification None = new() { Enabled = false };

    /// <summary>Whether to read the mode back at all. Default <c>true</c>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How long to leave the modem alone after a SETHW before reading the mode
    /// back. Default 1.5 s — what the pdn-soundmodem bench rig settled on
    /// empirically. The modem needs a moment to reconfigure after a mode change,
    /// and the frames immediately following one are unreliable regardless of
    /// TXDELAY, so this is a settle for the TNC's benefit as much as the
    /// readback's. <see cref="TimeSpan.Zero"/> reads back immediately.
    /// </summary>
    public TimeSpan SettleTime { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How many times to send the SETHW and check the readback before giving
    /// up. Default 3 — the bench evidence is that a re-sent SETHW takes
    /// (the mis-set observed in #633 came good on a retry), so one retry is the
    /// difference between a working link and a dead one; the third is slack.
    /// Values below 1 are treated as 1.
    /// </summary>
    public int Attempts { get; init; } = 3;

    /// <summary>
    /// Per-attempt timeout for the GETALL readback. <c>null</c> (default) uses
    /// <see cref="NinoTncSerialPort.GetAllAsync"/>'s own 5 s default. A GETALL
    /// that times out is treated as a failed attempt, not a hard error — the
    /// next attempt re-sends the SETHW.
    /// </summary>
    public TimeSpan? ReadBackTimeout { get; init; }
}
