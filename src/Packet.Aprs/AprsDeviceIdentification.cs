using System.Collections.Frozen;
using System.Text.Json;

namespace Packet.Aprs;

/// <summary>A device or program that sends APRS, from the APRS device identification database.</summary>
/// <param name="Vendor">Manufacturer or author, if known.</param>
/// <param name="Model">Product name, if known.</param>
/// <param name="DeviceClass">Kind of device, e.g. <c>ht</c>, <c>rig</c>, <c>tracker</c>, <c>software</c>, <c>app</c>.</param>
/// <param name="OperatingSystem">Operating system, for software.</param>
/// <param name="Features">Feature tags, e.g. <c>messaging</c>.</param>
public sealed record AprsDevice(string? Vendor, string? Model, string? DeviceClass, string? OperatingSystem, IReadOnlyList<string> Features)
{
    /// <summary>True if the database lists the device as able to do APRS messaging.</summary>
    public bool SupportsMessaging => Features.Contains("messaging");

    /// <summary>"Vendor Model", or whichever of the two is known.</summary>
    public override string ToString() => string.Join(' ', new[] { Vendor, Model }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// Identifies the sending device from a packet's destination address ("tocall", e.g. <c>APDW18</c>
/// is Dire Wolf 1.8) or from a Mic-E report's type code and suffix (APRS12c §4, §10).
/// </summary>
/// <remarks>
/// Uses an embedded snapshot of the APRS device identification database,
/// <see href="https://github.com/aprsorg/aprs-deviceid">aprsorg/aprs-deviceid</see>
/// (maintained by Hessu OH7LZB, licensed CC BY-SA 2.0), which is the authority for device
/// identifiers since 2022. <see cref="DatabaseVersion"/> says which revision is embedded;
/// <c>scripts/update-aprs-deviceid.py</c> refreshes it.
/// </remarks>
public static class AprsDeviceIdentification
{
    private static readonly Lazy<Database> Db = new(Load);

    /// <summary>The source commit and date of the embedded database.</summary>
    public static string DatabaseVersion => Db.Value.Version;

    /// <summary>
    /// Looks up a destination address (tocall). Exact entries win; then the wildcard entry with the
    /// most literal characters (<c>?</c> matches any character, <c>n</c> a digit, <c>*</c> the rest).
    /// Returns null for unknown identifiers and for Mic-E destinations.
    /// </summary>
    public static AprsDevice? FromDestination(AprsAddress destination) => FromTocall(destination.Base);

    /// <summary>Looks up a tocall given as text (callsign part only).</summary>
    public static AprsDevice? FromTocall(string tocall)
    {
        ArgumentNullException.ThrowIfNull(tocall);
        Database db = Db.Value;
        if (db.Exact.TryGetValue(tocall, out AprsDevice? exact))
        {
            return exact;
        }

        foreach ((string pattern, AprsDevice device) in db.Wildcards)
        {
            if (Matches(pattern, tocall))
            {
                return device;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a Mic-E device from its type code (the byte after the symbol) and comment suffix.
    /// </summary>
    public static AprsDevice? FromMicE(char? typeCode, string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        Database db = Db.Value;
        if (typeCode is '`' or '\'')
        {
            return db.MicE.TryGetValue(suffix, out AprsDevice? d) ? d : null;
        }

        return typeCode is { } prefix && db.MicELegacy.TryGetValue((prefix, suffix), out AprsDevice? legacy) ? legacy : null;
    }

    /// <summary>Identifies the sender of a packet: from Mic-E type codes for Mic-E, otherwise from the destination.</summary>
    public static AprsDevice? Identify(AprsPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return packet.Data is AprsMicEReport m ? FromMicE(m.TypeCode, m.DeviceSuffix) : FromDestination(packet.Destination);
    }

    internal static IEnumerable<string> MicESuffixes => Db.Value.MicE.Keys;

    internal static IEnumerable<string> MicELegacySuffixes(char prefix) =>
        Db.Value.MicELegacy.Keys.Where(k => k.Prefix == prefix && k.Suffix.Length > 0).Select(k => k.Suffix);

    private static bool Matches(string pattern, string tocall)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            char p = pattern[i];
            if (p == '*')
            {
                return true;
            }

            if (i >= tocall.Length)
            {
                return false;
            }

            char c = tocall[i];
            bool ok = p switch
            {
                '?' => true,
                'n' => char.IsAsciiDigit(c),
                _ => p == c,
            };
            if (!ok)
            {
                return false;
            }
        }

        return tocall.Length == pattern.Length;
    }

    private sealed record Database(
        string Version,
        FrozenDictionary<string, AprsDevice> Exact,
        IReadOnlyList<(string Pattern, AprsDevice Device)> Wildcards,
        FrozenDictionary<string, AprsDevice> MicE,
        FrozenDictionary<(char Prefix, string Suffix), AprsDevice> MicELegacy);

    private static Database Load()
    {
        using Stream stream = typeof(AprsDeviceIdentification).Assembly.GetManifestResourceStream("Packet.Aprs.Data.aprs-deviceid.json")
            ?? throw new InvalidOperationException("embedded device database is missing");
        using JsonDocument doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;

        var exact = new Dictionary<string, AprsDevice>(StringComparer.Ordinal);
        var wildcards = new List<(string, AprsDevice)>();
        foreach (JsonElement e in root.GetProperty("tocalls").EnumerateArray())
        {
            string tocall = e.GetProperty("tocall").GetString()!;
            AprsDevice device = Device(e);
            if (tocall.AsSpan().IndexOfAny("?n*") >= 0)
            {
                wildcards.Add((tocall, device));
            }
            else
            {
                exact.TryAdd(tocall, device);
            }
        }

        // Most specific first: more literal characters, then longer patterns.
        wildcards.Sort((a, b) =>
        {
            int literals = Literals(b.Item1).CompareTo(Literals(a.Item1));
            return literals != 0 ? literals : b.Item1.Length.CompareTo(a.Item1.Length);
        });

        var mice = new Dictionary<string, AprsDevice>(StringComparer.Ordinal);
        foreach (JsonElement e in root.GetProperty("mice").EnumerateArray())
        {
            mice.TryAdd(e.GetProperty("suffix").GetString()!, Device(e));
        }

        var legacy = new Dictionary<(char, string), AprsDevice>();
        foreach (JsonElement e in root.GetProperty("micelegacy").EnumerateArray())
        {
            char prefix = e.GetProperty("prefix").GetString()![0];
            string suffix = e.TryGetProperty("suffix", out JsonElement s) ? s.GetString()! : "";
            legacy.TryAdd((prefix, suffix), Device(e));
        }

        return new Database(root.GetProperty("commit").GetString()!, exact.ToFrozenDictionary(), wildcards, mice.ToFrozenDictionary(), legacy.ToFrozenDictionary());

        static int Literals(string p) => p.Count(c => c is not ('?' or 'n' or '*'));

        static AprsDevice Device(JsonElement e) => new(
            Str(e, "vendor"),
            Str(e, "model"),
            Str(e, "class"),
            Str(e, "os"),
            e.TryGetProperty("features", out JsonElement f) ? f.EnumerateArray().Select(x => x.GetString()!).ToArray() : []);

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) ? v.GetString() : null;
    }
}
