using System.Globalization;

namespace Packet.Radio.Tait.Ccdi;

/// <summary>A decoded CCDI message from the radio (manual §1.10). Unrecognised idents surface
/// as <see cref="CcdiUnknownMessage"/> rather than being dropped.</summary>
public abstract record CcdiMessage(char Ident)
{
    /// <summary>Decode a validated <see cref="CcdiFrame"/> into its typed message form.</summary>
    public static CcdiMessage Decode(CcdiFrame frame)
    {
        string p = frame.Parameters;
        return frame.Ident switch
        {
            'm' when p.Length >= 3 => new CcdiModelMessage(
                RuType: p[0], RuModel: p[1], RuTier: p[2], CcdiVersion: p[3..]),
            'n' => new CcdiSerialMessage(p),
            'v' when p.Length >= 2 => new CcdiVersionMessage(RecordNumber: p[..2], Version: p[2..]),
            'j' when p.Length >= 3 && int.TryParse(p[..3], NumberStyles.None, CultureInfo.InvariantCulture, out int cctm) =>
                new CcdiQueryResultMessage(CctmCommand: cctm, Value: p[3..]),
            'p' when p.Length >= 2 && byte.TryParse(p[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte ptype) =>
                new CcdiProgressMessage((CcdiProgressType)ptype, Para: p[2..]),
            'e' when p.Length >= 3 && byte.TryParse(p[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte errNum) =>
                new CcdiErrorMessage(Category: p[0], ErrorNumber: errNum),
            'r' when p.Length >= 7 => new CcdiRingMessage(
                Category: p[0], RingType: p[1..5], Status: p[5..7], CallerId: p[7..]),
            's' => new CcdiSdmMessage(p),
            'd' when p.Length >= 1 => new CcdiDisplayMessage(Kind: p[0], Payload: p[1..]),
            'z' => new CcdiTdmaDataMessage(p),
            // Upper-case idents are the CCR-mode interpreter (CCDI idents are lower-case).
            '+' when p.Length >= 1 => new CcrAckMessage(EchoedCommand: p[0]),
            '-' when p.Length >= 2 && byte.TryParse(p[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte reason) =>
                new CcrNakMessage(Reason: reason, EchoedCommand: p.Length >= 3 ? p[2] : null),
            'V' => new CcrSelcallDecodeMessage(p),
            'M' when p.Length >= 1 => new CcrNotificationMessage(Kind: p[0]),
            'Q' when p.Length >= 1 => new CcrPulseResultMessage(HasMinimumConfiguration: p[0] == 'P'),
            _ => new CcdiUnknownMessage(frame.Ident, p),
        };
    }
}

/// <summary>MODEL (§1.10.4) — radio type/model/tier plus the CCDI protocol version.</summary>
public sealed record CcdiModelMessage(char RuType, char RuModel, char RuTier, string CcdiVersion) : CcdiMessage('m');

/// <summary>RADIO_SERIAL (§1.10.7).</summary>
public sealed record CcdiSerialMessage(string SerialNumber) : CcdiMessage('n');

/// <summary>RADIO_VERSIONS (§1.10.8) — one record of the multi-message version inventory.
/// Record numbers: 00 model name, 01 software, 02 database, 03 FPGA.</summary>
public sealed record CcdiVersionMessage(string RecordNumber, string Version) : CcdiMessage('v');

/// <summary>CCTM_QUERY_RESULTS (§1.10.1) — the answer to a QUERY type-5 CCTM command.</summary>
public sealed record CcdiQueryResultMessage(int CctmCommand, string Value) : CcdiMessage('j')
{
    /// <summary>CCTM 063/064 RSSI results are an integer in units of 0.1 dB(m); this converts
    /// to dBm. Returns <c>null</c> when the value isn't a plain integer.</summary>
    public float? AsDecibels() =>
        int.TryParse(Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int tenths)
            ? tenths / 10f
            : null;

    /// <summary>The value as a plain invariant-culture integer, or <c>null</c>.</summary>
    public int? AsInteger() =>
        int.TryParse(Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : null;
}

/// <summary>PROGRESS (§1.10.5) — unsolicited radio state-change notification.</summary>
public sealed record CcdiProgressMessage(CcdiProgressType Type, string Para) : CcdiMessage('p');

/// <summary>ERROR (§1.10.2). Category '0' = transaction error, '1' = system error.</summary>
public sealed record CcdiErrorMessage(char Category, byte ErrorNumber) : CcdiMessage('e')
{
    /// <summary>Human-readable meaning of a category-0 (transaction) error number.</summary>
    public string Describe() => Category switch
    {
        '0' => ErrorNumber switch
        {
            0x01 => "unsupported command",
            0x02 => "checksum error",
            0x03 => "parameter error",
            0x05 => "radio not ready",
            0x06 => "command not accepted in current configuration",
            _ => $"transaction error 0x{ErrorNumber:X2}",
        },
        '1' => $"fatal system error 0x{ErrorNumber:X2}",
        _ => $"error category '{Category}' 0x{ErrorNumber:X2}",
    };
}

/// <summary>RING (§1.10.9) — an incoming call. <paramref name="RingType"/> is the four-character
/// [TYPE1..TYPE4] string (TYPE1: 0 voice, 2 status, 3 interrogation, 4 SDM received, 5 data,
/// 6 remote monitor; TYPE2: 0 normal/1 emergency; TYPE3: 0 individual/1 group/2 super-group).
/// <paramref name="Status"/> is "FF" when no status value was received.</summary>
public sealed record CcdiRingMessage(char Category, string RingType, string Status, string CallerId) : CcdiMessage('r');

/// <summary>GET_SDM (§1.10.3) — the buffered short data message, in response to QUERY type 1
/// (which also clears the radio's one-deep SDM buffer). Empty <paramref name="Data"/> = no SDM
/// buffered.</summary>
public sealed record CcdiSdmMessage(string Data) : CcdiMessage('s');

/// <summary>QUERY_DISPLAY_RESPONSE (§1.10.6) — one element of a display-dump burst.
/// <paramref name="Kind"/>: '0' start, 'F' end (payload = error digit), '1' text object
/// (payload = 9 hex chars x/y/font + the string), '2' icon object (11 hex chars).</summary>
public sealed record CcdiDisplayMessage(char Kind, string Payload) : CcdiMessage('d');

/// <summary>TDMA_DATA (§1.10.10, TM8200 only) — a received TDMA packet's raw data.</summary>
public sealed record CcdiTdmaDataMessage(string Data) : CcdiMessage('z');

/// <summary>CCR positive acknowledgement (§2.6): the command ident was accepted (accepted ≠
/// executed — e.g. TX frequency latches until the next PTT).</summary>
public sealed record CcrAckMessage(char EchoedCommand) : CcdiMessage('+');

/// <summary>CCR negative acknowledgement (§2.7). <see cref="EchoedCommand"/> is present only
/// when the command's checksum was valid.</summary>
public sealed record CcrNakMessage(byte Reason, char? EchoedCommand) : CcdiMessage('-')
{
    /// <summary>Human-readable meaning of the NAK reason code.</summary>
    public string Describe() => Reason switch
    {
        0x01 => "invalid CCR command",
        0x02 => "checksum error",
        0x03 => "parameter error",
        0x05 => "radio busy",
        0x06 => "command not accepted",
        _ => $"CCR NAK 0x{Reason:X2}",
    };
}

/// <summary>CCR unsolicited Selcall decode (§2.9.3): tones decoded from the channel — digits,
/// special tones A–F ('E' repeat), '-' for a gap of one tone period.</summary>
public sealed record CcrSelcallDecodeMessage(string Tones) : CcdiMessage('V');

/// <summary>CCR unsolicited notification (§2.9): 'R' = CCR mode initialised (M01R00),
/// 'P' = PTT approaching the transmit-timer limit (10 s warning, M01P02).</summary>
public sealed record CcrNotificationMessage(char Kind) : CcdiMessage('M');

/// <summary>CCR pulse response (§2.8.15): <c>true</c> = the radio has its minimum CCR
/// configuration (a receive frequency has been set since entering CCR); <c>false</c> = still on
/// defaults — i.e. the radio has been power-cycled and forgot everything.</summary>
public sealed record CcrPulseResultMessage(bool HasMinimumConfiguration) : CcdiMessage('Q');

/// <summary>Any message whose ident (or parameter shape) we don't decode yet — kept raw so
/// nothing the radio says is invisible to a consumer.</summary>
public sealed record CcdiUnknownMessage(char UnknownIdent, string Parameters) : CcdiMessage(UnknownIdent);

/// <summary>PROGRESS message types (§1.10.5, [PTYPE]).</summary>
public enum CcdiProgressType : byte
{
    /// <summary>A Selcall/Type-99 call was answered.</summary>
    CallAnswered = 0x00,
    /// <summary>Deferred calling in progress.</summary>
    DeferredCalling = 0x01,
    /// <summary>Transmission requested but inhibited.</summary>
    TxInhibited = 0x02,
    /// <summary>Emergency mode initiated.</summary>
    EmergencyModeInitiated = 0x03,
    /// <summary>Emergency mode terminated.</summary>
    EmergencyModeTerminated = 0x04,
    /// <summary>RF detected on the current channel — hardware DCD rising edge.</summary>
    ReceiverBusy = 0x05,
    /// <summary>RF no longer detected on the current channel — hardware DCD falling edge.</summary>
    ReceiverNotBusy = 0x06,
    /// <summary>PTT asserted (radio began transmitting).</summary>
    PttActivated = 0x07,
    /// <summary>PTT released (radio stopped transmitting).</summary>
    PttDeactivated = 0x08,
    /// <summary>Selcall retry.</summary>
    SelcallRetry = 0x16,
    /// <summary>Radio stunned.</summary>
    RadioStunned = 0x17,
    /// <summary>Radio revived.</summary>
    RadioRevived = 0x18,
    /// <summary>Valid FFSK data received while in command mode.</summary>
    FfskDataReceived = 0x19,
    /// <summary>Selcall auto-acknowledge status.</summary>
    SelcallAutoAcknowledge = 0x1C,
    /// <summary>SDM auto-acknowledge status.</summary>
    SdmAutoAcknowledge = 0x1D,
    /// <summary>SDM GPS data received.</summary>
    SdmGpsDataReceived = 0x1E,
    /// <summary>Radio restarted.</summary>
    RadioRestarted = 0x1F,
    /// <summary>Single in-band tone received.</summary>
    SingleInBandToneReceived = 0x20,
    /// <summary>User-initiated channel change.</summary>
    UserInitiatedChannelChange = 0x21,
    /// <summary>TDMA channel event (TM8200).</summary>
    TdmaChannel = 0x22,
    /// <summary>Key action report.</summary>
    KeyAction = 0x23,
    /// <summary>Channel-name report.</summary>
    ChannelName = 0x31,
}
