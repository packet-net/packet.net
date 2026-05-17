namespace Packet.Term;

/// <summary>
/// Process-wide handle to the live <see cref="AppSettings"/> instance.
/// Settings mutated from within the TUI (settings prompt, connect-target
/// update) write to this shared instance; <see cref="SaveSettings"/>
/// flushes it to disk. Not thread-safe by itself — the mutation points
/// are the prompt handlers, which never run concurrently with each other.
/// </summary>
internal static class AppContext
{
    /// <summary>The live settings instance for this process.</summary>
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>Convenience accessor for the last-connect-target string.</summary>
    public static string? LastConnectTarget
    {
        get => Settings.LastConnectTarget;
        set => Settings.LastConnectTarget = value;
    }

    /// <summary>Path the settings round-trip through.</summary>
    public static string SettingsPath { get; private set; } = AppSettings.DefaultPath;

    /// <summary>
    /// When <c>false</c>, <see cref="SaveSettings"/> is a no-op. Set this
    /// at boot when MYCALL and port are both fully specified on the CLI —
    /// in that case the launch is treated as ephemeral, so parallel
    /// instances driven by their own CLI flags can run side-by-side in
    /// the same directory without racing on the shared settings file.
    /// </summary>
    public static bool PersistenceEnabled { get; set; } = true;

    /// <summary>Initialise <see cref="Settings"/> from <paramref name="path"/>.</summary>
    public static void Load(string? path = null)
    {
        SettingsPath = path ?? AppSettings.DefaultPath;
        Settings = AppSettings.Load(SettingsPath);
    }

    /// <summary>Persist <see cref="Settings"/> to <see cref="SettingsPath"/>.</summary>
    public static void SaveSettings()
    {
        if (!PersistenceEnabled) return;
        try
        {
            Settings.Save(SettingsPath);
        }
        catch (IOException)
        {
            // Best-effort. The TUI will surface the failure on the next
            // attempt; we don't want a transient disk error to take the
            // foreground render loop down.
        }
    }
}
