using System.Net;
using M0LTE.Pocsag;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Transports;
using Packet.SoundModem.Pocsag;

namespace Packet.Node.Core.Paging;

/// <summary>
/// Hosts the POCSAG paging service: a TCP line server (PAGE/HEARD) that transmits and receives
/// POCSAG pages over a dedicated soundmodem audio device. Off by default. Reconciles on config
/// change like the RHP server — serialized, teardown-then-rebuild, and a device/bind failure is
/// logged rather than thrown so it can never crash the node.
/// </summary>
public sealed partial class PagingHostedService : IHostedService, IAsyncDisposable
{
    // POCSAG (512/1200/2400 baud FSK) runs at the 12 kHz audio-band DSP rate.
    private const int DspRate = 12000;

    private readonly IConfigProvider _config;
    private readonly ILogger<PagingHostedService> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private IDisposable? _subscription;
    private PagingConfig? _running;
    private SoundModemChannelHost? _host;
    private PagingTcpServer? _server;

    public PagingHostedService(IConfigProvider config, ILoggerFactory? loggerFactory = null)
    {
        _config = config;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PagingHostedService>();
    }

    /// <summary>The bound paging server port, or 0 when not running. Diagnostic / test seam.</summary>
    internal int LocalPort => _server?.LocalPort ?? 0;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync(_config.Current.Paging).ConfigureAwait(false);
        // Hot-reload: a changed paging block restarts just this service. Reconcile is serialized so
        // a burst of config edits can't interleave restarts.
        _subscription = _config.OnChange(next => _ = ReconcileAsync(next.Paging));
    }

    private async Task ReconcileAsync(PagingConfig next)
    {
        await _reconcileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_running is not null && _running == next)
            {
                return; // record equality — nothing relevant changed
            }

            await TearDownAsync().ConfigureAwait(false);
            _running = next;

            if (!next.Enabled)
            {
                return;
            }

            try
            {
                PocsagPolarity polarity = next.InvertPolarity ? PocsagPolarity.Inverted : PocsagPolarity.Normal;
                IPAddress bind = IPAddress.TryParse(next.Bind, out var parsed) ? parsed : IPAddress.Loopback;

                _host = await SoundModemChannelHost.OpenAsync(
                    next.Device, DspRate, next.CaptureRate, next.Ptt, next.Flex,
                    SoundModemFlexDevice.PacketBuffer, CancellationToken.None).ConfigureAwait(false);
                _server = new PagingTcpServer(_host.Channel, next.Port, next.Baud, polarity, bind);
                _server.Start();
                LogListening(next.Bind, _server.LocalPort, _server.Mode);
            }
            catch (Exception ex)
            {
                // A device open or bind clash must not crash the node — log and run without paging.
                LogStartFailed(ex, next.Device, next.Port);
                await TearDownAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task TearDownAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
            _host = null;
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        await TearDownAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _reconcileGate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "POCSAG paging listening on {Bind}:{Port} ({Mode})")]
    private partial void LogListening(string bind, int port, string mode);

    [LoggerMessage(Level = LogLevel.Error, Message = "POCSAG paging failed to start on {Device} port {Port}")]
    private partial void LogStartFailed(Exception exception, string device, int port);
}
