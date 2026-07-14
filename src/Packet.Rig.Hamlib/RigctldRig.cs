using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Packet.Rig.Hamlib;

/// <summary>
/// <see cref="IRigControl"/> over hamlib's NET rigctl protocol — the TCP text protocol served
/// by <c>rigctld</c> (and emulated by wfview, SDR++, GQRX, SparkSDR, skycatd, nCAT …; only real
/// rigctld is tested against today). Pure managed sockets: no libhamlib native dependency, which
/// is the pattern every surviving hamlib client ecosystem converged on — the P/Invoke lineage is
/// uniformly abandoned (see <c>docs/research/rig-control-spike.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Protocol.</b> Every command goes out in the Extended Response Protocol (<c>+</c> prefix),
/// whose replies always terminate with <c>RPRT n</c> — the only dialect with a deterministic
/// end-of-reply marker. <c>\chk_vfo</c> is probed at connect: when rigctld runs with
/// <c>--vfo</c>, commands are rewritten with a <c>currVFO</c> argument, so both server modes
/// work. Capabilities and identity come from <c>\dump_caps</c> at connect.
/// </para>
/// <para>
/// <b>Connection model.</b> One TCP connection, commands serialised through an internal gate
/// (rigctld processes a connection's commands in order; interleaving replies from concurrent
/// writers on one socket would desynchronise the stream). rigctld holds rig state server-side,
/// so the link is stateless for us: on any IO fault, timeout, or cancellation mid-command the
/// connection is dropped and the next command re-dials — same recover-by-redial shape as
/// <c>Packet.Kiss.KissTcpClient</c>. Capabilities are re-probed on re-dial only if they were
/// never obtained.
/// </para>
/// <para>
/// <b>Unkey on dispose.</b> If the last PTT command this client sent keyed the transmitter,
/// <see cref="DisposeAsync"/> makes a best-effort <c>T 0</c> before closing — a rig latched in
/// TX is a station incident (same contract as <c>Packet.Radio.IRadioControl</c>).
/// </para>
/// </remarks>
public sealed class RigctldRig : IRigControl
{
    private readonly RigctldRigOptions options;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);

    private TcpClient? tcp;
    private StreamReader? reader;
    private Stream? stream;
    private bool vfoMode;
    private bool probed;
    private bool keyedByUs;
    private bool disposed;

    private RigctldRig(RigctldRigOptions options)
    {
        this.options = options;
        time = options.TimeProvider;
    }

    /// <inheritdoc />
    public RigCapabilities Capabilities { get; private set; }

    /// <inheritdoc />
    public RigInfo Info { get; private set; } = new("Hamlib rigctld", null, null);

    /// <summary>
    /// Dial rigctld, probe VFO mode (<c>\chk_vfo</c>) and capabilities (<c>\dump_caps</c>), and
    /// return a ready client. Connection faults after this point self-heal by re-dialling on
    /// the next command.
    /// </summary>
    public static async Task<RigctldRig> ConnectAsync(
        RigctldRigOptions? options = null, CancellationToken cancellationToken = default)
    {
        var rig = new RigctldRig(options ?? new RigctldRigOptions());
        await rig.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await rig.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            rig.gate.Release();
        }

        return rig;
    }

    /// <inheritdoc />
    public async ValueTask<long> GetFrequencyAsync(CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.FrequencyGet);
        var payload = await TransactAsync("f", cancellationToken).ConfigureAwait(false);
        var value = RigctldProtocol.GetField(payload, "Frequency") ?? RigctldProtocol.BareValue(payload, "get_freq");
        return RigctldProtocol.ParseHz(value, "get_freq");
    }

    /// <inheritdoc />
    public async ValueTask SetFrequencyAsync(long frequencyHz, CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.FrequencySet);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequencyHz);
        await TransactAsync(
            string.Create(CultureInfo.InvariantCulture, $"F {frequencyHz}"),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<RigModeState> GetModeAsync(CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.ModeGet);
        var payload = await TransactAsync("m", cancellationToken).ConfigureAwait(false);

        // Extended replies label the fields; tolerate the bare two-line form for safety.
        var modeToken = RigctldProtocol.GetField(payload, "Mode")
            ?? RigctldProtocol.BareValue(payload, "get_mode");
        var passbandText = RigctldProtocol.GetField(payload, "Passband")
            ?? (payload.Count >= 2 ? payload[1].Trim() : null);

        int? passbandHz = passbandText is not null
            && int.TryParse(passbandText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pb)
            ? pb
            : null;
        return new RigModeState(RigMode.From(modeToken), passbandHz);
    }

    /// <inheritdoc />
    public async ValueTask SetModeAsync(
        RigMode mode, int? passbandHz = null, CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.ModeSet);
        if (mode.Token is null)
        {
            throw new ArgumentException("Uninitialised RigMode — use the statics or RigMode.From().", nameof(mode));
        }

        if (passbandHz is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(passbandHz), passbandHz, "Passband must be ≥ 0 Hz.");
        }

        // Passband 0 tells hamlib "the rig's default width for this mode" — the cross-backend
        // semantics IRigControl promises for null. (-1/no-change exists on the wire but can't be
        // expressed portably, so it isn't in the abstraction.)
        await TransactAsync(
            string.Create(CultureInfo.InvariantCulture, $"M {mode.Token} {passbandHz ?? 0}"),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> GetPttAsync(CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.PttGet);
        var payload = await TransactAsync("t", cancellationToken).ConfigureAwait(false);
        var value = RigctldProtocol.GetField(payload, "PTT") ?? RigctldProtocol.BareValue(payload, "get_ptt");

        // Hamlib PTT is an enum: 0 off; 1/2/3 are all transmit variants (on / mic / data).
        return value is not "0";
    }

    /// <inheritdoc />
    public async ValueTask SetPttAsync(bool transmit, CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.PttSet);
        await TransactAsync(transmit ? "T 1" : "T 0", cancellationToken).ConfigureAwait(false);
        keyedByUs = transmit;
    }

    /// <inheritdoc />
    public async ValueTask<double> ReadSwrAsync(CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.SwrMeter);
        return await ReadLevelAsync("SWR", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<double> ReadRfPowerAsync(CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.RfPowerMeter);
        return await ReadLevelAsync("RFPOWER_METER", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<double> ReadRfPowerWattsAsync(CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.RfPowerMeterWatts);
        return await ReadLevelAsync("RFPOWER_METER_WATTS", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> ReadDcdAsync(CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.DcdRead);
        var payload = await TransactAsync("\\get_dcd", cancellationToken).ConfigureAwait(false);
        var value = RigctldProtocol.GetField(payload, "DCD") ?? RigctldProtocol.BareValue(payload, "get_dcd");

        // Hamlib DCD is strictly binary on the wire: 1 = carrier present, 0 = channel clear.
        return value switch
        {
            "1" => true,
            "0" => false,
            _ => throw new RigProtocolException($"rigctld reply to 'get_dcd' had unrecognised DCD value '{value}'."),
        };
    }

    /// <inheritdoc />
    /// <remarks>Hamlib's <c>STRENGTH</c> level is calibrated dB relative to S9; the dBm value is
    /// <c>strength + </c><see cref="RigctldRigOptions.S9ReferenceDbm"/>.</remarks>
    public async ValueTask<double> ReadSignalStrengthDbmAsync(CancellationToken cancellationToken = default)
    {
        Require(RigCapabilities.SignalStrengthRead);
        var strengthDb = await ReadLevelAsync("STRENGTH", cancellationToken).ConfigureAwait(false);
        return strengthDb + options.S9ReferenceDbm;
    }

    /// <summary>
    /// Read any hamlib level by token (<c>STRENGTH</c>, <c>ALC</c>, <c>TEMP_METER</c>, …) — the
    /// escape hatch below the <see cref="IRigControl"/> common subset, same spirit as
    /// <c>TaitCcdiRadio.TransactRawAsync</c>. Token names come from <c>rigctl</c>'s
    /// <c>l ?</c>.
    /// </summary>
    public async ValueTask<double> ReadLevelAsync(string level, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        GuardWireToken(level, nameof(level));
        var payload = await TransactAsync($"l {level}", cancellationToken).ConfigureAwait(false);
        return RigctldProtocol.ParseDouble(RigctldProtocol.BareValue(payload, $"get_level {level}"), "get_level");
    }

    /// <summary>
    /// Send a raw NET-rigctl command line (without the <c>+</c> — it is added for you) and get
    /// the reply's payload lines back, echo and <c>RPRT</c> stripped. Escape hatch for
    /// everything not on the abstraction (split, VFO ops, <c>\send_morse</c> …).
    /// </summary>
    public async ValueTask<IReadOnlyList<string>> TransactRawAsync(
        string commandLine, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        foreach (var c in commandLine)
        {
            if (c is '\n' or '\r')
            {
                throw new ArgumentException("Command must be a single line.", nameof(commandLine));
            }
        }

        return await TransactAsync(commandLine, cancellationToken, injectVfo: false).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (keyedByUs && stream is not null)
        {
            try
            {
                // Direct write, bypassing the gate: dispose must not queue behind a stuck
                // command, and a torn reply no longer matters — the socket closes next.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2), time);
                var unkey = Encoding.UTF8.GetBytes(vfoMode ? "+T currVFO 0\n" : "+T 0\n");
                await stream.WriteAsync(unkey, cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Best effort only — the link may already be gone.
            }
        }

        DropConnection();
        gate.Dispose();
    }

    private void Require(RigCapabilities capability)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Capabilities.HasFlag(capability))
        {
            throw new NotSupportedException(
                $"This rig's control channel does not advertise {capability} (rig: {Info.Model ?? "unknown"}).");
        }
    }

    private static void GuardWireToken(string value, string paramName)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                throw new ArgumentException($"'{value}' is not a single wire token.", paramName);
            }
        }
    }

    /// <summary>Run one extended-protocol exchange under the gate, returning payload lines
    /// (echo and RPRT stripped). Throws typed exceptions per the <see cref="RigException"/>
    /// taxonomy; any transport-level upset drops the connection for redial.</summary>
    private async Task<IReadOnlyList<string>> TransactAsync(
        string commandLine, CancellationToken cancellationToken, bool injectVfo = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var wire = injectVfo && vfoMode ? InjectCurrVfo(commandLine) : commandLine;
            return await ExchangeAsync(wire, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>In <c>--vfo</c>-mode rigctld every rig command takes the VFO as its first
    /// argument; <c>currVFO</c> is hamlib's "whatever is selected" token.</summary>
    private static string InjectCurrVfo(string commandLine)
    {
        var space = commandLine.IndexOf(' ', StringComparison.Ordinal);
        return space < 0
            ? $"{commandLine} currVFO"
            : $"{commandLine[..space]} currVFO{commandLine[space..]}";
    }

    /// <summary>One wire round-trip: <c>+cmd\n</c> out, lines in until <c>RPRT</c>. Must be
    /// called under the gate with a live connection.</summary>
    private async Task<IReadOnlyList<string>> ExchangeAsync(string commandLine, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.CommandTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await stream!.WriteAsync(Encoding.UTF8.GetBytes($"+{commandLine}\n"), linked.Token).ConfigureAwait(false);
            await stream.FlushAsync(linked.Token).ConfigureAwait(false);

            var payload = new List<string>();
            var first = true;
            while (true)
            {
                var line = await reader!.ReadLineAsync(linked.Token).ConfigureAwait(false)
                    ?? throw Fault(new RigConnectionException("rigctld closed the connection mid-reply."));

                if (RigctldProtocol.TryParseRprt(line, out var code))
                {
                    return code == 0
                        ? payload
                        : throw new RigCommandException(
                            $"rigctld rejected '{commandLine}': {RigctldProtocol.DescribeError(code)}.", -code);
                }

                if (first)
                {
                    first = false; // echo line ("get_freq:" / "set_freq: 7074000") — drop it.
                    continue;
                }

                payload.Add(line);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw Fault(new RigTimeoutException(
                $"rigctld gave no complete reply to '{commandLine}' within {options.CommandTimeout.TotalSeconds:0.#}s."));
        }
        catch (OperationCanceledException)
        {
            // Caller cancellation mid-exchange leaves an unread reply in flight — the stream is
            // no longer alignable, so drop and let the next command redial.
            DropConnection();
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw Fault(new RigConnectionException($"Connection to rigctld failed during '{commandLine}'.", ex));
        }
    }

    /// <summary>A default-protocol (no <c>+</c>) exchange whose reply is exactly one line with
    /// no <c>RPRT</c> trailer. Only <c>\chk_vfo</c> uses this shape.</summary>
    private async Task<string> ExchangeOneLineAsync(string commandLine, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(options.CommandTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await stream!.WriteAsync(Encoding.UTF8.GetBytes($"{commandLine}\n"), linked.Token).ConfigureAwait(false);
            await stream.FlushAsync(linked.Token).ConfigureAwait(false);
            return await reader!.ReadLineAsync(linked.Token).ConfigureAwait(false)
                ?? throw Fault(new RigConnectionException("rigctld closed the connection mid-reply."));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw Fault(new RigTimeoutException(
                $"rigctld gave no reply to '{commandLine}' within {options.CommandTimeout.TotalSeconds:0.#}s."));
        }
        catch (OperationCanceledException)
        {
            DropConnection();
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw Fault(new RigConnectionException($"Connection to rigctld failed during '{commandLine}'.", ex));
        }
    }

    private RigException Fault(RigException ex)
    {
        DropConnection();
        return ex;
    }

    /// <summary>Dial + probe if not already connected. Must be called under the gate.</summary>
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (stream is not null)
        {
            return;
        }

        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(options.ConnectTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await client.ConnectAsync(options.Host, options.Port, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new RigTimeoutException(
                    $"Connecting to rigctld at {options.Host}:{options.Port} timed out after {options.ConnectTimeout.TotalSeconds:0.#}s.");
            }
            catch (SocketException ex)
            {
                throw new RigConnectionException($"Cannot connect to rigctld at {options.Host}:{options.Port}.", ex);
            }

            tcp = client;
            stream = client.GetStream();
            reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        }
        catch
        {
            client.Dispose();
            tcp = null;
            stream = null;
            reader = null;
            throw;
        }

        try
        {
            // chk_vfo goes out in the DEFAULT protocol (no '+') and is answered with exactly one
            // line and no RPRT trailer — same exchange hamlib's own netrigctl client does first.
            // The line's shape varies by server version ("0" on 4.x, "CHKVFO 0" on 3.3, and 4.x
            // echoes "ChkVFO: 0" to an extended request), hence the tolerant parse.
            vfoMode = RigctldProtocol.ParseChkVfo(
                await ExchangeOneLineAsync("\\chk_vfo", cancellationToken).ConfigureAwait(false));

            if (!probed)
            {
                var caps = await ExchangeAsync("\\dump_caps", cancellationToken).ConfigureAwait(false);
                (Capabilities, Info) = RigctldProtocol.ParseDumpCaps(caps);
                probed = true;
            }
        }
        catch
        {
            DropConnection();
            throw;
        }
    }

    private void DropConnection()
    {
        reader?.Dispose();
        tcp?.Dispose();
        reader = null;
        stream = null;
        tcp = null;
    }
}
