namespace Packet.Aprs;

// The named symbols, one for each symbol the APRS symbol tables define (APRS12c ch. 21,
// APRS-Symbols.pdf), so a symbol can be given by name rather than by its table and code.
// Descriptions are those of AprsSymbolTable.
public readonly partial record struct AprsSymbol
{
    // ---- primary table (/)

    /// <summary>Police, Sheriff: <c>/!</c>.</summary>
    public static AprsSymbol PoliceSheriff => new('/', '!');

    /// <summary>Digi (green star with white center): <c>/#</c>.</summary>
    public static AprsSymbol Digipeater => new('/', '#');

    /// <summary>Phone: <c>/$</c>.</summary>
    public static AprsSymbol Phone => new('/', '$');

    /// <summary>DX Cluster: <c>/%</c>.</summary>
    public static AprsSymbol DxCluster => new('/', '%');

    /// <summary>HF Gateway: <c>/&amp;</c>.</summary>
    public static AprsSymbol HfGateway => new('/', '&');

    /// <summary>Small Aircraft: <c>/'</c>.</summary>
    public static AprsSymbol SmallAircraft => new('/', '\'');

    /// <summary>Mobile Satellite Ground Station: <c>/(</c>.</summary>
    public static AprsSymbol MobileSatelliteGroundStation => new('/', '(');

    /// <summary>Wheelchair (handicapped): <c>/)</c>.</summary>
    public static AprsSymbol Wheelchair => new('/', ')');

    /// <summary>Snowmobile: <c>/*</c>.</summary>
    public static AprsSymbol Snowmobile => new('/', '*');

    /// <summary>Red Cross: <c>/+</c>.</summary>
    public static AprsSymbol RedCross => new('/', '+');

    /// <summary>Boy Scouts: <c>/,</c>.</summary>
    public static AprsSymbol BoyScouts => new('/', ',');

    /// <summary>House QTH (VHF): <c>/-</c>.</summary>
    public static AprsSymbol House => new('/', '-');

    /// <summary>X: <c>/.</c>.</summary>
    public static AprsSymbol XMark => new('/', '.');

    /// <summary>Red Dot: <c>//</c>.</summary>
    public static AprsSymbol RedDot => new('/', '/');

    /// <summary>0 Circle: <c>/0</c>.</summary>
    public static AprsSymbol Circle0 => new('/', '0');

    /// <summary>1 Circle: <c>/1</c>.</summary>
    public static AprsSymbol Circle1 => new('/', '1');

    /// <summary>2 Circle: <c>/2</c>.</summary>
    public static AprsSymbol Circle2 => new('/', '2');

    /// <summary>3 Circle: <c>/3</c>.</summary>
    public static AprsSymbol Circle3 => new('/', '3');

    /// <summary>4 Circle: <c>/4</c>.</summary>
    public static AprsSymbol Circle4 => new('/', '4');

    /// <summary>5 Circle: <c>/5</c>.</summary>
    public static AprsSymbol Circle5 => new('/', '5');

    /// <summary>6 Circle: <c>/6</c>.</summary>
    public static AprsSymbol Circle6 => new('/', '6');

    /// <summary>7 Circle: <c>/7</c>.</summary>
    public static AprsSymbol Circle7 => new('/', '7');

    /// <summary>8 Circle: <c>/8</c>.</summary>
    public static AprsSymbol Circle8 => new('/', '8');

    /// <summary>9 Circle: <c>/9</c>.</summary>
    public static AprsSymbol Circle9 => new('/', '9');

    /// <summary>Fire: <c>/:</c>.</summary>
    public static AprsSymbol Fire => new('/', ':');

    /// <summary>Campground (Portable ops): <c>/;</c>.</summary>
    public static AprsSymbol Campground => new('/', ';');

    /// <summary>Motorcycle: <c>/&lt;</c>.</summary>
    public static AprsSymbol Motorcycle => new('/', '<');

    /// <summary>Railroad Engine: <c>/=</c>.</summary>
    public static AprsSymbol RailroadEngine => new('/', '=');

    /// <summary>Car: <c>/&gt;</c>.</summary>
    public static AprsSymbol Car => new('/', '>');

    /// <summary>File Server: <c>/?</c>.</summary>
    public static AprsSymbol FileServer => new('/', '?');

    /// <summary>Hurricane Future Prediction: <c>/@</c>.</summary>
    public static AprsSymbol HurricaneFuturePrediction => new('/', '@');

    /// <summary>Aid Station: <c>/A</c>.</summary>
    public static AprsSymbol AidStation => new('/', 'A');

    /// <summary>BBS or PBBS: <c>/B</c>.</summary>
    public static AprsSymbol Bbs => new('/', 'B');

    /// <summary>Canoe: <c>/C</c>.</summary>
    public static AprsSymbol Canoe => new('/', 'C');

    /// <summary>Eyeball (events, etc.): <c>/E</c>.</summary>
    public static AprsSymbol Eyeball => new('/', 'E');

    /// <summary>Farm Vehicle (Tractor): <c>/F</c>.</summary>
    public static AprsSymbol FarmVehicle => new('/', 'F');

    /// <summary>Grid Square (6-character): <c>/G</c>.</summary>
    public static AprsSymbol GridSquare => new('/', 'G');

    /// <summary>Hotel (blue bed icon): <c>/H</c>.</summary>
    public static AprsSymbol Hotel => new('/', 'H');

    /// <summary>TCP/IP on air network station: <c>/I</c>.</summary>
    public static AprsSymbol TcpIpNetworkStation => new('/', 'I');

    /// <summary>School: <c>/K</c>.</summary>
    public static AprsSymbol School => new('/', 'K');

    /// <summary>PC user: <c>/L</c>.</summary>
    public static AprsSymbol PcUser => new('/', 'L');

    /// <summary>MacAPRS: <c>/M</c>.</summary>
    public static AprsSymbol MacAprs => new('/', 'M');

    /// <summary>NTS Station: <c>/N</c>.</summary>
    public static AprsSymbol NtsStation => new('/', 'N');

    /// <summary>Balloon: <c>/O</c>.</summary>
    public static AprsSymbol Balloon => new('/', 'O');

    /// <summary>Police: <c>/P</c>.</summary>
    public static AprsSymbol Police => new('/', 'P');

    /// <summary>Recreational Vehicle: <c>/R</c>.</summary>
    public static AprsSymbol RecreationalVehicle => new('/', 'R');

    /// <summary>Space Shuttle: <c>/S</c>.</summary>
    public static AprsSymbol SpaceShuttle => new('/', 'S');

    /// <summary>SSTV: <c>/T</c>.</summary>
    public static AprsSymbol Sstv => new('/', 'T');

    /// <summary>Bus: <c>/U</c>.</summary>
    public static AprsSymbol Bus => new('/', 'U');

    /// <summary>Amateur TV: <c>/V</c>.</summary>
    public static AprsSymbol AmateurTv => new('/', 'V');

    /// <summary>National Weather Service Site: <c>/W</c>.</summary>
    public static AprsSymbol NationalWeatherServiceSite => new('/', 'W');

    /// <summary>Helicopter: <c>/X</c>.</summary>
    public static AprsSymbol Helicopter => new('/', 'X');

    /// <summary>Yacht (sail boat): <c>/Y</c>.</summary>
    public static AprsSymbol Yacht => new('/', 'Y');

    /// <summary>WinAPRS: <c>/Z</c>.</summary>
    public static AprsSymbol WinAprs => new('/', 'Z');

    /// <summary>Jogger, Human/person: <c>/[</c>.</summary>
    public static AprsSymbol Jogger => new('/', '[');

    /// <summary>Triangle (DF): <c>/\</c>.</summary>
    public static AprsSymbol DirectionFinding => new('/', '\\');

    /// <summary>Mail/Post Office: <c>/]</c>.</summary>
    public static AprsSymbol PostOffice => new('/', ']');

    /// <summary>Large Aircraft: <c>/^</c>.</summary>
    public static AprsSymbol LargeAircraft => new('/', '^');

    /// <summary>Weather Station (blue): <c>/_</c>.</summary>
    public static AprsSymbol WeatherStation => new('/', '_');

    /// <summary>Dish Antenna: <c>/`</c>.</summary>
    public static AprsSymbol DishAntenna => new('/', '`');

    /// <summary>Ambulance: <c>/a</c>.</summary>
    public static AprsSymbol Ambulance => new('/', 'a');

    /// <summary>Bicycle: <c>/b</c>.</summary>
    public static AprsSymbol Bicycle => new('/', 'b');

    /// <summary>Incident Command Post: <c>/c</c>.</summary>
    public static AprsSymbol IncidentCommandPost => new('/', 'c');

    /// <summary>Fire Department: <c>/d</c>.</summary>
    public static AprsSymbol FireDepartment => new('/', 'd');

    /// <summary>Horse (equestrian): <c>/e</c>.</summary>
    public static AprsSymbol Horse => new('/', 'e');

    /// <summary>Fire Truck: <c>/f</c>.</summary>
    public static AprsSymbol FireTruck => new('/', 'f');

    /// <summary>Glider: <c>/g</c>.</summary>
    public static AprsSymbol Glider => new('/', 'g');

    /// <summary>Hospital: <c>/h</c>.</summary>
    public static AprsSymbol Hospital => new('/', 'h');

    /// <summary>IOTA (Islands on the Air): <c>/i</c>.</summary>
    public static AprsSymbol Iota => new('/', 'i');

    /// <summary>Jeep: <c>/j</c>.</summary>
    public static AprsSymbol Jeep => new('/', 'j');

    /// <summary>Truck: <c>/k</c>.</summary>
    public static AprsSymbol Truck => new('/', 'k');

    /// <summary>Laptop: <c>/l</c>.</summary>
    public static AprsSymbol Laptop => new('/', 'l');

    /// <summary>Mic-E Repeater: <c>/m</c>.</summary>
    public static AprsSymbol MicERepeater => new('/', 'm');

    /// <summary>Node (black bulls-eye): <c>/n</c>.</summary>
    public static AprsSymbol Node => new('/', 'n');

    /// <summary>Emergency Operations Center: <c>/o</c>.</summary>
    public static AprsSymbol EmergencyOperationsCenter => new('/', 'o');

    /// <summary>Rover (puppy dog): <c>/p</c>.</summary>
    public static AprsSymbol Rover => new('/', 'p');

    /// <summary>Grid Square shown above 128m: <c>/q</c>.</summary>
    public static AprsSymbol GridSquareAbove128m => new('/', 'q');

    /// <summary>Repeater: <c>/r</c>.</summary>
    public static AprsSymbol Repeater => new('/', 'r');

    /// <summary>Ship (power boat): <c>/s</c>.</summary>
    public static AprsSymbol Ship => new('/', 's');

    /// <summary>Truck Stop: <c>/t</c>.</summary>
    public static AprsSymbol TruckStop => new('/', 't');

    /// <summary>Truck (18-wheeler): <c>/u</c>.</summary>
    public static AprsSymbol EighteenWheeler => new('/', 'u');

    /// <summary>Van: <c>/v</c>.</summary>
    public static AprsSymbol Van => new('/', 'v');

    /// <summary>Water Station: <c>/w</c>.</summary>
    public static AprsSymbol WaterStation => new('/', 'w');

    /// <summary>X-APRS (Unix): <c>/x</c>.</summary>
    public static AprsSymbol XAprs => new('/', 'x');

    /// <summary>Yagi at QTH: <c>/y</c>.</summary>
    public static AprsSymbol YagiAtQth => new('/', 'y');

    // ---- alternate table (\)

    /// <summary>Emergency: <c>\!</c>.</summary>
    public static AprsSymbol Emergency => new('\\', '!');

    /// <summary>Digi (green star): <c>\#</c>.</summary>
    public static AprsSymbol OverlayDigipeater => new('\\', '#');

    /// <summary>Bank or ATM (green box): <c>\$</c>.</summary>
    public static AprsSymbol Bank => new('\\', '$');

    /// <summary>Power Plant: <c>\%</c>.</summary>
    public static AprsSymbol PowerPlant => new('\\', '%');

    /// <summary>I=IGate R=RX T=1hopTX 2=2hopTX: <c>\&amp;</c>.</summary>
    public static AprsSymbol Gateway => new('\\', '&');

    /// <summary>Crash (&amp; incident sites): <c>\'</c>.</summary>
    public static AprsSymbol Crash => new('\\', '\'');

    /// <summary>Cloudy: <c>\(</c>.</summary>
    public static AprsSymbol Cloudy => new('\\', '(');

    /// <summary>Firenet MEO, MODIS Earth Obs.: <c>\)</c>.</summary>
    public static AprsSymbol Firenet => new('\\', ')');

    /// <summary>Snow: <c>\*</c>.</summary>
    public static AprsSymbol Snow => new('\\', '*');

    /// <summary>Church: <c>\+</c>.</summary>
    public static AprsSymbol Church => new('\\', '+');

    /// <summary>Girl Scouts: <c>\,</c>.</summary>
    public static AprsSymbol GirlScouts => new('\\', ',');

    /// <summary>House (H=HF) (O = Op Present): <c>\-</c>.</summary>
    public static AprsSymbol OverlayHouse => new('\\', '-');

    /// <summary>Ambiguous (Big Question Mark): <c>\.</c>.</summary>
    public static AprsSymbol Ambiguous => new('\\', '.');

    /// <summary>Waypoint Destination (Note 1): <c>\/</c>.</summary>
    public static AprsSymbol Waypoint => new('\\', '/');

    /// <summary>Circle (E/I/W= IRLP/EchoLink/WIRES): <c>\0</c>.</summary>
    public static AprsSymbol OverlayCircle => new('\\', '0');

    /// <summary>802.11 or other network node: <c>\8</c>.</summary>
    public static AprsSymbol NetworkNode => new('\\', '8');

    /// <summary>Gas Station (blue pump): <c>\9</c>.</summary>
    public static AprsSymbol GasStation => new('\\', '9');

    /// <summary>Hail: <c>\:</c>.</summary>
    public static AprsSymbol Hail => new('\\', ':');

    /// <summary>Park/Picnic Area: <c>\;</c>.</summary>
    public static AprsSymbol Park => new('\\', ';');

    /// <summary>Advisory (one WX flag): <c>\&lt;</c>.</summary>
    public static AprsSymbol Advisory => new('\\', '<');

    /// <summary>APRStt Touchtone (DTMF Users): <c>\=</c>.</summary>
    public static AprsSymbol Aprstt => new('\\', '=');

    /// <summary>Cars &amp; Vehicles: <c>\&gt;</c>.</summary>
    public static AprsSymbol OverlayVehicle => new('\\', '>');

    /// <summary>Information Kiosk (blue box with ?): <c>\?</c>.</summary>
    public static AprsSymbol InformationKiosk => new('\\', '?');

    /// <summary>Hurricane/Tropical Storm: <c>\@</c>.</summary>
    public static AprsSymbol HurricaneTropicalStorm => new('\\', '@');

    /// <summary>Box: DTMF, RFID, XO: <c>\A</c>.</summary>
    public static AprsSymbol OverlayBox => new('\\', 'A');

    /// <summary>Blowing Snow: <c>\B</c>.</summary>
    public static AprsSymbol BlowingSnow => new('\\', 'B');

    /// <summary>Coast Guard: <c>\C</c>.</summary>
    public static AprsSymbol CoastGuard => new('\\', 'C');

    /// <summary>Drizzle: <c>\D</c>.</summary>
    public static AprsSymbol Drizzle => new('\\', 'D');

    /// <summary>Smoke (&amp; other vis codes): <c>\E</c>.</summary>
    public static AprsSymbol Smoke => new('\\', 'E');

    /// <summary>Freezing Rain: <c>\F</c>.</summary>
    public static AprsSymbol FreezingRain => new('\\', 'F');

    /// <summary>Snow Shower: <c>\G</c>.</summary>
    public static AprsSymbol SnowShower => new('\\', 'G');

    /// <summary>Haze: <c>\H</c>.</summary>
    public static AprsSymbol Haze => new('\\', 'H');

    /// <summary>Rain Shower: <c>\I</c>.</summary>
    public static AprsSymbol RainShower => new('\\', 'I');

    /// <summary>Lightning: <c>\J</c>.</summary>
    public static AprsSymbol Lightning => new('\\', 'J');

    /// <summary>Kenwood HT (w): <c>\K</c>.</summary>
    public static AprsSymbol KenwoodHt => new('\\', 'K');

    /// <summary>Lighthouse: <c>\L</c>.</summary>
    public static AprsSymbol Lighthouse => new('\\', 'L');

    /// <summary>MARS (A=Army, N=Navy, F=AF): <c>\M</c>.</summary>
    public static AprsSymbol Mars => new('\\', 'M');

    /// <summary>Navigation Buoy: <c>\N</c>.</summary>
    public static AprsSymbol NavigationBuoy => new('\\', 'N');

    /// <summary>Rocket: <c>\O</c>.</summary>
    public static AprsSymbol Rocket => new('\\', 'O');

    /// <summary>Parking: <c>\P</c>.</summary>
    public static AprsSymbol Parking => new('\\', 'P');

    /// <summary>Earthquake: <c>\Q</c>.</summary>
    public static AprsSymbol Earthquake => new('\\', 'Q');

    /// <summary>Restaurant: <c>\R</c>.</summary>
    public static AprsSymbol Restaurant => new('\\', 'R');

    /// <summary>Satellite: <c>\S</c>.</summary>
    public static AprsSymbol Satellite => new('\\', 'S');

    /// <summary>Thunderstorm: <c>\T</c>.</summary>
    public static AprsSymbol Thunderstorm => new('\\', 'T');

    /// <summary>Sunny: <c>\U</c>.</summary>
    public static AprsSymbol Sunny => new('\\', 'U');

    /// <summary>VORTAC Nav Aid: <c>\V</c>.</summary>
    public static AprsSymbol Vortac => new('\\', 'V');

    /// <summary>NWS Site: <c>\W</c>.</summary>
    public static AprsSymbol OverlayNwsSite => new('\\', 'W');

    /// <summary>Pharmacy Rx: <c>\X</c>.</summary>
    public static AprsSymbol Pharmacy => new('\\', 'X');

    /// <summary>Radios and devices: <c>\Y</c>.</summary>
    public static AprsSymbol OverlayRadio => new('\\', 'Y');

    /// <summary>Wall Cloud: <c>\[</c>.</summary>
    public static AprsSymbol WallCloud => new('\\', '[');

    /// <summary>Aircraft (Shows Heading): <c>\^</c>.</summary>
    public static AprsSymbol AircraftWithHeading => new('\\', '^');

    /// <summary>WX Station with Digi (green): <c>\_</c>.</summary>
    public static AprsSymbol WeatherStationWithDigipeater => new('\\', '_');

    /// <summary>Rain: <c>\`</c>.</summary>
    public static AprsSymbol Rain => new('\\', '`');

    /// <summary>ARRL, ARES, WinLINK, Dstar, LoRa, etc: <c>\a</c>.</summary>
    public static AprsSymbol OverlayDiamond => new('\\', 'a');

    /// <summary>Blowing Dust/Sand: <c>\b</c>.</summary>
    public static AprsSymbol BlowingDust => new('\\', 'b');

    /// <summary>CD triangle RACES/SATERN/etc: <c>\c</c>.</summary>
    public static AprsSymbol OverlayCivilDefense => new('\\', 'c');

    /// <summary>DX Spot (from callsign prefix): <c>\d</c>.</summary>
    public static AprsSymbol DxSpot => new('\\', 'd');

    /// <summary>Sleet: <c>\e</c>.</summary>
    public static AprsSymbol Sleet => new('\\', 'e');

    /// <summary>Funnel Cloud: <c>\f</c>.</summary>
    public static AprsSymbol FunnelCloud => new('\\', 'f');

    /// <summary>Gale Flags: <c>\g</c>.</summary>
    public static AprsSymbol GaleFlags => new('\\', 'g');

    /// <summary>Store or Hamfest: <c>\h</c>.</summary>
    public static AprsSymbol Store => new('\\', 'h');

    /// <summary>BOX or points of Interest: <c>\i</c>.</summary>
    public static AprsSymbol PointOfInterest => new('\\', 'i');

    /// <summary>Work Zone (steam shovel): <c>\j</c>.</summary>
    public static AprsSymbol WorkZone => new('\\', 'j');

    /// <summary>Special Vehicle SUV, ATV, 4x4: <c>\k</c>.</summary>
    public static AprsSymbol SpecialVehicle => new('\\', 'k');

    /// <summary>Area Symbols (box, circle, etc): <c>\l</c>.</summary>
    public static AprsSymbol Area => new('\\', 'l');

    /// <summary>Value Sign (3 digit display): <c>\m</c>.</summary>
    public static AprsSymbol ValueSign => new('\\', 'm');

    /// <summary>Triangle: <c>\n</c>.</summary>
    public static AprsSymbol Triangle => new('\\', 'n');

    /// <summary>Small Circle: <c>\o</c>.</summary>
    public static AprsSymbol SmallCircle => new('\\', 'o');

    /// <summary>Partly Cloudy: <c>\p</c>.</summary>
    public static AprsSymbol PartlyCloudy => new('\\', 'p');

    /// <summary>Restrooms: <c>\r</c>.</summary>
    public static AprsSymbol Restrooms => new('\\', 'r');

    /// <summary>Ship/Boat (top view): <c>\s</c>.</summary>
    public static AprsSymbol OverlayShip => new('\\', 's');

    /// <summary>Tornado: <c>\t</c>.</summary>
    public static AprsSymbol Tornado => new('\\', 't');

    /// <summary>Truck: <c>\u</c>.</summary>
    public static AprsSymbol OverlayTruck => new('\\', 'u');

    /// <summary>Van: <c>\v</c>.</summary>
    public static AprsSymbol OverlayVan => new('\\', 'v');

    /// <summary>Flooding (Avalanches/Slides): <c>\w</c>.</summary>
    public static AprsSymbol Flooding => new('\\', 'w');

    /// <summary>Wreck or Obstruction: <c>\x</c>.</summary>
    public static AprsSymbol Wreck => new('\\', 'x');

    /// <summary>Skywarn: <c>\y</c>.</summary>
    public static AprsSymbol Skywarn => new('\\', 'y');

    /// <summary>Overlayed Shelter: <c>\z</c>.</summary>
    public static AprsSymbol Shelter => new('\\', 'z');

    /// <summary>Fog: <c>\{</c>.</summary>
    public static AprsSymbol Fog => new('\\', '{');
}
