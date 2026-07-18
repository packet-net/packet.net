using System.Net;
using M0LTE.Ardop;
using M0LTE.Ardop.Host;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Transports;
using Packet.SoundModem.Channel;

namespace Packet.Node.Core.Ardop;

/// <summary>
/// Hosts the ARDOP virtual TNC: an ardopcf-compatible TCP host interface (command socket + a data
/// socket on <c>port+1</c>) backed by a dedicated 12 kHz soundmodem channel, so external ARDOP
/// hosts (BPQ, Pat, Winlink) can drive this node's soundcard/FlexRadio as an ARDOP modem. Off by
/// default. Reconciles like the RHP server — serialized, teardown-then-rebuild, never throwing on a
/// device/bind failure.
/// </summary>
public sealed partial class ArdopHostedService : IHostedService, IAsyncDisposable
{
    private readonly IConfigProvider _config;
    private readonly ILogger<ArdopHostedService> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private IDisposable? _subscription;
    private ArdopConfig? _running;
    private SoundModemChannelHost? _host;
    private ArdopHostServer? _server;

    public ArdopHostedService(IConfigProvider config, ILoggerFactory? loggerFactory = null)
    {
        _config = config;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ArdopHostedService>();
    }

    /// <summary>The bound command-socket port, or 0 when not running. Diagnostic / test seam.</summary>
    internal int LocalCommandPort => _server?.LocalCommandPort ?? 0;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ReconcileAsync(_config.Current.Ardop).ConfigureAwait(false);
        _subscription = _config.OnChange(next => _ = ReconcileAsync(next.Ardop));
    }

    private async Task ReconcileAsync(ArdopConfig next)
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
                IPAddress bind = IPAddress.TryParse(next.Bind, out var parsed) ? parsed : IPAddress.Loopback;

                _host = await SoundModemChannelHost.OpenAsync(
                    next.Device, ArdopModulator.SampleRate, next.CaptureRate, next.Ptt, next.Flex,
                    SoundModemFlexDevice.ArdopBuffer, CancellationToken.None).ConfigureAwait(false);

                // ARDOP owns the channel exclusively (single ARQ session) — disable the modem's
                // p-persistent CSMA backoff, matching the daemon.
                SoundModemChannel channel = _host.Channel;
                channel.Csma.Persistence = 255;

                var tnc = new ArdopHostTnc(captureDevice: next.Device, playbackDevice: next.Device)
                {
                    // TX: the ARQ engine's modulated bursts (short PCM) are scaled to ±1 floats and
                    // pushed through the channel's transmit path (the daemon's binding).
                    Transmitter = audio =>
                    {
                        var floats = new float[audio.Length];
                        for (int i = 0; i < audio.Length; i++)
                        {
                            floats[i] = audio[i] / 32768f;
                        }

                        return channel.EnqueueTransmit(_ => floats);
                    },
                };

                // RX: the channel's receive audio feeds the ARDOP demodulator via a tap.
                channel.AddReceiveTap(tnc.ProcessReceive);

                _server = new ArdopHostServer(tnc, next.Port, bind, ownsTnc: true);
                _server.Start();
                LogListening(next.Bind, _server.LocalCommandPort, _server.LocalDataPort);
            }
            catch (Exception ex)
            {
                // A device open or bind clash must not crash the node — log and run without ARDOP.
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
        // Host first: stopping the channel's RX pump + transmitter severs the channel→TNC receive
        // tap and the TNC→channel transmit path before the TNC (owned by the server) is disposed,
        // so neither side can call into a disposed peer.
        if (_host is not null)
        {
            await _host.DisposeAsync().ConfigureAwait(false);
            _host = null;
        }

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "ARDOP virtual TNC listening on {Bind} (command {CommandPort}, data {DataPort})")]
    private partial void LogListening(string bind, int commandPort, int dataPort);

    [LoggerMessage(Level = LogLevel.Error, Message = "ARDOP virtual TNC failed to start on {Device} port {Port}")]
    private partial void LogStartFailed(Exception exception, string device, int port);
}
