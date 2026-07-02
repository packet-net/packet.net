using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Packet.Radio.Tait;

/// <summary>
/// Finds Tait radios on the machine's serial ports — the CCDI analogue of
/// <c>NinoTncPortDiscovery</c>, but with an authoritative probe: unlike KISS, CCDI has a
/// query/response identity command, so a candidate port either answers a MODEL query with a
/// serial number or it isn't a Tait radio. Intended for the node host's "just plug it in"
/// configuration story: enumerate, probe, present <c>ProductName + serial</c> to the operator.
/// </summary>
/// <remarks>
/// The CCDI USB dongles seen in the wild (CP2102s) often share identical USB serial numbers,
/// so <c>/dev/serial/by-id</c> cannot distinguish two radios — the CCDI serial-number query is
/// the only reliable identity. Probing writes a <c>q002F</c> (MODEL query) to each candidate:
/// harmless to a KISS TNC sharing the machine (unframed ASCII is discarded by KISS framing),
/// but skip ports you know belong to something touchier via the env-var override.
/// </remarks>
public static class TaitRadioPortDiscovery
{
    /// <summary>Colon/semicolon/comma-separated list of ports to probe INSTEAD of scanning
    /// (e.g. <c>"/dev/ttyUSB0,/dev/ttyUSB1"</c>).</summary>
    public const string PortsOverrideEnvVar = "PACKETNET_TAIT_PORTS";

    /// <summary>
    /// Candidate serial ports worth probing, best-first: the env-var override verbatim if set;
    /// otherwise on Linux the USB-UART bridges (<c>/dev/ttyUSB*</c> — CCDI dongles are UART
    /// bridges, whereas NinoTNCs enumerate as CDC <c>/dev/ttyACM*</c>); otherwise every port
    /// the OS reports.
    /// </summary>
    public static IReadOnlyList<string> EnumerateCandidatePorts()
    {
        if (Environment.GetEnvironmentVariable(PortsOverrideEnvVar) is { Length: > 0 } overridePorts)
        {
            return overridePorts.Split(
                [',', ';', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string[] usbUarts = Directory.Exists("/dev")
                ? Directory.GetFiles("/dev", "ttyUSB*").Order().ToArray()
                : [];
            if (usbUarts.Length > 0)
            {
                return usbUarts;
            }
        }

        return SerialPort.GetPortNames();
    }

    /// <summary>
    /// Ask "is there a Tait radio on this port at this baud rate?" — opens the port, sends a
    /// MODEL/serial/versions query, and returns the identity, or <c>null</c> when anything
    /// (open failure, timeout, gibberish) says no. Never throws for a negative.
    /// </summary>
    public static async Task<TaitDiscoveredRadio?> ProbeAsync(
        string portName,
        int baudRate = TaitCcdiRadio.DefaultBaudRate,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = new TaitCcdiRadioOptions
            {
                KeepAliveInterval = null,
                TransactionTimeout = TimeSpan.FromMilliseconds(750),
            };
            await using var radio = TaitCcdiRadio.Open(portName, baudRate, options);
            var identity = await radio.QueryIdentityAsync(cancellationToken).ConfigureAwait(false);
            return new TaitDiscoveredRadio(portName, baudRate, identity);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException
                                       or InvalidOperationException or TaitCcdiException)
        {
            return null;
        }
    }

    /// <summary>
    /// Probe every candidate port (serially — two probes racing on one USB hub help nobody)
    /// and yield each radio found. <paramref name="baudRates"/> defaults to 28800 only; pass
    /// more (e.g. 9600, 19200) to sweep radios programmed at other rates.
    /// </summary>
    public static async IAsyncEnumerable<TaitDiscoveredRadio> DiscoverAsync(
        IReadOnlyList<int>? baudRates = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rates = baudRates is { Count: > 0 } ? baudRates : [TaitCcdiRadio.DefaultBaudRate];
        foreach (string port in EnumerateCandidatePorts())
        {
            foreach (int baud in rates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await ProbeAsync(port, baud, cancellationToken).ConfigureAwait(false) is { } found)
                {
                    yield return found;
                    break; // one radio per port; no point probing other rates
                }
            }
        }
    }
}

/// <summary>A Tait radio found by <see cref="TaitRadioPortDiscovery"/>: where it is, the rate
/// it answered at, and who it says it is.</summary>
public sealed record TaitDiscoveredRadio(string Port, int BaudRate, TaitRadioIdentity Identity);
