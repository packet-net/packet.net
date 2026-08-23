using System.Globalization;
using M0LTE.Tait.Codeplug;

namespace Packet.Node.Core.Radios.Programming;

/// <summary>What a completed programming run learned about the radio it wrote.</summary>
/// <param name="Model">The radio's product code (e.g. <c>TMAB12-B100_0201</c>).</param>
/// <param name="Serial">The radio's serial number.</param>
/// <param name="BackupPath">Where the pre-change codeplug was snapshotted, or null when no backup
/// directory was configured.</param>
/// <param name="RecordsWritten">How many codeplug records the write block committed.</param>
public sealed record TaitCodeplugWriteOutcome(
    string? Model,
    string? Serial,
    string? BackupPath,
    int RecordsWritten);

/// <summary>
/// The hardware seam of a programming run: everything that touches the radio's serial port, behind
/// one blocking call. Production is <see cref="TaitCodeplugWriter"/> over
/// <c>M0LTE.Tait.Codeplug</c>; the session tests substitute a scripted double so the whole
/// take-the-port-down / program / bring-it-back orchestration - and every path that must put the
/// port back - runs with no radio and no serial port.
/// </summary>
/// <remarks>
/// Deliberately synchronous: the codeplug library's protocol engine is lock-step blocking I/O, and
/// pretending otherwise would only add a layer of fake asynchrony. The session runs it on a
/// background thread.
/// </remarks>
internal interface ITaitCodeplugWriter
{
    /// <summary>
    /// Read the radio's codeplug, apply <paramref name="plan"/>, and write it back.
    /// </summary>
    /// <param name="devicePath">The serial device the radio's programming interface is on.</param>
    /// <param name="plan">What to write.</param>
    /// <param name="backupDirectory">Where to snapshot the pre-change codeplug, or null to skip the
    /// snapshot (no directory configured).</param>
    /// <param name="report">Progress sink: state, an optional 0..1 fraction within it, and a line
    /// of operator-facing text. Called on the calling thread, often.</param>
    /// <param name="cancellationToken">Abandons the run. Honoured up to the moment the write block
    /// opens; past that the codeplug is being modified and the write always runs to its commit.</param>
    TaitCodeplugWriteOutcome Program(
        string devicePath,
        TaitProgramPlan plan,
        string? backupDirectory,
        Action<TaitProgramState, double?, string> report,
        CancellationToken cancellationToken);
}

/// <summary>
/// The production <see cref="ITaitCodeplugWriter"/>: <c>M0LTE.Tait.Codeplug</c>'s
/// <see cref="TaitProgrammer"/> over the local serial port.
/// </summary>
/// <remarks>
/// <para>
/// The sequence is the CLI's <c>patch</c> verb: connect (which is where the operator power-cycles
/// the radio), interrogate, read the whole codeplug, snapshot it to a <c>.m8p</c> file, apply the
/// plan, write it back. Read-modify-write rather than a canned image, so everything the plan does
/// not name - the radio's identity, its audio routing, its GPS and customer-data blocks - survives.
/// </para>
/// <para>
/// The port opens at 19200, the rate the programming handshake runs at; no baud probing, because
/// the boot-latch flow only gives one power-cycle to get it right. The library's own guards stay
/// on: it refuses to write a radio whose codeplug database version is not one the field map is
/// validated against.
/// </para>
/// </remarks>
internal sealed class TaitCodeplugWriter : ITaitCodeplugWriter
{
    /// <summary>How long the connect keeps probing for the radio's boot banner. The operator has to
    /// walk to the radio and switch it off and on again, so this is generous.</summary>
    internal static readonly TimeSpan PowerCycleWait = TimeSpan.FromSeconds(90);

    /// <summary>The line rate the programming handshake runs at.</summary>
    internal const int ProgrammingBaud = 19200;

    private readonly Func<string, ISerialLine> openLine;

    /// <summary>The shared production instance (opens a real serial port).</summary>
    internal static TaitCodeplugWriter Instance { get; } = new();

    /// <summary>Create the writer. <paramref name="openLine"/> is the serial-port factory; null
    /// opens a real <see cref="SerialPortLine"/> at <see cref="ProgrammingBaud"/>.</summary>
    internal TaitCodeplugWriter(Func<string, ISerialLine>? openLine = null)
    {
        this.openLine = openLine ?? (path => new SerialPortLine(path, ProgrammingBaud));
    }

    /// <inheritdoc/>
    public TaitCodeplugWriteOutcome Program(
        string devicePath,
        TaitProgramPlan plan,
        string? backupDirectory,
        Action<TaitProgramState, double?, string> report,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(report);

        var options = new ProgrammerOptions
        {
            ConnectWaitMs = (int)PowerCycleWait.TotalMilliseconds,
        };

        using var programmer = new TaitProgrammer(openLine(devicePath), options);
        programmer.Progress += (_, p) => report(MapPhase(p.Phase), p.Fraction, p.What);

        // Connect is the power-cycle window: it probes for the boot banner until the radio answers
        // or the wait runs out. Report it before the first probe so the prompt is on the operator's
        // screen while they are still walking to the radio.
        report(TaitProgramState.PowerCycle, null, "power-cycle the radio now");
        programmer.Connect(cancellationToken);

        TaitIdentity identity = programmer.Interrogate();
        if (plan.CheckBand(identity.Model) is { } bandRefusal)
        {
            throw new InvalidOperationException(bandRefusal);
        }

        report(TaitProgramState.Reading, null, $"radio {identity.Model} s/n {identity.Serial}");
        CodeplugImage image = programmer.ReadImage(cancellationToken: cancellationToken);

        string? backupPath = SaveBackup(image, backupDirectory, identity, report);

        CodeplugFields fields = CodeplugFields.Open(image);
        plan.ApplyTo(fields);

        report(TaitProgramState.Writing, null, $"writing {plan}");
        int written = programmer.WriteImage(fields.Image, cancellationToken);

        return new TaitCodeplugWriteOutcome(identity.Model, identity.Serial, backupPath, written);
    }

    /// <summary>
    /// Snapshot the codeplug exactly as it was read, before a byte of the plan is applied - the
    /// library's first safety rail, and the only way back if a write turns out to have been a
    /// mistake. A snapshot failure is reported and the run continues: refusing to program because a
    /// disk write failed would be a worse outcome than programming without the belt.
    /// </summary>
    private static string? SaveBackup(
        CodeplugImage image, string? backupDirectory, TaitIdentity identity,
        Action<TaitProgramState, double?, string> report)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(backupDirectory);
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string serial = Sanitise(identity.Serial) is { Length: > 0 } s ? s : "unknown";
            string path = Path.Combine(backupDirectory, $"tait-{serial}-{stamp}.m8p");
            File.WriteAllText(path, image.ToM8p());
            report(TaitProgramState.Reading, null, $"pre-change codeplug saved to {path}");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            report(TaitProgramState.Reading, null, $"could not save the pre-change codeplug: {ex.Message}");
            return null;
        }
    }

    private static string Sanitise(string? value) =>
        new([.. (value ?? string.Empty).Where(char.IsAsciiLetterOrDigit)]);

    private static TaitProgramState MapPhase(ProgrammerPhase phase) => phase switch
    {
        ProgrammerPhase.WaitingForRadio => TaitProgramState.PowerCycle,
        ProgrammerPhase.Connected or ProgrammerPhase.Reading => TaitProgramState.Reading,
        _ => TaitProgramState.Writing,
    };
}
