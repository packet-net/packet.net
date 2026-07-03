using System.Globalization;
using System.Text;

namespace Packet.Tune.Core;

/// <summary>The actions of the mode-coordination sub-protocol (the args of a
/// <see cref="TuningVerb.ModeCoordination"/> telegram).</summary>
public enum ModeCoordAction
{
    /// <summary><c>propose|&lt;mode&gt;[|&lt;channel&gt;]</c> — coordinator → responder:
    /// let's switch the TNCs to this mode (and, when included, the radios to this
    /// channel).</summary>
    Propose,

    /// <summary><c>confirm|&lt;mode&gt;[|&lt;channel&gt;]</c> — responder → coordinator:
    /// accepted; awaiting the commit.</summary>
    Confirm,

    /// <summary><c>reject|&lt;mode&gt;[|&lt;reason&gt;]</c> — responder → coordinator:
    /// refused (unknown mode, local policy…). Nothing changes at either end.</summary>
    Reject,

    /// <summary><c>commit|&lt;mode&gt;[|&lt;channel&gt;]</c> — coordinator → responder:
    /// switch now. The commit telegram's sequence number becomes the attempt tag
    /// carried by the verification probe frames. The responder switches on
    /// receipt; the coordinator switches once the side channel confirms
    /// delivery.</summary>
    Commit,

    /// <summary><c>sent|&lt;count&gt;[|&lt;meanTxMs&gt;]</c> — either direction:
    /// "I have finished transmitting &lt;count&gt; probe frames on the link under
    /// test" (optionally with the sender-side mean send→TX-complete latency in
    /// whole ms). The receiver snapshots its probe counter and answers with a
    /// report.</summary>
    ProbesSent,

    /// <summary><c>report|&lt;decoded&gt;/&lt;count&gt;</c> — either direction: how many
    /// of the announced probe frames were decoded here.</summary>
    ProbeReport,

    /// <summary><c>revert[|&lt;reason&gt;]</c> — either direction: abandon the current
    /// mode/channel and return to the session's home mode/channel. The revert
    /// telegram's sequence number tags the home-verification probes that
    /// follow.</summary>
    Revert,
}

/// <summary>
/// One mode-coordination message — the payload of a
/// <see cref="TuningVerb.ModeCoordination"/> (<c>MODE</c>) telegram. The full wire
/// form is e.g. <c>V1|7|MODE|propose|2|1</c> (propose mode 2 on channel 1) and
/// every form fits the 32-character SDM budget. Sequence numbering, dedupe and
/// versioning come from the enclosing <see cref="TuningTelegram"/>.
/// </summary>
/// <remarks>
/// The protocol these messages carry rides a radio side channel
/// (<c>Packet.Radio.IRadioSideChannel</c>) that is mode- and channel-agnostic —
/// which is what breaks the chicken-and-egg of renegotiating the very link the
/// negotiation would otherwise have to travel over. See <see cref="ModeCoordinator"/> /
/// <see cref="ModeResponder"/> for the choreography.
/// </remarks>
public sealed record ModeCoordMessage
{
    /// <summary>The sub-protocol action.</summary>
    public required ModeCoordAction Action { get; init; }

    /// <summary>The NinoTNC mode under negotiation (propose/confirm/reject/commit).</summary>
    public byte? Mode { get; init; }

    /// <summary>The radio channel to switch to, when the attempt includes a channel
    /// change (propose/confirm/commit). Absent = the channel stays as it is.</summary>
    public int? Channel { get; init; }

    /// <summary>Probe frames announced (<see cref="ModeCoordAction.ProbesSent"/>) or
    /// expected (<see cref="ModeCoordAction.ProbeReport"/>).</summary>
    public int? Count { get; init; }

    /// <summary>Probe frames actually decoded (<see cref="ModeCoordAction.ProbeReport"/>).</summary>
    public int? Decoded { get; init; }

    /// <summary>Sender-side mean send→TX-complete latency of the announced probes, in
    /// whole milliseconds (<see cref="ModeCoordAction.ProbesSent"/>; optional).</summary>
    public int? MeanTxMs { get; init; }

    /// <summary>Short failure reason (reject/revert; optional). Kept to a single
    /// pipe-free token so the wire form stays inside the SDM budget.</summary>
    public string? Reason { get; init; }

