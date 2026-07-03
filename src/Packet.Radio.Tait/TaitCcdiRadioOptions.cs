using System.Globalization;

namespace Packet.Radio.Tait;

/// <summary>Behavioural knobs for <see cref="TaitCcdiRadio"/>.</summary>
public sealed record TaitCcdiRadioOptions
{
    /// <summary>
    /// How long the link may stay silent before the watchdog probes the radio (RSSI query in
    /// Command mode, pulse in CCR — the CCR manual itself recommends a 10 s pulse cadence).
    /// <c>null</c> disables the watchdog. Default 30 s.
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive failed probes before <see cref="TaitCcdiRadio.ConnectionState"/>
    /// goes <see cref="TaitConnectionState.Faulted"/>. A later successful probe recovers to
    /// Healthy automatically (radio power-cycles heal without a reopen).</summary>
    public int FaultAfterConsecutivePingFailures { get; init; } = 3;

    /// <summary>Per-transaction response deadline. Bench-measured round trips are ~15 ms, so
    /// the 2 s default is generous while still failing fast on a wedged interface.</summary>
    public TimeSpan TransactionTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// After a set-command's prompt, how long to wait for a trailing ERROR before declaring
    /// success — the radio prompts <i>before</i> it reports a rejection (hardware-observed).
    /// Applies to prompt-completed commands only; queries are unaffected.
    /// </summary>
    public TimeSpan PromptErrorGrace { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>Which serial-protocol interpreter the radio port is in.</summary>
public enum TaitProtocolMode
{
    /// <summary>CCDI Command mode — transactions + unsolicited PROGRESS/ERROR/RING.</summary>
    Command,

    /// <summary>Transparent mode — the port is a byte pipe through the radio's FFSK/THSD modem.</summary>
    Transparent,

    /// <summary>CCR mode — the run-time channel-programming interpreter (TM8100 only).</summary>
    Ccr,
}

/// <summary>Link/radio health as judged by the read pump and keep-alive watchdog.</summary>
public enum TaitConnectionState
{
    /// <summary>The radio is answering.</summary>
    Healthy,

    /// <summary>The serial link died or the radio stopped answering probes.</summary>
    Faulted,
}

/// <summary>CANCEL command variants (§1.9.1).</summary>
public enum TaitCancelType
{
    /// <summary>Clear down the current call (incl. retries/deferred calling/emergency).</summary>
    Call = 0,

    /// <summary>Delete all queued received SDMs (frees the one-deep receive buffer).</summary>
    DeleteReceivedSdm = 1,

    /// <summary>Return the radio to the idle display.</summary>
    IdleDisplay = 3,
}

/// <summary>DIAL command variants (§1.9.2).</summary>
public enum TaitDialType
{
    /// <summary>Selcall dialing (digits 0-9, tones A-F, '-', 'V').</summary>
    Selcall = 0,

    /// <summary>DTMF dialing (digits 0-9, A-D, '*', '#', '-').</summary>
    Dtmf = 1,
}

/// <summary>User-controls lockout states (FUNCTION 4, §1.9.3 note 1).</summary>
public enum TaitUserControls
{
    /// <summary>All user inputs, indicators and the display disabled; the radio shows
    /// "CCDI BUSY".</summary>
    DisableAll = 0,

    /// <summary>User input disabled; display and indicators stay live; attempted input sounds
    /// the invalid-keypress tone.</summary>
    DisableInputOnly = 1,

    /// <summary>Normal operation except while a CCDI command is being processed — the power-up
    /// default.</summary>
    EnableAll = 2,
}

/// <summary>Front-panel key numbers (§1.10.5, PROGRESS type 23 — also the key codes
/// <see cref="TaitCcdiRadio.SimulateKeyPressAsync"/> accepts).</summary>
public enum TaitKey : byte
{
    /// <summary>PTT.</summary>
    Ptt = 0x00,
    /// <summary>Hookswitch.</summary>
    Hookswitch = 0x01,
    /// <summary>On/Off key.</summary>
    OnOff = 0x02,
    /// <summary>Up key.</summary>
    Up = 0x03,
    /// <summary>Down key.</summary>
    Down = 0x04,
    /// <summary>Function key 1.</summary>
    Function1 = 0x05,
    /// <summary>Function key 2.</summary>
    Function2 = 0x06,
    /// <summary>Function key 3.</summary>
    Function3 = 0x07,
    /// <summary>Function key 4.</summary>
    Function4 = 0x08,
    /// <summary>Function key 5.</summary>
    Function5 = 0x09,
    /// <summary>Function key 6.</summary>
    Function6 = 0x0A,
    /// <summary>Zero key.</summary>
    Zero = 0x0B,
    /// <summary>One key.</summary>
    One = 0x0C,
    /// <summary>Two key.</summary>
    Two = 0x0D,
    /// <summary>Three key.</summary>
    Three = 0x0E,
    /// <summary>Four key.</summary>
    Four = 0x0F,
    /// <summary>Five key.</summary>
    Five = 0x10,
    /// <summary>Six key.</summary>
    Six = 0x11,
    /// <summary>Seven key.</summary>
    Seven = 0x12,
    /// <summary>Eight key.</summary>
    Eight = 0x13,
    /// <summary>Nine key.</summary>
    Nine = 0x14,
    /// <summary>Star key (*).</summary>
    Star = 0x15,
    /// <summary>Hash key (#).</summary>
    Hash = 0x16,
    /// <summary>Left softkey.</summary>
    SoftkeyLeft = 0x1E,
    /// <summary>Right softkey.</summary>
    SoftkeyRight = 0x1F,
}

/// <summary>The current-channel report (PROGRESS type 21, solicited via FUNCTION 0/5/2).</summary>
/// <param name="Kind">'0' single channel, '1' scan/vote group, '2' captured within a group,
/// '3' temporary (e.g. GPS), '9' not available / invalid.</param>
/// <param name="ChannelId">Channel or group id digits.</param>
/// <param name="Zone">Zone number when the radio reports the 6-digit zoned form (TM8200).</param>
public sealed record TaitChannelReport(char Kind, string ChannelId, int? Zone)
{
    /// <summary>Parse the PROGRESS-21 parameter string: [PARA1][PARA2] where PARA2 is either
    /// the channel id (1–4 digits) or zone(2)+channel(4).</summary>
    public static TaitChannelReport Parse(string para)
    {
        ArgumentException.ThrowIfNullOrEmpty(para);
        char kind = para[0];
        string rest = para[1..];
        if (rest.Length == 6 && int.TryParse(rest[..2], NumberStyles.None, CultureInfo.InvariantCulture, out int zone))
        {
            return new TaitChannelReport(kind, rest[2..].TrimStart('0') is { Length: > 0 } c ? c : "0", zone);
        }
        return new TaitChannelReport(kind, rest, null);
    }
}
