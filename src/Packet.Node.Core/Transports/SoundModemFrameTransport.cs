using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Packet.Ax25.Transport;
using Packet.Node.Core.Configuration;
using Packet.SoundModem.Audio;
using Packet.SoundModem.Channel;
using Packet.SoundModem.Dsp;
using Packet.SoundModem.Modems;

namespace Packet.Node.Core.Transports;

/// <summary>Blocking capture source for the soundmodem RX pump (seam for tests; the
/// production implementation wraps ALSA).</summary>
public interface ISoundModemCapture : IDisposable
{
    /// <summary>Capture sample rate.</summary>
    int SampleRate { get; }

    /// <summary>Fills <paramref name="buffer"/> with mono S16 frames; blocks until data
    /// is available. Returns frames read (0 signals end-of-stream for tests).</summary>
    int Read(Span<short> buffer);
}

/// <summary>
/// The <c>kind: soundmodem</c> transport: runs the pdn-soundmodem engine in-process over
/// an audio device. Implements the full optional-facet set the seam anticipates —
/// <see cref="ICarrierSense"/> (native DCD + energy busy into the listener's
/// carrier-sense gate, the OQ-012 shape), <see cref="ITxCompletionTransport"/>
/// (sample-accurate: the completion task resolves when the audio has fully left the
/// device), and <see cref="ICsmaChannelParams"/> (the KISS channel-access knobs drive the
/// modem's own p-persistent CSMA).
/// </summary>
public sealed class SoundModemFrameTransport : IAx25Transport, ICarrierSense, ITxCompletionTransport, ICsmaChannelParams
{
    private readonly SoundModemChannel _channel;
    private readonly ISoundModemCapture _capture;
    private readonly IAudioOutput _output;
    private readonly IPttControl _ptt;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<Ax25InboundFrame> _inbound =
        System.Threading.Channels.Channel.CreateUnbounded<Ax25InboundFrame>();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _rxPump;
    private readonly Task _transmitter;
    private volatile bool _running;

    /// <summary>Creates the transport over explicit audio endpoints (the test seam; use
    /// <see cref="Open"/> for the ALSA production path). Pumps start immediately.</summary>
    public SoundModemFrameTransport(
        SoundModemTransportConfig config,
        ISoundModemCapture capture,
        IAudioOutput output,
        IPttControl ptt,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(ptt);
        _capture = capture;
        _output = output;
        _ptt = ptt;
        _timeProvider = timeProvider ?? TimeProvider.System;

        int dspRate = DspRate(config.Mode);
        if (capture.SampleRate % dspRate != 0)
        {
            throw new ArgumentException(
                $"capture rate {capture.SampleRate} is not a multiple of the mode's DSP rate {dspRate}",
                nameof(capture));
        }

        if (output.SampleRate != dspRate)
        {
            throw new ArgumentException(
                $"output rate {output.SampleRate} != DSP rate {dspRate}", nameof(output));
        }

        // The spectrum tap runs whether or not anyone is watching (a 4096-pt FFT at
        // ~3 lines/s is sub-percent CPU); the waterfall SSE endpoint subscribes to the
        // event. Line format: one dB-scaled byte per bin, 0 Hz .. dspRate/2.
        _channel = new SoundModemChannel(
            dspRate, spectrumSink: line => SpectrumLine?.Invoke(line));
        SpectrumBinWidthHz = dspRate / 4096.0;
        _channel.AddModem(0, sink => CreateModem(config, dspRate, sink));
        _channel.FrameReceived += (_, frame) =>
            _inbound.Writer.TryWrite(new Ax25InboundFrame(frame, 0, _timeProvider.GetUtcNow()));

        _transmitter = _channel.RunTransmitterAsync(output, ptt, _stopping.Token);
        _rxPump = new Thread(RxPump) { IsBackground = true, Name = "soundmodem-rx" };
        _rxPump.Start();
        _running = true;
    }

