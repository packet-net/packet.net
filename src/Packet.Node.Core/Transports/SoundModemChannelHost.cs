using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using Packet.Node.Core.Configuration;
using Packet.SoundModem.Channel;
using Packet.SoundModem.FlexRadio;

namespace Packet.Node.Core.Transports;

/// <summary>
/// Hosts a <see cref="SoundModemChannel"/> over a resolved audio device (an ALSA device or a
/// <c>flex:</c> FlexRadio slice): it runs the RX pump (device audio → decimate → the channel) and
/// the channel's transmitter, and exposes the channel for a consumer to drive. The ARDOP virtual
/// TNC and the POCSAG paging server both attach to the channel's receive tap and transmit queue,
/// so this is the one place the device + channel plumbing lives for those services.
/// </summary>
internal sealed class SoundModemChannelHost : IAsyncDisposable
{
    private readonly IAudioInput _input;
    private readonly IAudioOutput _output;
    private readonly IPttControl _ptt;
    private readonly IAsyncDisposable? _deviceOwner;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _rxPump;
    private readonly Task _transmitter;
    private bool _disposed;

    /// <summary>The hosted channel — attach receive taps and enqueue transmits here.</summary>
    public SoundModemChannel Channel { get; }

    private SoundModemChannelHost(
        int dspRate, IAudioInput input, IAudioOutput output, IPttControl ptt, IAsyncDisposable? deviceOwner)
    {
        if (input.SampleRate % dspRate != 0)
        {
            throw new ArgumentException(
                $"device input rate {input.SampleRate} is not a multiple of the DSP rate {dspRate}", nameof(input));
        }

        if (output.SampleRate != dspRate)
        {
            throw new ArgumentException(
                $"device output rate {output.SampleRate} != DSP rate {dspRate}", nameof(output));
        }

        _input = input;
        _output = output;
        _ptt = ptt;
        _deviceOwner = deviceOwner;
        Channel = new SoundModemChannel(dspRate);
        _transmitter = Channel.RunTransmitterAsync(output, ptt, _stopping.Token);
        _rxPump = new Thread(RxPump) { IsBackground = true, Name = "soundmodem-host-rx" };
        _rxPump.Start();
    }

    /// <summary>Opens the device backend (ALSA, or a <c>flex:</c> FlexRadio slice) and starts the
    /// channel host at <paramref name="dspRate"/>. On any failure the partially-opened device is
    /// released before the exception propagates.</summary>
    public static async Task<SoundModemChannelHost> OpenAsync(
        string device,
        int dspRate,
        int captureRate,
        string pttSpec,
        SoundModemFlexConfig? flex,
        int flexPacketBuffer,
        CancellationToken cancellationToken)
    {
        if (SoundModemFlexDevice.IsFlex(device))
        {
            FlexRuntime? runtime = null;
            try
            {
                runtime = await SoundModemFlexDevice
                    .OpenAsync(device, dspRate, flex, flexPacketBuffer, cancellationToken)
                    .ConfigureAwait(false);
                // The FlexRuntime owns Input/Output/Ptt and keys the radio itself.
                return new SoundModemChannelHost(dspRate, runtime.Input, runtime.Output, runtime.Ptt, deviceOwner: runtime);
            }
            catch
            {
                if (runtime is not null)
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }

        AlsaAudioInput? input = null;
        IAudioOutput? output = null;
        IPttControl? ptt = null;
        try
        {
            input = new AlsaAudioInput(device, captureRate);
            // Cards commonly refuse a direct 12 kHz playback open; play at the card-native capture
            // rate through the image-rejecting upsampler instead.
            output = captureRate == dspRate
                ? new AlsaAudioOutput(device, dspRate)
                : new UpsamplingAudioOutput(new AlsaAudioOutput(device, captureRate), dspRate);
            ptt = SoundModemPtt.Create(pttSpec);
            return new SoundModemChannelHost(dspRate, input, output, ptt, deviceOwner: null);
        }
        catch
        {
            (ptt as IDisposable)?.Dispose();
            (output as IDisposable)?.Dispose();
            input?.Dispose();
            throw;
        }
    }

    private void RxPump()
    {
        int dspRate = Channel.SampleRate;
        int factor = _input.SampleRate / dspRate;
        Decimator? decimator = factor > 1 ? new Decimator(_input.SampleRate, factor) : null;
        var block = new float[_input.SampleRate / 10]; // 100 ms blocks
        var dsp = new float[decimator?.MaxOutput(block.Length) ?? block.Length];
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                int got = _input.Read(block);
                if (got <= 0)
                {
                    // End of stream (e.g. a closing mock source); idle briefly rather than spin.
                    if (_stopping.Token.WaitHandle.WaitOne(20))
                    {
                        return;
                    }

                    continue;
                }

                if (decimator is null)
                {
                    Channel.ProcessReceive(block.AsSpan(0, got));
                }
                else
                {
                    int produced = decimator.Process(block.AsSpan(0, got), dsp);
                    Channel.ProcessReceive(dsp.AsSpan(0, produced));
                }
            }
        }
        catch (Exception) when (_stopping.IsCancellationRequested)
        {
            // Disposal races (device closed under us) are expected on the way out.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await _transmitter.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _rxPump.Join(TimeSpan.FromSeconds(2));

        if (_deviceOwner is not null)
        {
            // The device (a FlexRuntime) owns Input/Output/Ptt — dispose it, not them.
            await _deviceOwner.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            (_input as IDisposable)?.Dispose();
            (_output as IDisposable)?.Dispose();
            (_ptt as IDisposable)?.Dispose();
        }

        _stopping.Dispose();
    }
}
