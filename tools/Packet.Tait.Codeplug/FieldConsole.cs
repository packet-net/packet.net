using System.Globalization;

namespace Packet.Tait.Codeplug;

/// <summary>Maps the typed <see cref="CodeplugFields"/> to and from flat <c>name=value</c> text, so
/// the CLI can dump every field and get/set one by name. Channel fields are named
/// <c>ch&lt;N&gt;.&lt;field&gt;</c> (e.g. <c>ch0.bandwidth</c>); global fields are bare
/// (e.g. <c>sdm</c>).</summary>
public static class FieldConsole
{
    /// <summary>Every field as an ordered (name, value) list.</summary>
    public static IReadOnlyList<(string Name, string Value)> Describe(CodeplugFields f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var rows = new List<(string, string)> { ("channels", Int(f.ChannelCount)) };
        for (int c = 0; c < f.ChannelCount; c++)
        {
            rows.Add(($"ch{c}.rxfreq", Int(f.GetRxFrequencyHz(c))));
            rows.Add(($"ch{c}.txfreq", Int(f.GetTxFrequencyHz(c))));
            rows.Add(($"ch{c}.splittx", f.GetSeparateTxFrequency(c) ? "true" : "false"));
            rows.Add(($"ch{c}.bandwidth", f.GetBandwidth(c).ToString()));
            rows.Add(($"ch{c}.power", f.GetPowerLevel(c).ToString()));
            rows.Add(($"ch{c}.txtonetype", f.GetTxSubaudibleType(c).ToString()));
            rows.Add(($"ch{c}.txtoneindex", Int(f.GetTxSubaudibleIndex(c))));
            rows.Add(($"ch{c}.rxtonetype", f.GetRxSubaudibleType(c).ToString()));
            rows.Add(($"ch{c}.rxtoneindex", Int(f.GetRxSubaudibleIndex(c))));
        }

        rows.Add(("sdm", f.SdmEnabled ? "true" : "false"));
        rows.Add(("thsd", f.ThsdModemEnabled ? "true" : "false"));
        rows.Add(("transparent", f.TransparentModeEnabled ? "true" : "false"));
        rows.Add(("dataport", f.DataPort.ToString()));
        rows.Add(("ffskbaud", f.FfskTransparentBaud.ToString()));
        rows.Add(("rxtap", "R" + Int(f.GetRxTapOutNode())));
        rows.Add(("txtap", "T" + Int(f.GetEptt1TapInNode())));
        rows.Add(("tapunmute", f.TapOutUnmute.ToString()));
        rows.Add(("rxtapinverted", f.RxTapOutInverted ? "true" : "false"));
        rows.Add(("txtapinverted", f.Eptt1TapInInverted ? "true" : "false"));
        return rows;
    }

    /// <summary>Read one field's value by name, or throw if the name is unknown.</summary>
    public static string Get(CodeplugFields f, string name)
    {
        foreach ((string n, string v) in Describe(f))
        {
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        throw new FormatException($"unknown field '{name}'");
    }

    /// <summary>Set one field by name from text, or throw if the name/value is invalid.</summary>
    public static void Set(CodeplugFields f, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(f);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        if (name.StartsWith("ch", StringComparison.OrdinalIgnoreCase) && name.Contains('.', StringComparison.Ordinal))
        {
            int dot = name.IndexOf('.', StringComparison.Ordinal);
            int channel = int.Parse(name.AsSpan(2, dot - 2), CultureInfo.InvariantCulture);
            string field = name[(dot + 1)..].ToLowerInvariant();
            switch (field)
            {
                case "rxfreq": f.SetRxFrequencyHz(channel, Hz(value)); return;
                case "txfreq": f.SetTxFrequencyHz(channel, Hz(value)); return;
                case "splittx": f.SetSeparateTxFrequency(channel, Bool(value)); return;
                case "bandwidth": f.SetBandwidth(channel, Enum<Bandwidth>(value)); return;
                case "power": f.SetPowerLevel(channel, Enum<PowerLevel>(value)); return;
                case "txtonetype": f.SetTxSubaudibleType(channel, Enum<SubaudibleType>(value)); return;
                case "txtoneindex": f.SetTxSubaudibleIndex(channel, int.Parse(value, CultureInfo.InvariantCulture)); return;
                case "rxtonetype": f.SetRxSubaudibleType(channel, Enum<SubaudibleType>(value)); return;
                case "rxtoneindex": f.SetRxSubaudibleIndex(channel, int.Parse(value, CultureInfo.InvariantCulture)); return;
                default: throw new FormatException($"unknown channel field '{field}'");
            }
        }

        switch (name.ToLowerInvariant())
        {
            case "sdm": f.SdmEnabled = Bool(value); return;
            case "thsd": f.ThsdModemEnabled = Bool(value); return;
            case "transparent": f.TransparentModeEnabled = Bool(value); return;
            case "dataport": f.DataPort = Enum<DataPort>(value); return;
            case "ffskbaud": f.FfskTransparentBaud = Enum<FfskBaud>(value); return;
            case "rxtap": f.SetRxTapOutNode(Node(value, 'R')); return;
            case "txtap": f.SetEptt1TapInNode(Node(value, 'T')); return;
            case "tapunmute": f.TapOutUnmute = Enum<TapOutUnmute>(value); return;
            case "rxtapinverted": f.RxTapOutInverted = Bool(value); return;
            case "txtapinverted": f.Eptt1TapInInverted = Bool(value); return;
            default: throw new FormatException($"unknown field '{name}'");
        }
    }

    private static string Int(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static long Hz(string s) => long.Parse(s, CultureInfo.InvariantCulture);

    private static bool Bool(string s) => s.ToLowerInvariant() switch
    {
        "true" or "on" or "1" or "yes" => true,
        "false" or "off" or "0" or "no" => false,
        _ => throw new FormatException($"expected a boolean, got '{s}'"),
    };

    private static int Node(string s, char prefix)
    {
        string t = s.Trim();
        if (t.Length > 0 && (t[0] == prefix || t[0] == char.ToLowerInvariant(prefix)))
        {
            t = t[1..];
        }

        return int.Parse(t, CultureInfo.InvariantCulture);
    }

    private static T Enum<T>(string s) where T : struct, System.Enum
    {
        if (System.Enum.TryParse(s, ignoreCase: true, out T value) && System.Enum.IsDefined(value))
        {
            return value;
        }

        throw new FormatException($"'{s}' is not a valid {typeof(T).Name} (one of: {string.Join(", ", System.Enum.GetNames<T>())})");
    }
}
