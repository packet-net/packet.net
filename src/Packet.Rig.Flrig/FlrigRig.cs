using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Packet.Rig.Flrig;

/// <summary>
/// <see cref="IRigControl"/> over flrig's XML-RPC server (default 127.0.0.1:12345). The client
/// contract here deliberately mirrors hamlib's own flrig backend (<c>rigs/dummy/flrig.c</c>) —
/// the most battle-tested flrig client in existence — including its meter conversions and its
/// try-<c>rig.get_SWR</c>-then-fall-back probing.
/// </summary>
/// <remarks>
/// <para>
/// <b>flrig's dialect quirks, designed around rather than fought:</b> frequency <em>gets</em>
/// return strings of Hz while <em>sets</em> take doubles; mode names are whatever the attached
/// transceiver calls them (<see cref="SupportedModes"/> enumerates the valid table, fetched at
/// connect via <c>rig.get_modes</c>); the get-side reports no passband width; meters are 0–100
/// needle deflections scaled by <c>rig.get_pwrmeter_scale</c>; and SWR needs interpolation
/// unless the newer direct <c>rig.get_SWR</c> method exists. flrig is poll-only — no
/// notifications — and supports multiple concurrent clients, so state can change under you.
/// </para>
/// <para>
/// <b>Capabilities</b> are static: presenting freq/mode/PTT and the meter methods is flrig's
/// job regardless of rig. A transceiver without a power/SWR meter simply reads 0 — flrig
/// answers anyway. (Hamlib's backend advertises exactly the same unconditional set.) Note the
/// server-side "Ignore xmlrpc mode changes" option makes flrig silently drop mode sets.
/// </para>
/// <para>
/// <b>Transport:</b> one HTTP POST per command, serialised through an internal gate so calls
/// from concurrent callers keep arrival order. HTTP is per-request, so there is no connection
/// state to heal — a dead flrig surfaces as <see cref="RigConnectionException"/> on each call
/// and recovery is automatic when it returns.
/// </para>
/// </remarks>
public sealed class FlrigRig : IRigControl
{
    private readonly FlrigRigOptions options;
    private readonly TimeProvider time;
    private readonly HttpClient http;
    private readonly SemaphoreSlim gate = new(1, 1);

    private double powerMeterScale = 1.0;
    private bool tryGetSwr = true; // probe rig.get_SWR once; revert to get_swrmeter interpolation
    private bool keyedByUs;
    private bool disposed;

    private FlrigRig(FlrigRigOptions options, HttpMessageHandler? handler)
    {
        this.options = options;
        time = options.TimeProvider;
        http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        // Timeouts are per-command CTSes on our TimeProvider (testable), not HttpClient's own.
        http.Timeout = Timeout.InfiniteTimeSpan;
        http.BaseAddress = new Uri($"http://{options.Host}:{options.Port}/");
    }

    /// <inheritdoc />
    public RigCapabilities Capabilities { get; private set; }

    /// <inheritdoc />
    public RigInfo Info { get; private set; } = new("flrig", null, null);

    /// <summary>flrig's own version string (<c>main.get_version</c>), for diagnostics.</summary>
    public string? FlrigVersion { get; private set; }

    /// <summary>
    /// The attached rig's mode vocabulary (<c>rig.get_modes</c>) — flrig mode names are
    /// rig-native, so this is the set <see cref="SetModeAsync"/> accepts. Empty when flrig
    /// didn't supply a table.
    /// </summary>
    public IReadOnlyList<string> SupportedModes { get; private set; } = [];

