namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// Resolves the node's <see cref="InstallChannel"/> at runtime (the revised "Channel
/// detection" in <c>docs/node-self-update-design.md</c>): the build stamp encodes only
/// <c>deb</c> vs <c>selfcontained</c>, and the <em>apt-vs-github</em> split is decided live,
/// because the same <c>.deb</c> can be installed from an apt repo (<c>apt</c>) or
/// <c>dpkg -i</c>'d from a GitHub Release (<c>github</c>) and dpkg records no install method.
/// </summary>
/// <remarks>
/// <para>Resolution order — every external probe guarded so a non-Debian / dpkg-less /
/// Windows host never throws and never shells out to dpkg unless it has already proven
/// itself a <c>.deb</c> install:</para>
/// <list type="number">
/// <item><b>stamp <c>selfcontained</c></b> → <see cref="InstallChannel.SelfContained"/>
/// immediately, with <em>zero</em> dpkg/apt probes.</item>
/// <item><b>stamp <c>deb</c> (or absent)</b> → probe dpkg ownership of the running binary:
/// <c>dpkg-query -S &lt;realpath of /proc/self/exe&gt;</c>. <c>dpkg-query</c> absent (the
/// runner reports not-launched) <em>or</em> the binary not owned by <c>packetnet</c> →
/// <see cref="InstallChannel.SelfContained"/>.</item>
/// <item><b>dpkg owns it</b> → probe apt's actual upgrade source: <c>apt-cache policy
/// packetnet</c>. A real repo origin in the version table (an http(s) source, not just
/// <c>/var/lib/dpkg/status</c>) → <see cref="InstallChannel.Apt"/>; only the installed dpkg
/// status with no repo, or <c>apt-cache</c> absent → <see cref="InstallChannel.Github"/>
/// (the conservative fall — worst case we self-update from Releases, never the reverse).</item>
/// </list>
/// <para>The <c>PDN_INSTALL_CHANNEL</c> env override short-circuits everything (dev/test),
/// and resolution is eager + cached in the ctor (resolve-once-at-boot — the channel doesn't
/// change at runtime).</para>
/// </remarks>
public sealed class RuntimeInstallChannelProvider : IInstallChannelProvider
{
    /// <summary>The default marker path the package ships.</summary>
    public const string DefaultMarkerPath = "/usr/lib/packetnet/install-channel";

    /// <summary>Environment variable that overrides all detection (dev/test).</summary>
    public const string EnvOverride = "PDN_INSTALL_CHANNEL";

    /// <summary>The dpkg package name that must own the running binary for it to count as a
    /// packaged install (anything else → self-contained).</summary>
    public const string PackageName = "packetnet";

    /// <inheritdoc/>
    public InstallChannel Channel { get; }

    /// <summary>The production provider: reads the shipped marker, shells out via
    /// <see cref="SystemProcessRunner"/>, and resolves the running binary from
    /// <c>/proc/self/exe</c>.</summary>
    public RuntimeInstallChannelProvider()
        : this(DefaultMarkerPath, SystemProcessRunner.Instance, runningBinaryPath: null)
    {
    }

    /// <summary>Test/explicit ctor. <paramref name="runner"/> simulates the dpkg-query /
    /// apt-cache outcomes; <paramref name="runningBinaryPath"/> stands in for the resolved
    /// <c>/proc/self/exe</c>.</summary>
    /// <param name="markerPath">The build-stamp marker (<c>deb</c> / <c>selfcontained</c>).</param>
    /// <param name="runner">The command seam used for the dpkg-query / apt-cache probes.</param>
    /// <param name="runningBinaryPath">The path the dpkg-ownership probe queries; when null,
    /// resolved from <c>/proc/self/exe</c> (falling back to <see cref="Environment.ProcessPath"/>).</param>
    public RuntimeInstallChannelProvider(string markerPath, IProcessRunner runner, string? runningBinaryPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        ArgumentNullException.ThrowIfNull(runner);
        Channel = Resolve(markerPath, runner, runningBinaryPath);
    }

    private static InstallChannel Resolve(string markerPath, IProcessRunner runner, string? runningBinaryPath)
    {
        // 0. The override wins outright (dev/test), parsed against the full channel set.
        var fromEnv = Environment.GetEnvironmentVariable(EnvOverride);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return ParseOverride(fromEnv);
        }

        // 1. Stamp `selfcontained` → decided on the stamp alone. NO dpkg/apt probes.
        var stamp = ReadStamp(markerPath);
        if (stamp == BuildStamp.SelfContained)
        {
            return InstallChannel.SelfContained;
        }

        // 2. Stamp `deb` (or absent) → does dpkg own the *running binary*?
        var binary = runningBinaryPath ?? ResolveRunningBinary();
        if (binary is null || !DpkgOwnsBinary(runner, binary))
        {
            // dpkg-query absent, or not owned → nothing manages us → self-contained.
            return InstallChannel.SelfContained;
        }

