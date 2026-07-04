using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;

namespace Packet.Tune.Core;

/// <summary>Tunables for <see cref="NinoTncStationStatusSource"/>.</summary>
public sealed record NinoTncStationStatusOptions
{
    /// <summary>The mode numbers this station advertises it can run. Null = every concrete
    /// NinoTNC catalog mode (0–14; the "Set from KISS" escape 15 is not a fixed mode).</summary>
    public IReadOnlyList<byte>? SupportedModes { get; init; }

    /// <summary>The capability tokens this station advertises. Null = the standard set
    /// (<c>hail</c>, <c>modecoord</c>, <c>tune</c>).</summary>
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Whether to sample the radio's receiver RSSI at hail receipt for
    /// <see cref="StationStatus.RssiOfHailDbm"/>. Default <c>true</c> (a cheap query; never
    /// keys). Best-effort — a failed read simply omits the field.</summary>
    public bool SampleRssiOfHail { get; init; } = true;

    /// <summary>Whether to query the radio's current channel for
    /// <see cref="StationStatus.Channel"/>. Default <c>true</c>.</summary>
    public bool QueryChannel { get; init; } = true;

    /// <summary>Timeout for the best-effort NinoTNC / radio queries. Default 3 s.</summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// The live <see cref="IStationStatusProvider"/>: assembles this station's
/// <see cref="StationStatus"/> from a NinoTNC (running mode + bit rate) and, when present, a
/// Tait CCDI radio (channel + RSSI-of-the-hail). Shared by the <c>packet-tune hail --respond</c>
/// CLI and the PDN node's hail responder, so both report status identically.
/// </summary>
/// <remarks>
/// The current mode is taken from <see cref="NinoTncSerialPort.CurrentMode"/> /
/// <see cref="NinoTncSerialPort.CurrentBitRateHz"/> when this connection has set one; otherwise it
/// falls back to a best-effort <c>GETALL</c> read of the firmware's actual running mode (so a
/// responder that did not itself command the mode — the usual case — still reports it correctly).
/// Every hardware query is best-effort: a timeout drops the field, never the whole reply.
/// </remarks>
public sealed class NinoTncStationStatusSource : IStationStatusProvider
{
    /// <summary>The standard capability tokens a PDN station advertises.</summary>
    public static readonly IReadOnlyList<string> DefaultCapabilities = ["hail", "modecoord", "tune"];

    private readonly NinoTncSerialPort? tnc;
    private readonly TaitCcdiRadio? radio;
    private readonly string callsign;
    private readonly NinoTncStationStatusOptions options;

    /// <summary>Create over a TNC + radio pair (lifetimes stay the caller's).</summary>
    /// <param name="tnc">The NinoTNC whose mode is reported, or <c>null</c> (mode unknown).</param>
    /// <param name="radio">The paired radio for channel/RSSI, or <c>null</c> (both omitted).</param>
    /// <param name="callsign">This station's callsign.</param>
    /// <param name="options">Tunables; null = defaults.</param>
    public NinoTncStationStatusSource(
        NinoTncSerialPort? tnc, TaitCcdiRadio? radio, string callsign, NinoTncStationStatusOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(callsign);
        this.tnc = tnc;
        this.radio = radio;
        this.callsign = callsign;
        this.options = options ?? new NinoTncStationStatusOptions();
    }

    /// <summary>Diagnostic sink. Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <inheritdoc/>
    public async Task<StationStatus> GetStatusAsync(StationHail hail, CancellationToken cancellationToken = default)
    {
        // RSSI first — sampled as close to hail receipt as we can, before any other query
        // (or the responder's own auto-ack) perturbs the receiver.
        double? rssi = null;
        if (radio is not null && options.SampleRssiOfHail)
        {
            rssi = await TrySampleRssiAsync(cancellationToken).ConfigureAwait(false);
        }

        var (mode, bitrate) = await ResolveModeAsync(cancellationToken).ConfigureAwait(false);

        string? channel = null;
        if (radio is not null && options.QueryChannel)
        {
            channel = await TryQueryChannelAsync(cancellationToken).ConfigureAwait(false);
        }

        return new StationStatus
        {
            Callsign = callsign,
            Mode = mode,
            BitRateHz = bitrate,
            Channel = channel,
            SupportedModes = options.SupportedModes ?? DefaultSupportedModes(),
            Capabilities = options.Capabilities ?? DefaultCapabilities,
            RssiOfHailDbm = rssi,
        };
    }

    /// <summary>The concrete NinoTNC catalog modes (0–14), the default advertised capability set.</summary>
    public static IReadOnlyList<byte> DefaultSupportedModes() =>
        NinoTncCatalog.ByMode.Keys.Where(m => m != 15).OrderBy(m => m).ToArray();

    private async Task<(byte? Mode, int? BitRate)> ResolveModeAsync(CancellationToken cancellationToken)
    {
        if (tnc is null)
        {
            return (null, null);
        }

        // Fast path: a mode this connection commanded.
        if (tnc.CurrentMode is { } current)
        {
            return (current, tnc.CurrentBitRateHz);
        }

        // Authoritative fallback: the firmware's actual running mode.
        try
        {
            var status = await tnc.GetAllAsync(options.QueryTimeout, cancellationToken).ConfigureAwait(false);
            if (status.RunningMode is { } running)
            {
                return (running.Mode, running.BitRateHz > 0 ? running.BitRateHz : null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            Log?.Invoke($"status: GETALL mode read failed ({ex.Message}) — mode reported unknown");
        }
        return (null, null);
    }

    private async Task<double?> TrySampleRssiAsync(CancellationToken cancellationToken)
    {
        try
        {
            float dbm = await radio!.ReadRssiDbmAsync(cancellationToken).AsTask()
                .WaitAsync(options.QueryTimeout, cancellationToken).ConfigureAwait(false);
            return Math.Round(dbm, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or TaitCcdiException or IOException)
        {
            Log?.Invoke($"status: RSSI-of-hail sample failed ({ex.Message}) — omitted");
            return null;
        }
    }

    private async Task<string?> TryQueryChannelAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await radio!.QueryCurrentChannelAsync(cancellationToken).WaitAsync(options.QueryTimeout, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrEmpty(report.ChannelId) ? null : report.ChannelId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or TaitCcdiException or IOException)
        {
            Log?.Invoke($"status: channel query failed ({ex.Message}) — omitted");
            return null;
        }
    }
}
