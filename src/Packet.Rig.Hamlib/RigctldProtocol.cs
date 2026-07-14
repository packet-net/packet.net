using System.Globalization;

namespace Packet.Rig.Hamlib;

/// <summary>
/// Pure parsing for hamlib's NET rigctl ("rigctld") wire protocol, Extended Response flavour —
/// the <c>+</c>-prefixed form where every reply is: an echo line (<c>get_freq:</c> /
/// <c>set_freq: 7074000</c>), zero or more payload lines, and a terminating <c>RPRT n</c>.
/// The extended protocol is the only sane machine dialect: the default protocol's replies have
/// no terminator on success and a per-command line count, which is exactly the regex fragility
/// every surveyed client ecosystem ran into. Kept free of IO so tests hit it directly.
/// </summary>
internal static class RigctldProtocol
{
    /// <summary>
    /// Hamlib error names by positive error code — <c>rig_errcode_e</c> from <c>rig.h</c>
    /// (verified against Hamlib master, 2026). <c>RPRT -n</c> on the wire means error n here.
    /// </summary>
    private static readonly string[] ErrorNames =
    [
        "RIG_OK",           // 0
        "RIG_EINVAL",       // 1  invalid parameter
        "RIG_ECONF",        // 2  invalid configuration
        "RIG_ENOMEM",       // 3  memory shortage
        "RIG_ENIMPL",       // 4  not implemented, but will be
        "RIG_ETIMEOUT",     // 5  communication timed out (rigctld <-> rig, not us <-> rigctld)
        "RIG_EIO",          // 6  IO error, including open failed
        "RIG_EINTERNAL",    // 7  internal hamlib error
        "RIG_EPROTO",       // 8  protocol error
        "RIG_ERJCTED",      // 9  command rejected by the rig
        "RIG_ETRUNC",       // 10 command performed, but arg truncated
        "RIG_ENAVAIL",      // 11 feature not available
        "RIG_ENTARGET",     // 12 target VFO unaccessible
        "RIG_BUSERROR",     // 13 communication bus error
        "RIG_BUSBUSY",      // 14 communication bus collision
        "RIG_EARG",         // 15 null rig handle or invalid pointer parameter
        "RIG_EVFO",         // 16 invalid VFO
        "RIG_EDOM",         // 17 argument out of domain of function
        "RIG_EDEPRECATED",  // 18 function deprecated
        "RIG_ESECURITY",    // 19 security error
        "RIG_EPOWER",       // 20 rig not powered on
        "RIG_ELIMIT",       // 21 limit exceeded
        "RIG_EACCESS",      // 22 access denied
    ];

    /// <summary>Human-readable form of a negative <c>RPRT</c> value, e.g. <c>-11</c> →
    /// <c>"RIG_ENAVAIL (-11)"</c>.</summary>
    internal static string DescribeError(int rprt)
    {
        var code = -rprt;
        var name = code >= 0 && code < ErrorNames.Length ? ErrorNames[code] : "unknown hamlib error";
        return $"{name} ({rprt})";
    }

    /// <summary>Parse an <c>RPRT n</c> terminator line. </summary>
    internal static bool TryParseRprt(string line, out int code)
    {
        code = 0;
        return line.StartsWith("RPRT ", StringComparison.Ordinal)
            && int.TryParse(line.AsSpan(5), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out code);
    }

