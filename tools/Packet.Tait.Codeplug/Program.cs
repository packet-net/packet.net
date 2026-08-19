// Tait TM8100/TM8200 codeplug programmer - spike-grade Linux CLI.
//
// Reverse-engineered from Free Serial Analyzer captures of the Windows CPS (see
// tait-programming-research/FINDINGS.md). The programming protocol is ASCII-hex, line-oriented,
// CR-terminated, strictly lock-step; records share the .m8p framing. The radio must be latched
// into programming mode first: power-cycle it as the operation is triggered. No RF is involved.
//
// Offline verbs (no radio):
//   parse <file.m8p>              verify every record checksum + print the section map
//   dump  <file.m8p>              decode the identity + known fields
//
// Hardware verbs (radio in programming mode on <port>):
//   version <port> [--baud N]                 interrogate: model / firmware / serial
//   read    <port> <out.m8p> [--baud N]       read the whole codeplug to a file
//   write   <port> <in.m8p>  [--baud N]       program a codeplug (auto-backs-up first)
//
// GOLDEN RULES (docs/research/tait-codeplug-programming-brief.md): always back up before a write
// (write does this), never touch firmware (this only writes the codeplug region), version-pin on
// DBVer, and bench on a sacrificial radio first.

using System.Globalization;
using Packet.Tait.Codeplug;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0])
    {
        case "parse":
            return CmdParse(Arg(args, 1));
        case "dump":
            return CmdDump(Arg(args, 1));
        case "get":
            return CmdGet(Arg(args, 1), args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null);
        case "set":
            return CmdSet(Arg(args, 1), Arg(args, 2), Arg(args, 3));
        case "patch":
            return CmdPatch(Arg(args, 1), Arg(args, 2), Arg(args, 3), Baud(args), HasFlag(args, "--restore"));
        case "version":
            return CmdVersion(Arg(args, 1), Baud(args));
        case "read":
            return CmdRead(Arg(args, 1), Arg(args, 2), Baud(args));
        case "write":
            return CmdWrite(Arg(args, 1), Arg(args, 2), Baud(args), HasFlag(args, "--yes"));
        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex) when (ex is FormatException or IOException or TimeoutException or InvalidOperationException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static int CmdParse(string path)
{
    CodeplugImage image = CodeplugImage.LoadM8p(File.ReadAllText(path));
    Console.WriteLine($"header: {string.Join(", ", image.Header.Select(kv => $"{kv.Key}={kv.Value}"))}");
    Console.WriteLine($"records: {image.Records.Count} (all checksums verified on load)");
    Console.WriteLine($"sections: {image.SectionMap().Count}");
    Console.WriteLine($"{"sec",4} {"#recs",6} {"databytes",10}");
    foreach ((byte section, int count, int bytes) in image.SectionMap())
    {
        Console.WriteLine($"0x{section:X2} {count,6} {bytes,10}");
    }

    return 0;
}

static int CmdDump(string path)
{
    CodeplugImage image = CodeplugImage.LoadM8p(File.ReadAllText(path));
    Console.WriteLine($"DBVer (header): {image.DatabaseVersion}");
    Console.WriteLine($"DBVer (radio):  {image.DatabaseVersionFromRecord}");
    if (!CodeplugFields.IsSupported(image))
    {
        Console.WriteLine("(field map not available for this database version)");
        return 0;
    }

    CodeplugFields fields = CodeplugFields.Open(image);
    foreach ((string name, string value) in FieldConsole.Describe(fields))
    {
        Console.WriteLine($"  {name,-16} {value}");
    }

    return 0;
}

static int CmdGet(string path, string? field)
{
    CodeplugImage image = CodeplugImage.LoadM8p(File.ReadAllText(path));
    CodeplugFields fields = CodeplugFields.Open(image);
    if (field is null)
    {
        foreach ((string name, string value) in FieldConsole.Describe(fields))
        {
            Console.WriteLine($"{name}={value}");
        }
    }
    else
    {
        Console.WriteLine(FieldConsole.Get(fields, field));
    }

    return 0;
}

static int CmdSet(string path, string field, string value)
{
    CodeplugImage image = CodeplugImage.LoadM8p(File.ReadAllText(path));
    CodeplugFields fields = CodeplugFields.Open(image);
    string before = FieldConsole.Get(fields, field);
    FieldConsole.Set(fields, field, value);
    string after = FieldConsole.Get(fields, field);
    File.WriteAllText(path, image.ToM8p());
    Console.WriteLine($"{field}: {before} -> {after}  (saved {path})");
    return 0;
}

static int CmdVersion(string port, int baud)
{
    using var programmer = new TaitProgrammer(new SerialPortLine(port, baud), HardwareOptions(baud));
    Console.WriteLine($"opening {port} at {baud} 8N1; POWER-CYCLE THE RADIO NOW to latch programming mode...");
    TaitIdentity id = programmer.Interrogate();
    Console.WriteLine("identity:");
    Console.WriteLine($"  {id}");
    return 0;
}

static ProgrammerOptions HardwareOptions(int baud) => new()
{
    ConnectWaitMs = 90_000, // wait up to 90s for the operator to power-cycle into programming mode
    ProbeBauds = baud == 19200 ? [19200, 9600] : [baud, baud == 9600 ? 19200 : 9600],
};

static int CmdRead(string port, string outPath, int baud)
{
    using var programmer = new TaitProgrammer(new SerialPortLine(port, baud), HardwareOptions(baud));
    Console.WriteLine($"opening {port} at {baud} 8N1; POWER-CYCLE THE RADIO NOW to latch programming mode...");
    CodeplugImage image = programmer.ReadImage();
    File.WriteAllText(outPath, image.ToM8p());
    Console.WriteLine($"wrote {image.Records.Count} records to {outPath}");
    return 0;
}

static int CmdWrite(string port, string inPath, int baud, bool yes)
{
    CodeplugImage image = CodeplugImage.LoadM8p(File.ReadAllText(inPath));
    Console.WriteLine($"loaded {image.Records.Count} records from {inPath} (DBVer {image.DatabaseVersion})");

    using var programmer = new TaitProgrammer(new SerialPortLine(port, baud), HardwareOptions(baud));
    Console.WriteLine($"opening {port} at {baud} 8N1; POWER-CYCLE THE RADIO NOW to latch programming mode...");

    // Golden rule 1: always snapshot the current codeplug before writing.
    string backup = $"{inPath}.pre-write-backup-{DateTime.UtcNow:yyyyMMddHHmmss}.m8p";
    Console.WriteLine("backing up the current codeplug first...");
    File.WriteAllText(backup, programmer.ReadImage().ToM8p());
    Console.WriteLine($"backup saved to {backup}");

    if (yes)
    {
        Console.WriteLine($"--yes given: writing {image.Records.Count} records to {port}...");
    }
    else
    {
        Console.Write($"about to WRITE {image.Records.Count} records to the radio on {port}. Type 'yes' to proceed: ");
        if (Console.ReadLine()?.Trim() != "yes")
        {
            Console.WriteLine("aborted; no write performed.");
            return 0;
        }
    }

    int written = programmer.WriteImage(image);
    Console.WriteLine($"wrote {written} records. Re-read to verify before trusting.");
    return 0;
}

static int CmdPatch(string port, string field, string value, int baud, bool restore)
{
    using var programmer = new TaitProgrammer(new SerialPortLine(port, baud), HardwareOptions(baud));
    Console.WriteLine($"opening {port} at {baud} 8N1; POWER-CYCLE THE RADIO NOW to latch programming mode...");

    CodeplugImage image = programmer.ReadImage();
    var snapshot = image.Records.ToDictionary(r => (r.Section, r.Index), r => (byte[])r.Data.Clone());
    CodeplugFields fields = CodeplugFields.Open(image);

    string before = FieldConsole.Get(fields, field);
    FieldConsole.Set(fields, field, value);
    string after = FieldConsole.Get(fields, field);

    var changed = image.Records
        .Where(r => !r.Data.AsSpan().SequenceEqual(snapshot[(r.Section, r.Index)]))
        .ToList();
    if (changed.Count == 0)
    {
        Console.WriteLine($"{field} is already {value}; nothing to write.");
        return 0;
    }

    Console.WriteLine($"{field}: {before} -> {after}");
    Console.WriteLine($"writing {changed.Count} changed record(s): " +
        string.Join(", ", changed.Select(r => $"0x{r.Section:X2}/{r.Index}")));
    programmer.WriteRecords(changed);

    string reread = FieldConsole.Get(CodeplugFields.Open(programmer.ReadImage()), field);
    Console.WriteLine($"read back: {field} = {reread}  [{(reread == after ? "OK" : "MISMATCH")}]");

    if (restore)
    {
        var originals = changed
            .Select(r => new CodeplugRecord(r.Section, r.Index, snapshot[(r.Section, r.Index)]))
            .ToList();
        programmer.WriteRecords(originals);
        string restored = FieldConsole.Get(CodeplugFields.Open(programmer.ReadImage()), field);
        Console.WriteLine($"restored: {field} = {restored}  [{(restored == before ? "OK" : "MISMATCH")}]");
    }

    return 0;
}

static string Arg(string[] args, int index)
{
    if (index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
    {
        throw new FormatException($"missing argument #{index}");
    }

    return args[index];
}

static bool HasFlag(string[] args, string flag) => Array.Exists(args, a => a == flag);

static int Baud(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--baud")
        {
            return int.Parse(args[i + 1], CultureInfo.InvariantCulture);
        }
    }

    return 19200; // programming transfer rate (session opens at 9600 then switches to 19200)
}

static void PrintUsage()
{
    Console.WriteLine("usage:");
    Console.WriteLine("  parse   <file.m8p>                     verify checksums + section map");
    Console.WriteLine("  dump    <file.m8p>                     decode every mapped field");
    Console.WriteLine("  get     <file.m8p> [field]             read one field (or all as name=value)");
    Console.WriteLine("  set     <file.m8p> <field> <value>     set one field and save (e.g. ch0.bandwidth Wide)");
    Console.WriteLine("  version <port> [--baud N]              interrogate a radio");
    Console.WriteLine("  read    <port> <out.m8p> [--baud N]    read the codeplug");
    Console.WriteLine("  write   <port> <in.m8p>  [--baud N]    program the codeplug (backs up first)");
    Console.WriteLine("  patch   <port> <field> <value> [--restore]  live-set one field (writes only the changed record)");
    Console.WriteLine();
    Console.WriteLine("the radio must be latched into programming mode (power-cycle as you trigger).");
}