    /// <summary>Encode to the telegram-args wire form (see <see cref="ModeCoordAction"/>
    /// for the per-action shapes).</summary>
    public string ToArgs()
    {
        var sb = new StringBuilder();
        switch (Action)
        {
            case ModeCoordAction.Propose:
            case ModeCoordAction.Confirm:
            case ModeCoordAction.Commit:
                sb.Append(ActionToken(Action)).Append('|').Append(RequiredMode());
                if (Channel is { } channel)
                {
                    sb.Append('|').Append(channel.ToString(CultureInfo.InvariantCulture));
                }
                break;
            case ModeCoordAction.Reject:
                sb.Append("reject|").Append(RequiredMode());
                AppendReason(sb);
                break;
            case ModeCoordAction.ProbesSent:
                sb.Append("sent|").Append(RequiredCount());
                if (MeanTxMs is { } meanTx)
                {
                    sb.Append('|').Append(meanTx.ToString(CultureInfo.InvariantCulture));
                }
                break;
            case ModeCoordAction.ProbeReport:
                sb.Append("report|")
                  .Append((Decoded ?? throw MissingField("Decoded")).ToString(CultureInfo.InvariantCulture))
                  .Append('/')
                  .Append(RequiredCount());
                break;
            case ModeCoordAction.Revert:
                sb.Append("revert");
                AppendReason(sb);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Action), Action, "unknown mode-coordination action");
        }
        return sb.ToString();
    }

    /// <summary>Wrap in a <see cref="TuningVerb.ModeCoordination"/> telegram with the
    /// given sequence number.</summary>
    public TuningTelegram ToTelegram(int sequence) =>
        new(sequence, TuningVerb.ModeCoordination, ToArgs());

    /// <summary>Try to parse the args of a <c>MODE</c> telegram. Unknown actions and
    /// malformed numbers parse as <c>false</c> (forward compatibility: peers ignore
    /// what they cannot read).</summary>
    public static bool TryParse(string? args, out ModeCoordMessage? message)
    {
        message = null;
        if (string.IsNullOrEmpty(args))
        {
            return false;
        }
        string[] parts = args.Split('|');
        switch (parts[0])
        {
            case "propose" or "confirm" or "commit":
            {
                if (parts.Length is < 2 or > 3 || !TryByte(parts[1], out byte mode))
                {
                    return false;
                }
                int? channel = null;
                if (parts.Length == 3)
                {
                    if (!TryInt(parts[2], out int ch))
                    {
                        return false;
                    }
                    channel = ch;
                }
                message = new ModeCoordMessage
                {
                    Action = parts[0] switch
                    {
                        "propose" => ModeCoordAction.Propose,
                        "confirm" => ModeCoordAction.Confirm,
                        _ => ModeCoordAction.Commit,
                    },
                    Mode = mode,
                    Channel = channel,
                };
                return true;
            }
            case "reject":
            {
                if (parts.Length is < 2 or > 3 || !TryByte(parts[1], out byte mode))
                {
                    return false;
                }
                message = new ModeCoordMessage
                {
                    Action = ModeCoordAction.Reject,
                    Mode = mode,
                    Reason = parts.Length == 3 ? parts[2] : null,
                };
                return true;
            }
            case "sent":
            {
                if (parts.Length is < 2 or > 3 || !TryInt(parts[1], out int count))
                {
                    return false;
                }
                int? meanTx = null;
                if (parts.Length == 3)
                {
                    if (!TryInt(parts[2], out int mt))
                    {
                        return false;
                    }
                    meanTx = mt;
                }
                message = new ModeCoordMessage { Action = ModeCoordAction.ProbesSent, Count = count, MeanTxMs = meanTx };
                return true;
            }
            case "report":
            {
                if (parts.Length != 2)
                {
                    return false;
                }
                string[] fraction = parts[1].Split('/');
                if (fraction.Length != 2 || !TryInt(fraction[0], out int decoded) || !TryInt(fraction[1], out int count))
                {
                    return false;
                }
                message = new ModeCoordMessage { Action = ModeCoordAction.ProbeReport, Decoded = decoded, Count = count };
                return true;
            }
            case "revert":
            {
                if (parts.Length > 2)
                {
                    return false;
                }
                message = new ModeCoordMessage
                {
                    Action = ModeCoordAction.Revert,
                    Reason = parts.Length == 2 ? parts[1] : null,
                };
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>Try to extract a mode-coordination message from any telegram
    /// (<c>false</c> for non-<c>MODE</c> verbs or unreadable args).</summary>
    public static bool TryFromTelegram(TuningTelegram telegram, out ModeCoordMessage? message)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        if (telegram.Verb != TuningVerb.ModeCoordination)
        {
            message = null;
            return false;
        }
        return TryParse(telegram.Args, out message);
    }

    /// <inheritdoc/>
    public override string ToString() => ToArgs();

    private static string ActionToken(ModeCoordAction action) => action switch
    {
        ModeCoordAction.Propose => "propose",
        ModeCoordAction.Confirm => "confirm",
        ModeCoordAction.Commit => "commit",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "not a mode-carrying action"),
    };

    private void AppendReason(StringBuilder sb)
    {
        if (string.IsNullOrEmpty(Reason))
        {
            return;
        }
        // One short pipe-free token, capped so the enclosing telegram stays
        // inside the side channel's (32-char SDM) budget even with a 4-digit
        // sequence number (the tightest form is reject|<mode>|<reason>).
        string clean = Reason.Replace('|', '/');
        sb.Append('|').Append(clean.Length <= MaxReasonLength ? clean : clean[..MaxReasonLength]);
    }

    /// <summary>Longest reason token that keeps every wire form inside the
    /// 32-character SDM budget at 4-digit sequence numbers.</summary>
    public const int MaxReasonLength = 9;

    private string RequiredMode() =>
        (Mode ?? throw MissingField("Mode")).ToString(CultureInfo.InvariantCulture);

    private string RequiredCount() =>
        (Count ?? throw MissingField("Count")).ToString(CultureInfo.InvariantCulture);

    private InvalidOperationException MissingField(string field) =>
        new($"a {Action} message needs {field}");

    private static bool TryByte(string text, out byte value) =>
        byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
