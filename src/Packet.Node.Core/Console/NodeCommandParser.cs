using System.Text;
using Packet.Core;

namespace Packet.Node.Core.Console;

/// <summary>
/// Maps a raw console input line to a typed <see cref="NodeCommand"/>. Total and
/// allocation-bounded: it <b>never throws</b> on arbitrary input, bounds the
/// line length it will consider, and turns anything it can't classify into
/// <see cref="UnknownCommand"/> (or a typed error like
/// <see cref="MalformedConnect"/>). This is the property/fuzz contract — see
/// <c>tools/Packet.Fuzz</c> and the parser property tests.
/// </summary>
/// <remarks>
/// TNC2 conventions: verbs are case-insensitive and abbreviate to their first
/// letter — <c>C</c>/<c>CONNECT</c>, <c>B</c>/<c>BYE</c>, <c>D</c>/<c>DISCONNECT</c>,
/// <c>N</c>/<c>NODES</c>, <c>I</c>/<c>INFO</c>, <c>H</c>/<c>HELP</c>/<c>?</c>. Any
/// unambiguous prefix of a verb is accepted (e.g. <c>CONN</c>, <c>DISC</c>).
/// </remarks>
public static class NodeCommandParser
{
    /// <summary>The longest input line the parser will consider. Longer input is
    /// truncated before classification so a hostile peer can't drive unbounded
    /// work or allocation. (A station callsign is ≤ 9 chars; 512 is generous.)</summary>
    public const int MaxLineLength = 512;

    /// <summary>
    /// Decode <paramref name="lineBytes"/> as text (UTF-8, lenient — invalid
    /// sequences become replacement chars, never an exception) and parse. Used
    /// by the wire-facing paths and the fuzz harness, which feed raw bytes.
    /// </summary>
    public static NodeCommand Parse(ReadOnlySpan<byte> lineBytes)
    {
        // Bound the work up front: never decode more than MaxLineLength bytes.
        if (lineBytes.Length > MaxLineLength)
        {
            lineBytes = lineBytes[..MaxLineLength];
        }

        // Strip a trailing CR/LF if the caller handed us a raw terminated line,
        // and any embedded NUL the decoder would otherwise carry through.
        string text;
        try
        {
            text = Encoding.UTF8.GetString(lineBytes);
        }
        catch
        {
            // GetString on a Span does not throw for invalid UTF-8 (it inserts
            // U+FFFD), but guard anyway so the "never throws" contract is total
            // even if the encoding policy changes.
            return new UnknownCommand(string.Empty);
        }

        return Parse(text);
    }

    /// <summary>
    /// Parse an already-decoded line into a typed command. Total: every input
    /// maps to some <see cref="NodeCommand"/>.
    /// </summary>
    public static NodeCommand Parse(string? line)
    {
        if (line is null)
        {
            return new EmptyCommand();
        }

        if (line.Length > MaxLineLength)
        {
            line = line[..MaxLineLength];
        }

        // Normalise: drop control chars (CR/LF/NUL/etc.) at the edges and collapse
        // to a clean trimmed string. We keep interior printable content for the
        // Connect argument and the Unknown echo.
        var trimmed = TrimControl(line);
        if (trimmed.Length == 0)
        {
            return new EmptyCommand();
        }

        // Split on the first run of whitespace into verb + remainder.
        int sep = IndexOfWhitespace(trimmed);
        string verb = sep < 0 ? trimmed : trimmed[..sep];
        string rest = sep < 0 ? string.Empty : trimmed[(sep + 1)..].Trim();

        // "?" is a verb on its own (help) regardless of case.
        if (verb == "?")
        {
            return new HelpCommand();
        }

        var upper = verb.ToUpperInvariant();

        if (Matches(upper, "CONNECT"))
        {
            return ParseConnect(rest, trimmed);
        }
        if (Matches(upper, "BYE") || Matches(upper, "DISCONNECT"))
        {
            return new ByeCommand();
        }
        if (Matches(upper, "NODES"))
        {
            return new NodesCommand();
        }
        if (Matches(upper, "INFO"))
        {
            return new InfoCommand();
        }
        if (Matches(upper, "HELP"))
        {
            return new HelpCommand();
        }

        // Over-RF sysop verbs (auth part 4). SYSOP and SESSIONS both start with 'S', so a
        // bare "S" is ambiguous — require ≥2 chars for those two (SY… → SYSOP, SE… →
        // SESSIONS) and never let a single keystroke trigger the auth command. KICK / PORT
        // / RELOAD have unique initials, so the usual unambiguous-prefix rule is safe.
        if (upper.Length >= 2 && Matches(upper, "SYSOP"))
        {
            return ParseSysop(rest);
        }
        if (upper.Length >= 2 && Matches(upper, "SESSIONS"))
        {
            return new SessionsCommand();
        }
        if (Matches(upper, "KICK"))
        {
            return ParseKick(rest);
        }
        if (Matches(upper, "PORT"))
        {
            return ParsePort(rest);
        }
        if (Matches(upper, "RELOAD"))
        {
            return new ReloadCommand();
        }

        return new UnknownCommand(trimmed);
    }

