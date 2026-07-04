namespace Packet.Node.Core.Radios;

/// <summary>
/// Resolves a serial device path (e.g. <c>/dev/ttyUSB0</c>) to the stable <c>/dev/serial/by-id/*</c>
/// symlink that canonicalises to it — the udev by-id convention, Linux-only. Returns <c>null</c> when
/// there is no such symlink, when <b>two</b> symlinks canonicalise to the same device (the shared-USB-
/// serial CP2102 CCDI dongles produce ambiguous by-id links — the very reason the CCDI serial, not
/// by-id, is a radio's stable key), or off Linux.
/// </summary>
/// <remarks>The by-id directory is injectable so the ambiguity/uniqueness logic is unit-testable
/// against a temp directory of symlinks; production uses the standard <c>/dev/serial/by-id</c>.</remarks>
public sealed class SerialByIdResolver
{
    /// <summary>The standard udev serial by-id directory.</summary>
    public const string DefaultByIdDirectory = "/dev/serial/by-id";

    private readonly string byIdDirectory;

    /// <summary>Build a resolver over <paramref name="byIdDirectory"/> (default the standard udev
    /// path). Tests pass a temp directory populated with symlinks.</summary>
    public SerialByIdResolver(string? byIdDirectory = null)
    {
        this.byIdDirectory = byIdDirectory ?? DefaultByIdDirectory;
    }

    /// <summary>
    /// The <c>by-id</c> symlink that canonicalises to <paramref name="devicePath"/>, or <c>null</c>
    /// when none / more than one does (ambiguous) / not on Linux / the directory is absent. Never
    /// throws — a filesystem hiccup resolves to <c>null</c>.
    /// </summary>
    public string? Resolve(string devicePath)
    {
        ArgumentNullException.ThrowIfNull(devicePath);

        // by-id is a Linux udev convention; off Linux there is nothing to resolve.
        if (!OperatingSystem.IsLinux() || !Directory.Exists(byIdDirectory))
        {
            return null;
        }

        try
        {
            string target = Canonical(devicePath);
            string? match = null;
            int count = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(byIdDirectory))
            {
                if (string.Equals(Canonical(entry), target, StringComparison.Ordinal))
                {
                    match = entry;
                    count++;
                }
            }
            // Exactly one link → stable identity for this device; zero or many (shared USB serial)
            // → ambiguous, so refuse to guess.
            return count == 1 ? match : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Resolve a path (following a symlink to its final target) to an absolute canonical path.
    private static string Canonical(string path)
    {
        try
        {
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return Path.GetFullPath(resolved?.FullName ?? path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }
}