    /// <summary>Dial flrig, verify it answers (<c>main.get_version</c>), and read identity,
    /// the power-meter scale, and the mode table.</summary>
    public static async Task<FlrigRig> ConnectAsync(
        FlrigRigOptions? options = null, CancellationToken cancellationToken = default)
        => await ConnectAsync(options, handler: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Test seam: as <see cref="ConnectAsync(FlrigRigOptions?, CancellationToken)"/>
    /// with an explicit <see cref="HttpMessageHandler"/>.</summary>
    internal static async Task<FlrigRig> ConnectAsync(
        FlrigRigOptions? options, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        var rig = new FlrigRig(options ?? new FlrigRigOptions(), handler);
        try
        {
            rig.FlrigVersion = await rig.CallAsync("main.get_version", [], cancellationToken).ConfigureAwait(false);

            string? xcvr = null;
            try
            {
                xcvr = await rig.CallAsync("rig.get_xcvr", [], cancellationToken).ConfigureAwait(false);
            }
            catch (RigCommandException)
            {
                // Not fatal — hamlib's backend treats a missing get_xcvr the same way.
            }

            rig.Info = new RigInfo("flrig", Manufacturer: null, Model: string.IsNullOrWhiteSpace(xcvr) ? null : xcvr);

            try
            {
                var scale = await rig.CallAsync("rig.get_pwrmeter_scale", [], cancellationToken).ConfigureAwait(false);
                if (double.TryParse(scale, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
                {
                    rig.powerMeterScale = s;
                }
            }
            catch (RigCommandException)
            {
                // Older flrig — scale stays 1, same default as hamlib's backend.
            }

            try
            {
                var modes = await rig.CallAsync("rig.get_modes", [], cancellationToken).ConfigureAwait(false);
                rig.SupportedModes = modes
                    .Split(['\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (RigCommandException)
            {
                // No table — SetModeAsync will pass tokens through unchecked.
            }

            // Static by design — see the class remarks.
            rig.Capabilities =
                RigCapabilities.FrequencyGet | RigCapabilities.FrequencySet |
                RigCapabilities.ModeGet | RigCapabilities.ModeSet |
                RigCapabilities.PttGet | RigCapabilities.PttSet |
                RigCapabilities.SwrMeter | RigCapabilities.RfPowerMeter | RigCapabilities.RfPowerMeterWatts;

            return rig;
        }
        catch
        {
            rig.http.Dispose();
            rig.gate.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<long> GetFrequencyAsync(CancellationToken cancellationToken = default)
    {
        // Stringly-typed on purpose: rig.get_vfo returns the current-VFO frequency as a string
        // of Hz (there is no rig.get_frequency).
        var value = await CallAsync("rig.get_vfo", [], cancellationToken).ConfigureAwait(false);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz)
            ? checked((long)Math.Round(hz))
            : throw new RigProtocolException($"flrig rig.get_vfo returned unparseable frequency '{value}'.");
    }

    /// <inheritdoc />
    public async ValueTask SetFrequencyAsync(long frequencyHz, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequencyHz);
        await CallAsync("main.set_frequency", [(double)frequencyHz], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<RigModeState> GetModeAsync(CancellationToken cancellationToken = default)
    {
        var mode = await CallAsync("rig.get_mode", [], cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mode))
        {
            throw new RigProtocolException("flrig rig.get_mode returned an empty mode.");
        }

        // flrig's get side has no passband report (rig.get_bw's reply shapes are rig-dependent
        // and unreliable — hamlib falls back to cache), so PassbandHz is honest-null.
        return new RigModeState(RigMode.From(mode), PassbandHz: null);
    }

    /// <inheritdoc />
    public async ValueTask SetModeAsync(
        RigMode mode, int? passbandHz = null, CancellationToken cancellationToken = default)
    {
        if (mode.Token is null)
        {
            throw new ArgumentException("Uninitialised RigMode — use the statics or RigMode.From().", nameof(mode));
        }

        if (passbandHz is not null)
        {
            throw new NotSupportedException(
                "flrig cannot set a passband width alongside the mode — pass null and let the rig choose.");
        }

        // flrig mode names are rig-native. When we have the rig's table, reject tokens outside
        // it up front: flrig ignores unknown mode strings SILENTLY, which is worse than a throw.
        var native = ResolveNativeMode(mode);
        await CallAsync("rig.set_mode", [native], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> GetPttAsync(CancellationToken cancellationToken = default)
    {
        var value = await CallAsync("rig.get_ptt", [], cancellationToken).ConfigureAwait(false);
        return value.Trim() is not ("0" or "");
    }

    /// <inheritdoc />
    public async ValueTask SetPttAsync(bool transmit, CancellationToken cancellationToken = default)
    {
        await CallAsync("rig.set_ptt", [transmit ? 1 : 0], cancellationToken).ConfigureAwait(false);
        keyedByUs = transmit;
    }

    /// <inheritdoc />
    public async ValueTask<double> ReadSwrAsync(CancellationToken cancellationToken = default)
    {
        if (tryGetSwr)
        {
            try
            {
                // Newer flrig has a direct float SWR method; probe it once (hamlib does the same).
                var direct = await CallAsync("rig.get_SWR", [], cancellationToken).ConfigureAwait(false);
                return Parse(direct, "rig.get_SWR");
            }
            catch (RigCommandException)
            {
                tryGetSwr = false;
            }
        }

        // Older path: a 0–100 needle deflection interpolated to a ratio.
        var meter = await CallAsync("rig.get_swrmeter", [], cancellationToken).ConfigureAwait(false);
        return FlrigMeters.InterpolateSwr(Parse(meter, "rig.get_swrmeter"));
    }

    /// <inheritdoc />
    public async ValueTask<double> ReadRfPowerAsync(CancellationToken cancellationToken = default)
    {
        var meter = await CallAsync("rig.get_pwrmeter", [], cancellationToken).ConfigureAwait(false);
        return Parse(meter, "rig.get_pwrmeter") / 100.0 * powerMeterScale;
    }

    /// <inheritdoc />
    public async ValueTask<double> ReadRfPowerWattsAsync(CancellationToken cancellationToken = default)
    {
        var meter = await CallAsync("rig.get_pwrmeter", [], cancellationToken).ConfigureAwait(false);
        return Parse(meter, "rig.get_pwrmeter") * powerMeterScale;
    }

    /// <inheritdoc />
    public ValueTask<bool> ReadDcdAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "flrig exposes no data-carrier-detect / squelch state over XML-RPC — there is nothing " +
            "to serve this read from.");

    /// <inheritdoc />
    public ValueTask<double> ReadSignalStrengthDbmAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "flrig's s-meter (rig.get_smeter) is an uncalibrated 0–100 needle deflection, and we " +
            "never synthesize dBm from uncalibrated readings — use CallRawAsync(\"rig.get_smeter\", …) " +
            "for the raw deflection.");

    /// <summary>
    /// Invoke any flrig XML-RPC method and get its string-form result — the escape hatch below
    /// the <see cref="IRigControl"/> common subset (<c>rig.cat_string</c> raw CAT passthrough,
    /// <c>rig.get_smeter</c>, split/VFO-B ops, …). Args may be <see cref="string"/>,
    /// <see cref="int"/> or <see cref="double"/> — flrig cares about XML-RPC arg types.
    /// </summary>
    public async ValueTask<string> CallRawAsync(
        string methodName, IReadOnlyList<object>? args = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        return await CallAsync(methodName, args as object[] ?? args?.ToArray() ?? [], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (keyedByUs)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2), time);
                using var content = new StringContent(
                    XmlRpcCodec.BuildCall("rig.set_ptt", 0), Encoding.UTF8, "text/xml");
                using var response = await http.PostAsync((Uri?)null, content, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Best effort only.
            }
        }

        http.Dispose();
        gate.Dispose();
    }

    private string ResolveNativeMode(RigMode mode)
    {
        if (SupportedModes.Count == 0)
        {
            return mode.Token;
        }

        foreach (var native in SupportedModes)
        {
            if (string.Equals(native, mode.Token, StringComparison.OrdinalIgnoreCase))
            {
                return native; // preserve the rig's own casing on the wire
            }
        }

        throw new NotSupportedException(
            $"The attached rig has no mode '{mode.Token}' — flrig reports: {string.Join(", ", SupportedModes)}.");
    }

    private static double Parse(string value, string method)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw new RigProtocolException($"flrig {method} returned unparseable number '{value}'.");

    private async Task<string> CallAsync(string methodName, object[] args, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(options.CommandTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                using var content = new StringContent(XmlRpcCodec.BuildCall(methodName, args), Encoding.UTF8, "text/xml");
                using var response = await http.PostAsync((Uri?)null, content, linked.Token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new RigConnectionException(
                        $"flrig at {options.Host}:{options.Port} answered HTTP {(int)response.StatusCode} to {methodName}.");
                }

                return XmlRpcCodec.ParseResponse(body, methodName);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new RigTimeoutException(
                    $"flrig gave no reply to {methodName} within {options.CommandTimeout.TotalSeconds:0.#}s.");
            }
            catch (HttpRequestException ex)
            {
                throw new RigConnectionException($"Cannot reach flrig at {options.Host}:{options.Port}.", ex);
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