        // 3. dpkg owns it → can apt upgrade it from a real repo?
        return AptHasRepoOrigin(runner) ? InstallChannel.Apt : InstallChannel.Github;
    }

    /// <summary>`dpkg-query -S &lt;binary&gt;` — owned by <see cref="PackageName"/>?
    /// Runner not-launched (dpkg-query absent) or a non-zero exit (not owned) → false.</summary>
    private static bool DpkgOwnsBinary(IProcessRunner runner, string binary)
    {
        var result = runner.Run("dpkg-query", new[] { "-S", binary });
        if (!result.Executed || result.ExitCode != 0)
        {
            // Not-launched (no dpkg-query) OR "no path found matching pattern" (exit 1).
            return false;
        }

        // Output is `<package>[, <package>...]: <path>`. Confirm OUR package owns it, not
        // just that *some* package does (e.g. a path collision under a shared dir).
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }
            var owners = line[..colon];
            foreach (var owner in owners.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // dpkg may qualify with an arch (`packetnet:arm64`); match the package stem.
                var name = owner.Split(':', 2, StringSplitOptions.TrimEntries)[0];
                if (string.Equals(name, PackageName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>`apt-cache policy packetnet` — does the version table cite a real repo
    /// origin (an http(s) source), not just <c>/var/lib/dpkg/status</c>? Runner not-launched
    /// (apt-cache absent) → false (→ Github, the conservative fall).</summary>
    private static bool AptHasRepoOrigin(IProcessRunner runner)
    {
        var result = runner.Run("apt-cache", new[] { "policy", PackageName });
        if (!result.Executed || result.ExitCode != 0)
        {
            // apt-cache absent, or it doesn't know the package → no upgradable repo → Github.
            return false;
        }

        // The version table lists each available version's source. A repo source is an
        // http(s) URL or a `<host> <suite>/<component>` origin line; the *only* installed-
        // status pseudo-source is `/var/lib/dpkg/status`. If every cited source is the dpkg
        // status file, apt has no repo to upgrade from → Github. A single real repo line → Apt.
        foreach (var raw in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // We only care about the version-table source lines (they carry priority + origin).
            // Form: `500 https://repo.example/oarc bookworm/main amd64 Packages` (real repo) vs
            //       `100 /var/lib/dpkg/status` (installed-only).
            if (!StartsWithPriority(raw))
            {
                continue;
            }
            if (raw.Contains("/var/lib/dpkg/status", StringComparison.Ordinal))
            {
                continue; // the installed-status pseudo-source — not a repo.
            }
            if (raw.Contains("http://", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("https://", StringComparison.OrdinalIgnoreCase))
            {
                return true; // a real http(s) repo origin → apt can upgrade us.
            }
        }

        return false;
    }

    /// <summary>An apt-cache version-table source line begins with a numeric priority token
    /// (e.g. <c>500 …</c>). Cheap discriminator from the "Installed:"/"Candidate:" header lines.</summary>
    private static bool StartsWithPriority(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsDigit(line[i]))
        {
            i++;
        }
        return i > 0 && i < line.Length && line[i] == ' ';
    }

    /// <summary>Resolve the running binary the dpkg-ownership probe should query. Mirrors
    /// <c>readlink -f /proc/self/exe</c> (Linux), falling back to <see cref="Environment.ProcessPath"/>.
    /// On a host without <c>/proc</c> (Windows) → ProcessPath, which dpkg won't own anyway.</summary>
    private static string? ResolveRunningBinary()
    {
        const string procSelfExe = "/proc/self/exe";
        try
        {
            if (File.Exists(procSelfExe))
            {
                // ResolveLinkTarget(returnFinalTarget:true) == `readlink -f`.
                var resolved = File.ResolveLinkTarget(procSelfExe, returnFinalTarget: true)?.FullName;
                if (!string.IsNullOrEmpty(resolved))
                {
                    return resolved;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to ProcessPath.
        }

        return Environment.ProcessPath;
    }

    private enum BuildStamp { Absent, Deb, SelfContained, Unrecognised }

    /// <summary>Read the build stamp. It now encodes <c>deb</c> vs <c>selfcontained</c> ONLY
    /// (the apt/github split is a runtime concern). Unreadable/absent → <see cref="BuildStamp.Absent"/>,
    /// which is treated identically to <c>deb</c> (probe dpkg). An unrecognised token is also
    /// treated as <c>deb</c> (conservatively probe rather than guess self-contained).</summary>
    private static BuildStamp ReadStamp(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath))
            {
                return BuildStamp.Absent;
            }
            return ParseStamp(File.ReadAllText(markerPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BuildStamp.Absent;
        }
    }

    private static BuildStamp ParseStamp(string token) => token.Trim().ToLowerInvariant() switch
    {
        "deb" => BuildStamp.Deb,
        "selfcontained" or "self-contained" => BuildStamp.SelfContained,
        "" => BuildStamp.Absent,
        _ => BuildStamp.Unrecognised,
    };

    /// <summary>Parse the <c>PDN_INSTALL_CHANNEL</c> override against the full channel set
    /// (case/whitespace-insensitive). Unrecognised → <see cref="InstallChannel.Unknown"/>.</summary>
    public static InstallChannel ParseOverride(string token) => token.Trim().ToLowerInvariant() switch
    {
        "apt" => InstallChannel.Apt,
        "github" => InstallChannel.Github,
        "selfcontained" or "self-contained" => InstallChannel.SelfContained,
        "unknown" => InstallChannel.Unknown,
        _ => InstallChannel.Unknown,
    };
}
