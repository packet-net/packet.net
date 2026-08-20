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
            rows.Add(($"ch{c}.squelch", f.GetSquelch(c).ToString()));
            rows.Add(($"ch{c}.txinhibit", f.GetTxInhibit(c).ToString()));
            rows.Add(($"ch{c}.network", Int(f.GetNetwork(c))));
            rows.Add(($"ch{c}.txtone", f.GetTxSubaudible(c)));
            rows.Add(($"ch{c}.rxtone", f.GetRxSubaudible(c)));
        }

        rows.Add(("ctcsstable", string.Join(",", f.CtcssTable.Select(hz => hz.ToString("0.0", CultureInfo.InvariantCulture)))));
        rows.Add(("dcstable", string.Join(",", f.DcsTable)));

        rows.Add(("sdm", f.SdmEnabled ? "true" : "false"));
        rows.Add(("thsd", f.ThsdModemEnabled ? "true" : "false"));
        rows.Add(("transparent", f.TransparentModeEnabled ? "true" : "false"));
        rows.Add(("dataport", f.DataPort.ToString()));
        rows.Add(("ffskbaud", f.FfskTransparentBaud.ToString()));
        rows.Add(("ffskmodembaud", f.FfskModemBaud.ToString()));
        rows.Add(("ccdimode", f.CcdiModeAllowed ? "true" : "false"));
        rows.Add(("powerup", f.PowerupState.ToString()));
        rows.Add(("cmbaud", f.CommandModeBaud.ToString()));
        rows.Add(("hsdbaud", f.HsdBaud.ToString()));
        rows.Add(("ccdisdmout", f.CcdiSdmOutputEnabled ? "true" : "false"));
        rows.Add(("ccdiprogress", f.CcdiProgressMessageEnabled ? "true" : "false"));
        rows.Add(("ccdisdmtextonly", f.CcdiSdmTextOnly ? "true" : "false"));
        rows.Add(("textsdmindicator", f.TextSdmIndicator ? "true" : "false"));
        rows.Add(("textsdmackx", f.TextSdmAutoAckTransmission ? "true" : "false"));
        rows.Add(("textsdmackr", f.TextSdmAutoAckReception ? "true" : "false"));
        rows.Add(("sdmackdelayms", f.SdmAutoAckDelayMs.ToString(CultureInfo.InvariantCulture)));
        rows.Add(("sdmwaitack", f.SdmWaitForAck.ToString(CultureInfo.InvariantCulture)));
        rows.Add(("ignoreesc", f.IgnoreEscapeSequence ? "true" : "false"));
        rows.Add(("ignoresubaud", f.IgnoreSubaudibleOnData ? "true" : "false"));
        // General tab
        rows.Add(("openmonitor", f.OpenMonitorOnDialledCall ? "true" : "false"));
        rows.Add(("selcallout", f.SelcallOutputEnabled ? "true" : "false"));
        rows.Add(("maxframelen", f.MaximumInitialFrameLength ? "true" : "false"));
        rows.Add(("uartdelay", f.UartWriteDelayMs.ToString(CultureInfo.InvariantCulture)));
        rows.Add(("txbackoffmin", f.TxBackoffTimeMinMs.ToString(CultureInfo.InvariantCulture)));
        rows.Add(("txbackoffmax", f.TxBackoffTimeMaxMs.ToString(CultureInfo.InvariantCulture)));
        // Serial Communications tab
        rows.Add(("xon", "0x" + f.XonCharacter.ToString("X2", CultureInfo.InvariantCulture)));
        rows.Add(("xoff", "0x" + f.XoffCharacter.ToString("X2", CultureInfo.InvariantCulture)));
        rows.Add(("cmflow", f.CommandModeFlowControl.ToString()));
        rows.Add(("tmflow", f.FfskTransparentFlowControl.ToString()));
        rows.Add(("hsdflow", f.HsdFlowControl.ToString()));
        // RF Modems tab
        rows.Add(("checkpacketlen", f.CheckPacketLength ? "true" : "false"));
        rows.Add(("toneblank", f.FfskToneBlanking ? "true" : "false"));
        rows.Add(("ffskleadin", f.FfskLeadInDelayMs.ToString(CultureInfo.InvariantCulture)));
        rows.Add(("ffskleadout", f.FfskLeadOutDelayMs.ToString(CultureInfo.InvariantCulture)));
        rows.Add(("widebandmodem", f.WidebandModemEnabled ? "true" : "false"));
        rows.Add(("layer2", f.ThsdLayer2Protocol.ToString()));
        rows.Add(("fec", f.ThsdForwardErrorCorrection ? "true" : "false"));
        rows.Add(("fecblocks", f.ThsdNumberOfBlocks.ToString(CultureInfo.InvariantCulture)));
        rows.Add(("thsdleadin", f.ThsdLeadInDelayMs.ToString(CultureInfo.InvariantCulture)));
        rows.Add(("thsdleadout", f.ThsdLeadOutDelayMs.ToString(CultureInfo.InvariantCulture)));
        // SDM tab
        rows.Add(("sdmbufoverwrite", f.SdmBufferOverwrite ? "true" : "false"));
        rows.Add(("sdmcallerid", f.SdmCallerId ? "true" : "false"));
        // TOTAL Transparent Mode tab (IDs shown in hex, as the CPS does)
        rows.Add(("totalservice", f.TotalService.ToString()));
        rows.Add(("totalradioid", "0x" + f.TotalRadioId.ToString("X4", CultureInfo.InvariantCulture)));
        rows.Add(("totalsystemid", "0x" + f.TotalSystemId.ToString("X2", CultureInfo.InvariantCulture)));
        rows.Add(("totaldestid", "0x" + f.TotalDestinationId.ToString("X4", CultureInfo.InvariantCulture)));
        rows.Add(("totallinkid", "0x" + f.TotalLinkId.ToString("X2", CultureInfo.InvariantCulture)));
        rows.Add(("unitdataidentity", f.UnitDataIdentity));
        // GPS tab (record 0x45; only if present)
        if (f.HasGps)
        {
            rows.Add(("gpsenabled", f.GpsEnabled ? "true" : "false"));
            rows.Add(("gpsport", f.GpsSerialPort.ToString()));
            rows.Add(("gpsbaud", f.GpsBaudRate.ToString()));
            rows.Add(("gpschanneltype", f.GpsPollResponseChannelType.ToString()));
            rows.Add(("gpschannel", f.GpsPollResponseChannel.ToString(CultureInfo.InvariantCulture)));
            rows.Add(("gpscalloutinterval", f.GpsCalloutIntervalSeconds.ToString(CultureInfo.InvariantCulture)));
            rows.Add(("gpsmaxcallouts", f.GpsMaxNumberOfCallouts.ToString(CultureInfo.InvariantCulture)));
            rows.Add(("gpsconntimeout", f.GpsConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture)));
            rows.Add(("gpsleadin", f.GpsLeadInDelayMs.ToString(CultureInfo.InvariantCulture)));
            rows.Add(("gpspolldelay", f.GpsPollResponseDelayMs.ToString(CultureInfo.InvariantCulture)));
            rows.Add(("gpsemergency", f.GpsSendOnEmergencyCallout ? "true" : "false"));
            rows.Add(("gpsdispatcher", f.GpsDispatcherAddress));
        }

        // Customer Data tab (records 0x4C/0x4D; only if present)
        if (f.HasCustomerData)
        {
            for (int i = 1; i <= 4; i++)
            {
                rows.Add(($"custglobal{i}", "0x" + f.GetCustomerGlobalByte(i).ToString("X2", CultureInfo.InvariantCulture)));
            }

            if (f.HasRecord(0x4D, 0))
            {
                for (int i = 1; i <= 4; i++)
                {
                    rows.Add(($"custnet{i}", "0x" + f.GetCustomerNetworkByte(1, i).ToString("X2", CultureInfo.InvariantCulture)));
                }
            }
        }
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
                case "squelch": f.SetSquelch(channel, Enum<Squelch>(value)); return;
                case "txinhibit": f.SetTxInhibit(channel, Enum<TxInhibit>(value)); return;
                case "network": f.SetNetwork(channel, int.Parse(value, CultureInfo.InvariantCulture)); return;
                case "txtonetype": f.SetTxSubaudibleType(channel, Enum<SubaudibleType>(value)); return;
                case "txtoneindex": f.SetTxSubaudibleIndex(channel, int.Parse(value, CultureInfo.InvariantCulture)); return;
                case "rxtonetype": f.SetRxSubaudibleType(channel, Enum<SubaudibleType>(value)); return;
                case "rxtoneindex": f.SetRxSubaudibleIndex(channel, int.Parse(value, CultureInfo.InvariantCulture)); return;
                case "rxtone": SetTone(f, channel, rx: true, value); return;
                case "txtone": SetTone(f, channel, rx: false, value); return;
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
            case "ffskmodembaud": f.FfskModemBaud = Enum<FfskModemRate>(value); return;
            case "ccdimode": f.CcdiModeAllowed = Bool(value); return;
            case "powerup": f.PowerupState = Enum<DataPowerupMode>(value); return;
            case "cmbaud": f.CommandModeBaud = Enum<FfskBaud>(value); return;
            case "hsdbaud": f.HsdBaud = Enum<FfskBaud>(value); return;
            case "ccdisdmout": f.CcdiSdmOutputEnabled = Bool(value); return;
            case "ccdiprogress": f.CcdiProgressMessageEnabled = Bool(value); return;
            case "ccdisdmtextonly": f.CcdiSdmTextOnly = Bool(value); return;
            case "textsdmindicator": f.TextSdmIndicator = Bool(value); return;
            case "textsdmackx": f.TextSdmAutoAckTransmission = Bool(value); return;
            case "textsdmackr": f.TextSdmAutoAckReception = Bool(value); return;
            case "sdmackdelayms": f.SdmAutoAckDelayMs = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "sdmwaitack": f.SdmWaitForAck = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "ignoreesc": f.IgnoreEscapeSequence = Bool(value); return;
            case "ignoresubaud": f.IgnoreSubaudibleOnData = Bool(value); return;
            // General tab
            case "openmonitor": f.OpenMonitorOnDialledCall = Bool(value); return;
            case "selcallout": f.SelcallOutputEnabled = Bool(value); return;
            case "maxframelen": f.MaximumInitialFrameLength = Bool(value); return;
            case "uartdelay": f.UartWriteDelayMs = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "txbackoffmin": f.TxBackoffTimeMinMs = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "txbackoffmax": f.TxBackoffTimeMaxMs = int.Parse(value, CultureInfo.InvariantCulture); return;
            // Serial Communications tab
            case "xon": f.XonCharacter = (byte)Num(value); return;
            case "xoff": f.XoffCharacter = (byte)Num(value); return;
            case "cmflow": f.CommandModeFlowControl = Enum<DataFlowControl>(value); return;
            case "tmflow": f.FfskTransparentFlowControl = Enum<DataFlowControl>(value); return;
            case "hsdflow": f.HsdFlowControl = Enum<DataFlowControl>(value); return;
            // RF Modems tab
            case "checkpacketlen": f.CheckPacketLength = Bool(value); return;
            case "toneblank": f.FfskToneBlanking = Bool(value); return;
            case "ffskleadin": f.FfskLeadInDelayMs = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "ffskleadout": f.FfskLeadOutDelayMs = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "widebandmodem": f.WidebandModemEnabled = Bool(value); return;
            case "layer2": f.ThsdLayer2Protocol = Enum<ThsdLayer2>(value); return;
            case "fec": f.ThsdForwardErrorCorrection = Bool(value); return;
            case "fecblocks": f.ThsdNumberOfBlocks = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "thsdleadin": f.ThsdLeadInDelayMs = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "thsdleadout": f.ThsdLeadOutDelayMs = int.Parse(value, CultureInfo.InvariantCulture); return;
            // SDM tab
            case "sdmbufoverwrite": f.SdmBufferOverwrite = Bool(value); return;
            case "sdmcallerid": f.SdmCallerId = Bool(value); return;
            // TOTAL Transparent Mode tab (IDs accept hex like FFFF or 0xFFFF, or decimal)
            case "totalservice": f.TotalService = Enum<TotalModeService>(value); return;
            case "totalradioid": f.TotalRadioId = Num(value); return;
            case "totalsystemid": f.TotalSystemId = Num(value); return;
            case "totaldestid": f.TotalDestinationId = Num(value); return;
            case "totallinkid": f.TotalLinkId = Num(value); return;
            case "unitdataidentity": f.UnitDataIdentity = value; return;
            // GPS tab
            case "gpsenabled": f.GpsEnabled = Bool(value); return;
            case "gpsport": f.GpsSerialPort = Enum<DataPort>(value); return;
            case "gpsbaud": f.GpsBaudRate = Enum<FfskBaud>(value); return;
            case "gpschanneltype": f.GpsPollResponseChannelType = Enum<GpsPollResponseChannelType>(value); return;
            case "gpschannel": f.GpsPollResponseChannel = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "gpscalloutinterval": f.GpsCalloutIntervalSeconds = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "gpsmaxcallouts": f.GpsMaxNumberOfCallouts = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "gpsconntimeout": f.GpsConnectionTimeoutSeconds = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "gpsleadin": f.GpsLeadInDelayMs = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "gpspolldelay": f.GpsPollResponseDelayMs = int.Parse(value, CultureInfo.InvariantCulture); return;
            case "gpsemergency": f.GpsSendOnEmergencyCallout = Bool(value); return;
            case "gpsdispatcher": f.GpsDispatcherAddress = value; return;
            // Customer Data tab (bytes accept hex or decimal); custnetN targets network 1
            case "custglobal1": f.SetCustomerGlobalByte(1, (byte)Num(value)); return;
            case "custglobal2": f.SetCustomerGlobalByte(2, (byte)Num(value)); return;
            case "custglobal3": f.SetCustomerGlobalByte(3, (byte)Num(value)); return;
            case "custglobal4": f.SetCustomerGlobalByte(4, (byte)Num(value)); return;
            case "custnet1": f.SetCustomerNetworkByte(1, 1, (byte)Num(value)); return;
            case "custnet2": f.SetCustomerNetworkByte(1, 2, (byte)Num(value)); return;
            case "custnet3": f.SetCustomerNetworkByte(1, 3, (byte)Num(value)); return;
            case "custnet4": f.SetCustomerNetworkByte(1, 4, (byte)Num(value)); return;
            case "rxtap": f.SetRxTapOutNode(Node(value, 'R')); return;
            case "txtap": f.SetEptt1TapInNode(Node(value, 'T')); return;
            case "tapunmute": f.TapOutUnmute = Enum<TapOutUnmute>(value); return;
            case "rxtapinverted": f.RxTapOutInverted = Bool(value); return;
            case "txtapinverted": f.Eptt1TapInInverted = Bool(value); return;
            case "audio":
                if (!string.Equals(value, "packet-defaults", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException("the only supported 'audio' value is 'packet-defaults'");
                }

                f.ApplyPacketAudioDefaults();
                return;
            case "profile":
                switch (value.ToLowerInvariant())
                {
                    case "pdn-basic": f.ApplyPdnBasic(); return;
                    case "pdn-extra": f.ApplyPdnExtra(); return;
                    default: throw new FormatException("supported profiles: pdn-basic, pdn-extra");
                }

            default: throw new FormatException($"unknown field '{name}'");
        }
    }

    private static void SetTone(CodeplugFields f, int channel, bool rx, string value)
    {
        string s = value.Trim();
        if (string.Equals(s, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (rx) { f.SetRxSubaudibleNone(channel); } else { f.SetTxSubaudibleNone(channel); }
            return;
        }

        // Accept "CTCSS 88.5" / "C88.5" / "88.5", and "DCS 023" / "D023".
        if (s.StartsWith("CTCSS", StringComparison.OrdinalIgnoreCase))
        {
            s = "C" + s[5..].Trim();
        }
        else if (s.StartsWith("DCS", StringComparison.OrdinalIgnoreCase))
        {
            s = "D" + s[3..].Trim();
        }

        if (s.Length > 1 && (s[0] is 'C' or 'c'))
        {
            double hz = double.Parse(s[1..], CultureInfo.InvariantCulture);
            if (rx) { f.SetRxCtcss(channel, hz); } else { f.SetTxCtcss(channel, hz); }
        }
        else if (s.Length > 1 && (s[0] is 'D' or 'd'))
        {
            string code = s[1..].Trim();
            if (rx) { f.SetRxDcs(channel, code); } else { f.SetTxDcs(channel, code); }
        }
        else if (s.Contains('.', StringComparison.Ordinal))
        {
            double hz = double.Parse(s, CultureInfo.InvariantCulture);
            if (rx) { f.SetRxCtcss(channel, hz); } else { f.SetTxCtcss(channel, hz); }
        }
        else
        {
            throw new FormatException($"tone must be like 'CTCSS 88.5', 'DCS 023', or 'None' (got '{value}')");
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

    // Parse an integer that is either hex (a "0x" prefix, or bare hex digits containing a letter, as the
    // CPS shows the character and TOTAL-ID fields) or plain decimal.
    private static int Num(string s)
    {
        string t = s.Trim();
        bool hex = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex)
        {
            t = t[2..];
        }
        else
        {
            foreach (char c in t)
            {
                if (c is >= 'a' and <= 'f' or >= 'A' and <= 'F') { hex = true; break; }
            }
        }

        return hex
            ? int.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(t, CultureInfo.InvariantCulture);
    }
}