    /// <summary>Opens the transport over ALSA per the port configuration.</summary>
    public static SoundModemFrameTransport Open(
        SoundModemTransportConfig config, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        int dspRate = DspRate(config.Mode);
        var capture = new AlsaCaptureSource(config.Device, config.CaptureRate);
        AlsaAudioOutput? output = null;
        IPttControl? ptt = null;
        try
        {
            output = new AlsaAudioOutput(config.Device, dspRate);
            ptt = CreatePtt(config.Ptt);
            return new SoundModemFrameTransport(config, capture, output, ptt, timeProvider);
        }
        catch
        {
            (ptt as IDisposable)?.Dispose();
            output?.Dispose();
            capture.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public bool? ChannelBusy => _running ? _channel.ChannelBusy : null;

    /// <summary>One waterfall line per FFT frame (~3/s): 2048 dB-scaled bytes covering
    /// 0 Hz to half the DSP rate. The buffer is reused — copy if kept. Raised on the
    /// receive-pump thread.</summary>
    public event Action<ReadOnlyMemory<byte>>? SpectrumLine;

    /// <summary>Width of one spectrum bin in hertz.</summary>
    public double SpectrumBinWidthHz { get; }

    /// <inheritdoc/>
    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        // Fire-and-forget at this layer: queue for the CSMA/PTT machinery and return.
        // Failures surface on the completion-aware path or in the transmitter task.
        _ = _channel.EnqueueTransmit(0, ax25.ToArray());
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<TxCompletion> SendAwaitingCompletionAsync(
        ReadOnlyMemory<byte> ax25, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        DateTimeOffset queued = _timeProvider.GetUtcNow();
        Task sent = _channel.EnqueueTransmit(0, ax25.ToArray());
        if (timeout is { } limit)
        {
            await sent.WaitAsync(limit, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await sent.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new TxCompletion(queued, _timeProvider.GetUtcNow());
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (Ax25InboundFrame frame in _inbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    /// <inheritdoc/>
    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
    {
        _channel.Csma.TxDelayMilliseconds = tenMsUnits * 10;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default)
    {
        _channel.Csma.Persistence = value;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
    {
        _channel.Csma.SlotTimeMilliseconds = tenMsUnits * 10;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
    {
        _channel.Csma.TxTailMilliseconds = tenMsUnits * 10;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        await _stopping.CancelAsync().ConfigureAwait(false);
        _inbound.Writer.TryComplete();
        try
        {
            await _transmitter.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (!_rxPump.Join(TimeSpan.FromSeconds(2)))
        {
            // The capture read may be blocked in ALSA; disposing the capture below
            // unblocks it and the background thread dies with the process either way.
        }

        _capture.Dispose();
        (_output as IDisposable)?.Dispose();
        (_ptt as IDisposable)?.Dispose();
        _stopping.Dispose();
    }

    private void RxPump()
    {
        int dspRate = _channel.SampleRate;
        int factor = _capture.SampleRate / dspRate;
        Decimator? decimator = factor > 1 ? new Decimator(_capture.SampleRate, factor) : null;
        var pcm = new short[_capture.SampleRate / 10]; // 100 ms blocks
        var floats = new float[pcm.Length];
        var dsp = new float[decimator?.MaxOutput(pcm.Length) ?? pcm.Length];
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int got = _capture.Read(pcm);
                if (got <= 0)
                {
                    // End of stream (test sources); idle briefly rather than spin.
                    if (_stopping.Token.WaitHandle.WaitOne(20))
                    {
                        return;
                    }

                    continue;
                }

                for (int i = 0; i < got; i++)
                {
                    floats[i] = pcm[i] / 32768f;
                }

                if (decimator is null)
                {
                    _channel.ProcessReceive(floats.AsSpan(0, got));
                }
                else
                {
                    int produced = decimator.Process(floats.AsSpan(0, got), dsp);
                    _channel.ProcessReceive(dsp.AsSpan(0, produced));
                }
            }
        }
        catch (Exception) when (_stopping.IsCancellationRequested)
        {
            // Disposal races (capture closed under us) are expected on the way out.
        }
    }

    private static int DspRate(string mode) =>
        mode.StartsWith("fsk9600", StringComparison.OrdinalIgnoreCase) ? 48000 : 12000;

    private static IModem CreateModem(SoundModemTransportConfig config, int dspRate, Action<byte[]> sink)
    {
        double? frequency = config.Frequency > 0 ? config.Frequency : null;
        return config.Mode.ToLowerInvariant() switch
        {
            "afsk1200" => new Afsk1200Modem(dspRate, sink, frequency ?? 1700),
            "afsk1200-multi" => new Afsk1200MultiModem(dspRate, sink, offsetPairs: 3, centerFrequency: frequency ?? 1700),
            "afsk1200-fx25" => new Afsk1200Modem(dspRate, sink, frequency ?? 1700, Fx25Mode.TransmitReceive),
            "afsk1200-fx25rx" => new Afsk1200Modem(dspRate, sink, frequency ?? 1700, Fx25Mode.Receive),
            "bpsk300" => new Bpsk300Modem(dspRate, sink, crc: true, frequency ?? 1500),
            "bpsk300-nocrc" => new Bpsk300Modem(dspRate, sink, crc: false, frequency ?? 1500),
            "qpsk2400" => QpskModem.Qpsk2400(dspRate, sink),
            "qpsk3600" => QpskModem.Qpsk3600(dspRate, sink),
            "fsk9600" => new Fsk9600Modem(dspRate, sink, Fsk9600Framing.ClassicHdlc),
            "fsk9600-il2p" => new Fsk9600Modem(dspRate, sink, Fsk9600Framing.Il2pCrc),
            _ => throw new NotSupportedException($"unknown soundmodem mode '{config.Mode}'"),
        };
    }

    private static IPttControl CreatePtt(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return new NullPtt();
        }

        string[] parts = spec.Split(':');
        return parts switch
        {
            ["serial", var device] => new SerialPtt(device),
            ["serial", var device, var line] => new SerialPtt(device, useRts: line != "dtr", useDtr: line == "dtr"),
            ["cm108", var device] => new Cm108Ptt(device),
            ["cm108", var device, var gpio] => new Cm108Ptt(device, int.Parse(gpio, CultureInfo.InvariantCulture)),
            _ => throw new NotSupportedException($"unknown ptt spec '{spec}'"),
        };
    }

    /// <summary>ALSA-backed capture source.</summary>
    private sealed class AlsaCaptureSource(string device, int sampleRate) : ISoundModemCapture
    {
        private readonly AlsaPcm _pcm = AlsaPcm.Open(device, AlsaPcm.Direction.Capture, channels: 1, sampleRate);

        public int SampleRate { get; } = sampleRate;

        public int Read(Span<short> buffer) => _pcm.Read(buffer);

        public void Dispose() => _pcm.Dispose();
    }
}
