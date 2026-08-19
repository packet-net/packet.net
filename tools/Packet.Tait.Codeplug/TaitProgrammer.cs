using System.Globalization;
using System.Text;

namespace Packet.Tait.Codeplug;

/// <summary>Tunables for the programming session.</summary>
public sealed class ProgrammerOptions
{
    /// <summary>The write-block init argument sent as <c>i&lt;arg&gt;</c>. The captured value is
    /// <c>53380146</c>; its derivation (range, size, or unlock key) is not yet mapped, so it is
    /// replayed verbatim by default.</summary>
    public string WriteInitArg { get; init; } = "53380146";

    /// <summary>Overall deadline for a single command's response, in milliseconds.</summary>
    public int TransactionTimeoutMs { get; init; } = 5000;

    /// <summary>How long <see cref="TaitProgrammer.Connect"/> keeps retrying the reset probe while
    /// waiting for the radio to boot into programming mode. The boot-latch flow triggers the read
    /// first and powers the radio on second, so the tool must poll through the boot window. 0 means
    /// a single attempt (the default, for the scripted mock).</summary>
    public int ConnectWaitMs { get; init; }

    /// <summary>Per-attempt read wait for the reset probe during <see cref="TaitProgrammer.Connect"/>.</summary>
    public int ProbeTimeoutMs { get; init; } = 400;

    /// <summary>Baud rates to cycle through while probing for the radio during
    /// <see cref="TaitProgrammer.Connect"/>. The capture showed the CP2102 set to 9600 then 19200,
    /// so which rate the boot handshake wants is unconfirmed; probing both within one connect window
    /// avoids spending a power-cycle to guess. Empty = leave the port at its opened rate.</summary>
    public IReadOnlyList<int> ProbeBauds { get; init; } = [];
}

/// <summary>
/// Drives the Tait TM8100/TM8200 boot-time programming protocol over an <see cref="ISerialLine"/>.
///
/// The protocol is ASCII-hex, line-oriented, CR-terminated, strictly lock-step: every command is
/// answered by a single <c>&gt;</c> prompt before the next is sent. Records use the
/// <c>&lt;addr&gt;&lt;len&gt;&lt;data&gt;&lt;checksum&gt;</c> framing of <see cref="CodeplugRecord"/>.
/// Session open is <c>^</c> (reset, replies <c>v</c>) then <c>#</c> (enter programming, replies a
/// prompt); then <c>ld</c> and <c>d00</c> handshake. Read is <c>r&lt;section&gt;</c>. Write is
/// <c>b</c>, <c>i&lt;arg&gt;</c>, a run of <c>w&lt;record&gt;</c>, then <c>e</c>. Teardown is <c>^</c>.
///
/// The radio must already be latched into programming mode (power-cycle it as the operation is
/// triggered). All work is over the data connector with no RF.
/// </summary>
public sealed class TaitProgrammer : IDisposable
{
    private const byte Prompt = (byte)'>';
    private const byte Banner = (byte)'v';

    private readonly ISerialLine _line;
    private readonly ProgrammerOptions _options;
    private readonly byte[] _rx = new byte[512];
    private int _rxLen;
    private int _rxPos;
    private bool _connected;

    /// <summary>Wrap an open serial line.</summary>
    public TaitProgrammer(ISerialLine line, ProgrammerOptions? options = null)
    {
        _line = line ?? throw new ArgumentNullException(nameof(line));
        _options = options ?? new ProgrammerOptions();
    }

    /// <summary>Enter programming mode and complete the <c>ld</c>/<c>d00</c> handshake. Idempotent.</summary>
    public void Connect()
    {
        if (_connected)
        {
            return;
        }

        // Boot-latch: the read is triggered first and the radio powered on second, so retry the
        // reset probe through the boot window until the radio answers with its 'v' banner.
        long deadline = Environment.TickCount64 + _options.ConnectWaitMs;
        int attempt = 0;
        while (true)
        {
            try
            {
                if (_options.ProbeBauds.Count > 0)
                {
                    _line.SetBaudRate(_options.ProbeBauds[attempt % _options.ProbeBauds.Count]);
                }

                attempt++;
                SendBare("^"); // reset; radio answers with the 'v' banner
                DrainUntil(Banner, _options.ProbeTimeoutMs);
                break;
            }
            catch (TimeoutException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw new TimeoutException(
                        "radio did not enter programming mode; power-cycle it as the read is triggered");
                }
            }
        }