    // SYSOP <code>  (AX.25: callsign implicit from PeerId)
    // SYSOP <user> <code>  (telnet: no callsign). The service interprets the tokens per
    // transport; the parser only splits the first two (extras ignored). The code is never
    // echoed or logged.
    private static SysopCommand ParseSysop(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            return new SysopCommand(null, null);
        }
        int ws = IndexOfWhitespace(rest);
        if (ws < 0)
        {
            return new SysopCommand(rest, null);
        }
        string t1 = rest[..ws];
        string t2 = rest[(ws + 1)..].Trim();
        int ws2 = IndexOfWhitespace(t2);
        if (ws2 >= 0)
        {
            t2 = t2[..ws2];
        }
        return new SysopCommand(t1, string.IsNullOrEmpty(t2) ? null : t2);
    }

    // KICK <id>  — id is "portId:peer" (the same id SESSIONS renders).
    private static NodeCommand ParseKick(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            return new MalformedKick("Kick needs a session id, e.g. KICK gb7rdg:M0LTE-1");
        }
        int ws = IndexOfWhitespace(rest);
        string id = ws < 0 ? rest : rest[..ws];
        return new KickCommand(id);
    }

    // PORT <id> UP|DOWN
    private static NodeCommand ParsePort(string rest)
    {
        const string usage = "Usage: PORT <id> UP|DOWN";
        if (string.IsNullOrWhiteSpace(rest))
        {
            return new MalformedPort(usage);
        }
        int ws = IndexOfWhitespace(rest);
        if (ws < 0)
        {
            return new MalformedPort(usage);
        }
        string id = rest[..ws];
        string state = rest[(ws + 1)..].Trim();
        int ws2 = IndexOfWhitespace(state);
        if (ws2 >= 0)
        {
            state = state[..ws2];
        }
        return state.ToUpperInvariant() switch
        {
            "UP" => new PortPowerCommand(id, true),
            "DOWN" => new PortPowerCommand(id, false),
            _ => new MalformedPort(usage),
        };
    }

    private static NodeCommand ParseConnect(string rest, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            return new MalformedConnect(rawLine, "Connect needs a callsign, e.g. C M0LTE-1");
        }

        // Take the first token as the target; ignore trailing via-path / extras
        // (same-port-only in slice 1, so a via path is not honoured — flagging it
        // as malformed would be hostile, so we just use the first token).
        int ws = IndexOfWhitespace(rest);
        string target = ws < 0 ? rest : rest[..ws];

        if (!Callsign.TryParse(target.ToUpperInvariant(), out var call))
        {
            return new MalformedConnect(rawLine,
                $"'{target}' is not a valid callsign (1–6 letters/digits, optional -SSID 0–15).");
        }

        return new ConnectCommand(call);
    }

    // An input verb matches a canonical verb if it is a non-empty,
    // case-folded prefix of it. "C"→CONNECT, "CONN"→CONNECT, "CONNECT"→CONNECT.
    private static bool Matches(string upperVerb, string canonical) =>
        upperVerb.Length > 0 &&
        upperVerb.Length <= canonical.Length &&
        canonical.AsSpan().StartsWith(upperVerb, StringComparison.Ordinal);

    private static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i])) return i;
        }
        return -1;
    }

    // Trim leading/trailing control + whitespace characters; the interior is
    // left intact (so a callsign with no surrounding junk survives). Bounded by
    // the already-capped string length.
    private static string TrimControl(string s)
    {
        int start = 0;
        int end = s.Length;
        while (start < end && IsTrimmable(s[start])) start++;
        while (end > start && IsTrimmable(s[end - 1])) end--;
        return s[start..end];
    }

    private static bool IsTrimmable(char c) => char.IsControl(c) || char.IsWhiteSpace(c);
}
