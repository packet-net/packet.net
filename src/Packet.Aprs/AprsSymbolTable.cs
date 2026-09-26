namespace Packet.Aprs;

/// <summary>
/// The APRS symbol tables (APRS12c §21, APRS-Symbols.pdf): descriptions, and the obsolete ways of
/// giving a symbol outside the information field (generic destination addresses and source SSIDs).
/// </summary>
/// <remarks>
/// The descriptions are the short English names from the APRS symbol reference. Overlays on the
/// alternate table refine the meaning (e.g. <c>\&amp;</c> with overlay I is an IGate); the overlay
/// character is available as <see cref="AprsSymbol.Overlay"/>.
/// </remarks>
public static class AprsSymbolTable
{
    /// <summary>A short English description of the symbol, or null if it is reserved or unassigned.</summary>
    public static string? Describe(AprsSymbol symbol)
    {
        if (!symbol.IsValid)
        {
            return null;
        }

        int i = symbol.Code - '!';
        return symbol.IsPrimaryTable ? Primary[i] : Alternate[i];
    }

    /// <summary>
    /// The symbol encoded in a generic destination address, used by early trackers that sent raw
    /// NMEA (APRS12c §4, §21): <c>GPSxyz</c>, <c>SPCxyz</c> or <c>SYMxyz</c> (xy from the tables, z an
    /// optional overlay), or <c>GPSCnn</c> / <c>GPSEnn</c> (primary / alternate table, nn 01-94).
    /// Null if the address is not one of these forms.
    /// </summary>
    public static AprsSymbol? FromDestination(AprsAddress destination)
    {
        string d = destination.Base;
        if (d.Length == 6 && d.StartsWith("GPS", StringComparison.Ordinal) && d[3] is 'C' or 'E'
            && int.TryParse(d.AsSpan(4, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int nn) && nn is >= 1 and <= 94)
        {
            return new AprsSymbol(d[3] == 'C' ? '/' : '\\', (char)('!' + nn - 1));
        }

        if (d.Length is 5 or 6 && (d.StartsWith("GPS", StringComparison.Ordinal) || d.StartsWith("SPC", StringComparison.Ordinal) || d.StartsWith("SYM", StringComparison.Ordinal)))
        {
            string xy = d.Substring(3, 2);
            int primary = Array.IndexOf(PrimaryXy, xy);
            int alternate = Array.IndexOf(AlternateXy, xy);
            char? overlay = d.Length == 6 && d[5] != ' ' ? d[5] : null;
            if (primary >= 0 && overlay is null)
            {
                return new AprsSymbol('/', (char)('!' + primary));
            }

            if (alternate >= 0 && (overlay is null || AprsSymbol.IsValidTable(overlay.Value)))
            {
                return new AprsSymbol(overlay ?? '\\', (char)('!' + alternate));
            }
        }

        return null;
    }

    /// <summary>
    /// The symbol implied by a source SSID (APRS12c §21 Symbol in the Source Address SSID), for
    /// stand-alone trackers with no other way to send one: 1 ambulance, 2 bus, 3 fire truck,
    /// 4 bicycle, 5 yacht, 6 helicopter, 7 small aircraft, 8 ship, 9 car, 10 motorcycle,
    /// 11 balloon, 12 jeep, 13 RV, 14 truck, 15 van. Null for SSID 0.
    /// </summary>
    public static AprsSymbol? FromSourceSsid(int ssid) => ssid switch
    {
        1 => new AprsSymbol('/', 'a'),
        2 => new AprsSymbol('/', 'U'),
        3 => new AprsSymbol('/', 'f'),
        4 => new AprsSymbol('/', 'b'),
        5 => new AprsSymbol('/', 'Y'),
        6 => new AprsSymbol('/', 'X'),
        7 => new AprsSymbol('/', '\''),
        8 => new AprsSymbol('/', 's'),
        9 => new AprsSymbol('/', '>'),
        10 => new AprsSymbol('/', '<'),
        11 => new AprsSymbol('/', 'O'),
        12 => new AprsSymbol('/', 'j'),
        13 => new AprsSymbol('/', 'R'),
        14 => new AprsSymbol('/', 'k'),
        15 => new AprsSymbol('/', 'v'),
        _ => null,
    };

    // GPSxyz two-letter codes, index 0 = symbol code '!' (APRS12c Appendix 2).
    private static readonly string[] PrimaryXy = Codes("BB", 15, "P0", 10, "MR", 7, "PA", 26, "HS", 6, "LA", 26, "J1", 4);
    private static readonly string[] AlternateXy = Codes("OB", 15, "A0", 10, "NR", 7, "AA", 26, "DS", 6, "SA", 26, "Q1", 4);

    private static string[] Codes(params object[] runs)
    {
        var codes = new List<string>(94);
        for (int r = 0; r < runs.Length; r += 2)
        {
            string start = (string)runs[r];
            for (int i = 0; i < (int)runs[r + 1]; i++)
            {
                codes.Add($"{start[0]}{(char)(start[1] + i)}");
            }
        }

        return [.. codes];
    }

    // Generated from APRS-Symbols.pdf (wb2osz/aprsspec, 2024) by hand-checked extraction;
    // index 0 is symbol code '!'. Null means reserved / not assigned.
    private static readonly string?[] Primary =
    [
        "Police, Sheriff", // /!
        null, // /"
        "Digi (green star with white center)", // /#
        "Phone", // /$
        "DX Cluster", // /%
        "HF Gateway", // /&
        "Small Aircraft", // /'
        "Mobile Satellite Ground Station", // /(
        "Wheelchair (handicapped)", // /)
        "Snowmobile", // /*
        "Red Cross", // /+
        "Boy Scouts", // /,
        "House QTH (VHF)", // /-
        "X", // /.
        "Red Dot", // //
        "0 Circle", // /0
        "1 Circle", // /1
        "2 Circle", // /2
        "3 Circle", // /3
        "4 Circle", // /4
        "5 Circle", // /5
        "6 Circle", // /6
        "7 Circle", // /7
        "8 Circle", // /8
        "9 Circle", // /9
        "Fire", // /:
        "Campground (Portable ops)", // /;
        "Motorcycle", // /<
        "Railroad Engine", // /=
        "Car", // />
        "File Server", // /?
        "Hurricane Future Prediction", // /@
        "Aid Station", // /A
        "BBS or PBBS", // /B
        "Canoe", // /C
        null, // /D
        "Eyeball (events, etc.)", // /E
        "Farm Vehicle (Tractor)", // /F
        "Grid Square (6-character)", // /G
        "Hotel (blue bed icon)", // /H
        "TCP/IP on air network station", // /I
        null, // /J
        "School", // /K
        "PC user", // /L
        "MacAPRS", // /M
        "NTS Station", // /N
        "Balloon", // /O
        "Police", // /P
        null, // /Q
        "Recreational Vehicle", // /R
        "Space Shuttle", // /S
        "SSTV", // /T
        "Bus", // /U
        "Amateur TV", // /V
        "National Weather Service Site", // /W
        "Helicopter", // /X
        "Yacht (sail boat)", // /Y
        "WinAPRS", // /Z
        "Jogger, Human/person", // /[
        "Triangle (DF)", // /\
        "Mail/Post Office", // /]
        "Large Aircraft", // /^
        "Weather Station (blue)", // /_
        "Dish Antenna", // /`
        "Ambulance", // /a
        "Bicycle", // /b
        "Incident Command Post", // /c
        "Fire Department", // /d
        "Horse (equestrian)", // /e
        "Fire Truck", // /f
        "Glider", // /g
        "Hospital", // /h
        "IOTA (Islands on the Air)", // /i
        "Jeep", // /j
        "Truck", // /k
        "Laptop", // /l
        "Mic-E Repeater", // /m
        "Node (black bulls-eye)", // /n
        "Emergency Operations Center", // /o
        "Rover (puppy dog)", // /p
        "Grid Square shown above 128m", // /q
        "Repeater", // /r
        "Ship (power boat)", // /s
        "Truck Stop", // /t
        "Truck (18-wheeler)", // /u
        "Van", // /v
        "Water Station", // /w
        "X-APRS (Unix)", // /x
        "Yagi at QTH", // /y
        null, // /z
        null, // /{
        null, // /|
        null, // /}
        null, // /~
    ];

    private static readonly string?[] Alternate =
    [
        "Emergency", // \!
        null, // \"
        "Digi (green star)", // \#
        "Bank or ATM (green box)", // \$
        "Power Plant", // \%
        "I=IGate R=RX T=1hopTX 2=2hopTX", // \&
        "Crash (& incident sites)", // \'
        "Cloudy", // \(
        "Firenet MEO, MODIS Earth Obs.", // \)
        "Snow", // \*
        "Church", // \+
        "Girl Scouts", // \,
        "House (H=HF) (O = Op Present)", // \-
        "Ambiguous (Big Question Mark)", // \.
        "Waypoint Destination (Note 1)", // \/
        "Circle (E/I/W= IRLP/EchoLink/WIRES)", // \0
        null, // \1
        null, // \2
        null, // \3
        null, // \4
        null, // \5
        null, // \6
        null, // \7
        "802.11 or other network node", // \8
        "Gas Station (blue pump)", // \9
        "Hail", // \:
        "Park/Picnic Area", // \;
        "Advisory (one WX flag)", // \<
        "APRStt Touchtone (DTMF Users)", // \=
        "Cars & Vehicles", // \>
        "Information Kiosk (blue box with ?)", // \?
        "Hurricane/Tropical Storm", // \@
        "Box: DTMF, RFID, XO", // \A
        "Blowing Snow", // \B
        "Coast Guard", // \C
        "Drizzle", // \D
        "Smoke (& other vis codes)", // \E
        "Freezing Rain", // \F
        "Snow Shower", // \G
        "Haze", // \H
        "Rain Shower", // \I
        "Lightning", // \J
        "Kenwood HT (w)", // \K
        "Lighthouse", // \L
        "MARS (A=Army, N=Navy, F=AF)", // \M
        "Navigation Buoy", // \N
        "Rocket", // \O
        "Parking", // \P
        "Earthquake", // \Q
        "Restaurant", // \R
        "Satellite", // \S
        "Thunderstorm", // \T
        "Sunny", // \U
        "VORTAC Nav Aid", // \V
        "NWS Site", // \W
        "Pharmacy Rx", // \X
        "Radios and devices", // \Y
        null, // \Z
        "Wall Cloud", // \[
        null, // \\
        null, // \]
        "Aircraft (Shows Heading)", // \^
        "WX Station with Digi (green)", // \_
        "Rain", // \`
        "ARRL, ARES, WinLINK, Dstar, LoRa, etc", // \a
        "Blowing Dust/Sand", // \b
        "CD triangle RACES/SATERN/etc", // \c
        "DX Spot (from callsign prefix)", // \d
        "Sleet", // \e
        "Funnel Cloud", // \f
        "Gale Flags", // \g
        "Store or Hamfest", // \h
        "BOX or points of Interest", // \i
        "Work Zone (steam shovel)", // \j
        "Special Vehicle SUV, ATV, 4x4", // \k
        "Area Symbols (box, circle, etc)", // \l
        "Value Sign (3 digit display)", // \m
        "Triangle", // \n
        "Small Circle", // \o
        "Partly Cloudy", // \p
        null, // \q
        "Restrooms", // \r
        "Ship/Boat (top view)", // \s
        "Tornado", // \t
        "Truck", // \u
        "Van", // \v
        "Flooding (Avalanches/Slides)", // \w
        "Wreck or Obstruction", // \x
        "Skywarn", // \y
        "Overlayed Shelter", // \z
        "Fog", // \{
        null, // \|
        null, // \}
        null, // \~
    ];
}