        SendBare("#"); // enter programming mode
        DrainUntil(Prompt, _options.TransactionTimeoutMs);
        Transact("ld"); // login/version; radio answers {Cxx}
        Transact("d00"); // select database
        _connected = true;
    }

    /// <summary>Read the identity block plus the two status sections the CPS interrogate pulls.</summary>
    public TaitIdentity Interrogate()
    {
        Connect();
        IReadOnlyList<CodeplugRecord> id = ReadSection(0x00);
        Transact("p01");
        ReadSection(0x27);
        Transact("p00");
        ReadSection(0x2F);
        return TaitIdentity.FromSectionZero(id);
    }

    /// <summary>Read the given sections and assemble a <see cref="CodeplugImage"/>. If
    /// <paramref name="sections"/> is null, the standard set observed in a full CPS read is used.</summary>
    public CodeplugImage ReadImage(IReadOnlyList<byte>? sections = null)
    {
        Connect();
        sections ??= DefaultReadSections;
        var records = new List<CodeplugRecord>();
        foreach (byte s in sections)
        {
            records.AddRange(ReadSection(s));
        }

        TaitIdentity id = TaitIdentity.FromSectionZero(records.Where(r => r.Section == 0).ToList());
        var header = new List<KeyValuePair<string, string>>
        {
            new("Radio", "TM8000"),
            new("Model", id.Model ?? ""),
            new("Firmware", id.Firmware ?? ""),
            new("Serial", id.Serial ?? ""),
        };
        return new CodeplugImage(header, records);
    }

    /// <summary>Read one section: issue <c>r&lt;section&gt;</c> and parse the records it streams.</summary>
    public IReadOnlyList<CodeplugRecord> ReadSection(byte section)
    {
        Connect();
        string body = Transact("r" + section.ToString("X2", CultureInfo.InvariantCulture));
        return ParseRecords(body);
    }

    /// <summary>
    /// Write an image: the CPS write preamble, then the write block (<c>b</c>, <c>i&lt;arg&gt;</c>,
    /// a <c>w&lt;record&gt;</c> per record awaiting each prompt, <c>e</c>). Section 0 (the read-only
    /// identity) is skipped, matching the CPS. Returns the number of records written.
    /// </summary>
    public int WriteImage(CodeplugImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return WriteRecords(image.Records);
    }

    /// <summary>
    /// Write a subset of records in one write block (<c>b</c>, <c>i&lt;arg&gt;</c>, a
    /// <c>w&lt;record&gt;</c> per record awaiting each prompt, <c>e</c>) preceded by the CPS write
    /// preamble. Because there is no whole-codeplug checksum, a single changed record can be written
    /// in place without rewriting the rest. Section 0 (the read-only identity) is always skipped.
    /// Returns the number of records written.
    /// </summary>
    public int WriteRecords(IEnumerable<CodeplugRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        Connect();

        var toWrite = records.Where(r => r.Section != 0x00).ToList();
        if (toWrite.Count == 0)
        {
            return 0;
        }

        // Faithful preamble reads (harmless, and what the CPS does before a write).
        ReadSection(0x00);
        Transact("p01");
        ReadSection(0x27);
        Transact("p00");
        ReadSection(0x2F);
        Transact("p01");
        ReadSection(0x22);

        Transact("b"); // begin
        Transact("i" + _options.WriteInitArg); // init/unlock

        foreach (CodeplugRecord r in toWrite)
        {
            Transact("w" + r.ToWireLine());
        }

        Transact("e"); // end/commit
        return toWrite.Count;
    }

    /// <summary>Leave programming mode (<c>^</c>), best-effort.</summary>
    public void Close()
    {
        if (!_connected)
        {
            return;
        }

        try
        {
            SendBare("^");
            DrainUntil(Banner, _options.TransactionTimeoutMs);
        }
        catch (TimeoutException)
        {
            // teardown is best-effort
        }

        _connected = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Close();
        _line.Dispose();
    }

    /// <summary>Sections seen in a full CPS read, in order.</summary>
    public static IReadOnlyList<byte> DefaultReadSections { get; } = new byte[]
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x07, 0x09, 0x0A, 0x0F, 0x11, 0x14, 0x15, 0x16,
        0x18, 0x19, 0x1E, 0x22, 0x24, 0x26, 0x27, 0x2C, 0x32, 0x33, 0x34, 0x35, 0x37, 0x38,
        0x39, 0x3A, 0x3B, 0x3C, 0x41, 0x43, 0x44, 0x45, 0x48, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E,
        0x52, 0x53,
    };

    private static List<CodeplugRecord> ParseRecords(string body)
    {
        var records = new List<CodeplugRecord>();
        foreach (string line in body.Split('\r'))
        {
            string t = line.Trim();
            if (t.Length == 0 || !Uri.IsHexDigit(t[0]))
            {
                continue; // skip prompts, {Cxx} acks, blanks
            }

            records.Add(CodeplugRecord.Parse(t));
        }

        return records;
    }

    /// <summary>Send a command (CR-appended) and return the response up to, but excluding, the
    /// trailing prompt.</summary>
    private string Transact(string command)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(command + "\r");
        _line.Write(bytes, 0, bytes.Length);
        return DrainUntil(Prompt, _options.TransactionTimeoutMs);
    }

    private void SendBare(string command)
    {
        if (command.Length == 0)
        {
            return;
        }

        byte[] bytes = Encoding.ASCII.GetBytes(command);
        _line.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Read bytes until <paramref name="terminator"/> is seen, returning everything before
    /// it as ASCII. Enforces <paramref name="timeoutMs"/> as the overall deadline.</summary>
    private string DrainUntil(byte terminator, int timeoutMs)
    {
        var sb = new StringBuilder();
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            byte b = ReadByte(deadline);
            if (b == terminator)
            {
                return sb.ToString();
            }

            sb.Append((char)b);
        }
    }

    private byte ReadByte(long deadline)
    {
        while (_rxPos >= _rxLen)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("no programming response within the transaction deadline");
            }

            try
            {
                _rxLen = _line.Read(_rx, 0, _rx.Length);
                _rxPos = 0;
            }
            catch (TimeoutException)
            {
                // keep waiting until the overall deadline
            }
        }

        return _rx[_rxPos++];
    }
}