    /// <summary>
    /// Extract a named field from extended-reply payload lines: finds the first
    /// <c>Key: value</c> / <c>Key:&lt;tab&gt;value</c> line matching <paramref name="key"/> and
    /// returns the trimmed value, or null. (Structured gets label their payload —
    /// <c>Frequency: 14074000</c> — but level reads return a bare value line, so callers fall
    /// back to <see cref="BareValue"/> where appropriate.)
    /// </summary>
    internal static string? GetField(IReadOnlyList<string> payload, string key)
    {
        foreach (var line in payload)
        {
            if (line.Length > key.Length
                && line[key.Length] == ':'
                && line.StartsWith(key, StringComparison.Ordinal))
            {
                return line[(key.Length + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>The single bare-value payload line of a level/parm read (<c>get_level: SWR</c>
    /// replies carry the value with no <c>Key:</c> label).</summary>
    internal static string BareValue(IReadOnlyList<string> payload, string command)
        => payload.Count >= 1
            ? payload[0].Trim()
            : throw new RigProtocolException($"rigctld reply to '{command}' carried no value line.");

    /// <summary>Parse hamlib's decimal number format (plain integers from the dummy rig, but
    /// doubles are legal for frequency) into integer Hz.</summary>
    internal static long ParseHz(string value, string command)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz)
            ? checked((long)Math.Round(hz))
            : throw new RigProtocolException($"rigctld reply to '{command}' had unparseable frequency '{value}'.");

    internal static double ParseDouble(string value, string command)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new RigProtocolException($"rigctld reply to '{command}' had unparseable number '{value}'.");

    /// <summary>
    /// Interpret a <c>\chk_vfo</c> reply. The shape varies by hamlib version — bare <c>0</c>/
    /// <c>1</c> (4.0+), <c>CHKVFO 0</c> (3.3), <c>ChkVFO: 0</c> (extended-form echo) — so accept
    /// any line whose last token is 0/1. A server that rejects the command outright
    /// (<c>RPRT -n</c>) predates VFO mode entirely, which means VFO mode is off.
    /// </summary>
    internal static bool ParseChkVfo(string line)
    {
        if (TryParseRprt(line, out _))
        {
            return false;
        }

        var lastToken = line.TrimEnd();
        var space = lastToken.LastIndexOf(' ');
        if (space >= 0)
        {
            lastToken = lastToken[(space + 1)..];
        }

        return lastToken switch
        {
            "0" => false,
            "1" => true,
            _ => throw new RigProtocolException($"Unrecognised chk_vfo reply '{line}'."),
        };
    }

    /// <summary>
    /// Digest a <c>\dump_caps</c> payload into capability flags + identity. Keys off the stable
    /// prose lines (<c>Can get Frequency:&lt;tab&gt;Y</c>, <c>Get level: SWR(…) …</c>); hamlib
    /// prints <c>Y</c>/<c>N</c>/<c>E</c> (E = emulated by the backend, which works), so
    /// anything but <c>N</c> counts as supported. Advertised caps are a statement of intent —
    /// a rig can still reject at runtime (the dummy rig advertises PTT it cannot key without a
    /// PTT device) and that surfaces as <see cref="RigCommandException"/>.
    /// </summary>
    internal static (RigCapabilities Capabilities, RigInfo Info) ParseDumpCaps(IReadOnlyList<string> payload)
    {
        var caps = RigCapabilities.None;
        string? model = null, mfg = null;

        foreach (var line in payload)
        {
            if (Value(line, "Model name:") is { } m)
            {
                model = m;
            }
            else if (Value(line, "Mfg name:") is { } v)
            {
                mfg = v;
            }
            else if (Value(line, "Can get Frequency:") is { } gf)
            {
                caps |= Supported(gf, RigCapabilities.FrequencyGet);
            }
            else if (Value(line, "Can set Frequency:") is { } sf)
            {
                caps |= Supported(sf, RigCapabilities.FrequencySet);
            }
            else if (Value(line, "Can get Mode:") is { } gm)
            {
                caps |= Supported(gm, RigCapabilities.ModeGet);
            }
            else if (Value(line, "Can set Mode:") is { } sm)
            {
                caps |= Supported(sm, RigCapabilities.ModeSet);
            }
            else if (Value(line, "Can get PTT:") is { } gp)
            {
                caps |= Supported(gp, RigCapabilities.PttGet);
            }
            else if (Value(line, "Can set PTT:") is { } sp)
            {
                caps |= Supported(sp, RigCapabilities.PttSet);
            }
            else if (Value(line, "Can get DCD:") is { } gd)
            {
                // "Can get DCD:" is the gate; the separate "DCD type:" line describes the
                // detection mechanism, not whether the read works — deliberately ignored.
                caps |= Supported(gd, RigCapabilities.DcdRead);
            }
            else if (Value(line, "Get level:") is { } levels)
            {
                foreach (var token in LevelTokens(levels))
                {
                    caps |= token switch
                    {
                        "SWR" => RigCapabilities.SwrMeter,
                        "RFPOWER_METER" => RigCapabilities.RfPowerMeter,
                        "RFPOWER_METER_WATTS" => RigCapabilities.RfPowerMeterWatts,
                        "STRENGTH" => RigCapabilities.SignalStrengthRead,
                        _ => RigCapabilities.None,
                    };
                }
            }
        }

        return (caps, new RigInfo("Hamlib rigctld", mfg, model));

        static string? Value(string line, string prefix)
            => line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..].Trim() : null;

        static RigCapabilities Supported(string value, RigCapabilities flag)
            => value.StartsWith('N') ? RigCapabilities.None : flag;

        static IEnumerable<string> LevelTokens(string levels)
        {
            foreach (var raw in levels.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var paren = raw.IndexOf('(');
                yield return paren >= 0 ? raw[..paren] : raw;
            }
        }
    }
}
