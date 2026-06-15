namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// The outcome of attempting to run a short-lived external probe (e.g. <c>dpkg-query</c>,
/// <c>apt-cache</c>). Distinguishes the three cases channel detection cares about:
/// the executable wasn't found (a non-Debian / dpkg-less / Windows host — the load-bearing
/// safe-fallback signal), the process ran and exited, or some other launch fault occurred.
/// </summary>
/// <param name="Executed"><c>true</c> if the process actually started and exited;
/// <c>false</c> if the executable could not be launched (missing-executable launch error —
/// caught and reported, never thrown).</param>
/// <param name="ExitCode">The process exit code when <see cref="Executed"/>; undefined otherwise.</param>
/// <param name="StandardOutput">Captured stdout when <see cref="Executed"/>; empty otherwise.</param>
public readonly record struct ProcessRunResult(bool Executed, int ExitCode, string StandardOutput)
{
    /// <summary>The executable could not be launched (not on PATH / not present). The
    /// documented safe-fallback signal for channel detection — never an exception.</summary>
    public static ProcessRunResult NotLaunched { get; } = new(Executed: false, ExitCode: -1, StandardOutput: "");

    /// <summary>The process ran to completion.</summary>
    public static ProcessRunResult Ran(int exitCode, string standardOutput) =>
        new(Executed: true, ExitCode: exitCode, StandardOutput: standardOutput ?? "");

    /// <summary>Convenience: the process ran <em>and</em> exited <c>0</c>.</summary>
    public bool Succeeded => Executed && ExitCode == 0;
}

/// <summary>
/// A minimal seam over launching a short-lived external command and capturing its result.
/// The only purpose is to make runtime install-channel detection (which shells out to
/// <c>dpkg-query</c> / <c>apt-cache</c>) unit-testable <em>without</em> shelling out for real
/// — a test supplies a fake runner that returns canned <see cref="ProcessRunResult"/>s for
/// each probe, simulating an owned/not-owned binary, a present/absent repo, or a missing
/// <c>dpkg-query</c> / <c>apt-cache</c>.
/// </summary>
/// <remarks>
/// Implementations MUST be total: a missing executable (Win32 launch error / "No such file
/// or directory") is reported as <see cref="ProcessRunResult.NotLaunched"/>, never thrown,
/// so a caller on a non-Debian host never sees an exception.
/// </remarks>
public interface IProcessRunner
{
    /// <summary>Run <paramref name="fileName"/> with <paramref name="arguments"/>, capture
    /// stdout, and return how it went. Never throws for a missing executable — returns
    /// <see cref="ProcessRunResult.NotLaunched"/> instead.</summary>
    ProcessRunResult Run(string fileName, IReadOnlyList<string> arguments);
}
