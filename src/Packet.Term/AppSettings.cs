using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Term;

/// <summary>
/// Persistent user preferences. Round-trips JSON at
/// <c>&lt;LocalAppData&gt;/PacketNet/Packet.Term/settings.json</c>.
/// </summary>
/// <remarks>
/// Path resolution uses <see cref="Environment.SpecialFolder.LocalApplicationData"/>
/// which on Linux is <c>~/.config</c>, on Windows <c>%LOCALAPPDATA%</c>, and on
/// macOS <c>~/Library/Application Support</c>.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>Our station identity, e.g. <c>M0LTE-1</c>.</summary>
    public string? MyCall { get; set; }

    /// <summary>Serial port the KISS modem lives on, e.g. <c>/dev/ttyUSB0</c> or <c>COM5</c>.</summary>
    public string? SerialPort { get; set; }

    /// <summary>
    /// The callsign last typed into the connect prompt. Pre-filled as the
    /// default value next time the user hits <kbd>C</kbd>.
    /// </summary>
    public string? LastConnectTarget { get; set; }

    /// <summary>Absolute path to the on-disk settings file for this user.</summary>
    public static string DefaultPath { get; } = ResolveDefaultPath();

    private static string ResolveDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PacketNet", "Packet.Term", "settings.json");
    }

    /// <summary>Load settings from <paramref name="path"/>, or a blank instance if the file is missing.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(path);
            var loaded = JsonSerializer.Deserialize(stream, AppSettingsJsonContext.Default.AppSettings);
            return loaded ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Corrupt file — fall back to defaults rather than crash on startup.
            return new AppSettings();
        }
    }

    /// <summary>Persist this instance to <paramref name="path"/>, creating parent directories as needed.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, this, AppSettingsJsonContext.Default.AppSettings);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
