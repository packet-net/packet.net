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
