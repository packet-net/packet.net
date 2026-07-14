using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;

namespace Packet.Rig.Flrig.Tests;

/// <summary>
/// A scripted flrig XML-RPC endpoint behind an <see cref="HttpMessageHandler"/> — no sockets,
/// fully deterministic. Implements the stateful method slice hamlib's flrig backend exercises;
/// individual methods can be overridden or faulted per test. (flrig itself is an FLTK GUI with
/// no headless mode, so an in-process fake is the established way to test its clients.)
/// </summary>
internal sealed class FakeFlrigHandler : HttpMessageHandler
{
    internal string Version = "2.0.05";
    internal string Xcvr = "IC-7300";
    internal double FrequencyHz = 14_074_000;
    internal string Mode = "USB";
    internal int Ptt;
    internal string Modes = "LSB\nUSB\nAM\nCW\nRTTY-U\nDATA-U";
    internal double PwrMeter = 50;       // 0–100 needle deflection
    internal double PwrMeterScale = 1.0;
    internal double? SwrDirect = 1.2;    // rig.get_SWR when non-null; else the method faults
    internal double SwrMeter;            // 0–100 deflection for the fallback path

    /// <summary>Method names that fault with (code, message) on next call.</summary>
    internal readonly ConcurrentDictionary<string, (int Code, string Message)> Faults = new();

    /// <summary>Method invocation log: (method, rawArgsXml).</summary>
    internal readonly ConcurrentQueue<(string Method, string Body)> Calls = new();

    /// <summary>When set, requests never complete — timeout tests advance a FakeTimeProvider.</summary>
    internal volatile bool HangNextRequest;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var method = Extract(body, "methodName");
        Calls.Enqueue((method, body));

        if (HangNextRequest)
        {
            HangNextRequest = false;
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Faults.TryRemove(method, out var fault))
        {
            return Respond(FaultXml(fault.Code, fault.Message));
        }

        var inv = CultureInfo.InvariantCulture;
        return method switch
        {
            "main.get_version" => Respond(ValueXml(Version)),
            "rig.get_xcvr" => Respond(ValueXml(Xcvr)),
            "rig.get_pwrmeter_scale" => Respond(ValueXml(PwrMeterScale.ToString(inv))),
            "rig.get_modes" => Respond(ArrayXml(Modes.Split('\n'))),
            "rig.get_vfo" => Respond(ValueXml(FrequencyHz.ToString("F0", inv))), // string of Hz, like flrig
            "main.set_frequency" => Apply(() => FrequencyHz = double.Parse(Extract(body, "double"), inv)),
            "rig.get_mode" => Respond(ValueXml(Mode)),
            "rig.set_mode" => Apply(() => Mode = Extract(body, "string")),
            "rig.get_ptt" => Respond(TypedXml("i4", Ptt.ToString(inv))),
            "rig.set_ptt" => Apply(() => Ptt = int.Parse(Extract(body, "i4"), inv)),
            "rig.get_SWR" when SwrDirect is { } swr => Respond(ValueXml(swr.ToString(inv))),
            "rig.get_SWR" => Respond(FaultXml(-1, "unknown method name")),
            "rig.get_swrmeter" => Respond(ValueXml(SwrMeter.ToString(inv))),
            "rig.get_pwrmeter" => Respond(ValueXml(PwrMeter.ToString(inv))),
            _ => Respond(FaultXml(-1, $"unknown method name: {method}")),
        };

        HttpResponseMessage Apply(Action mutate)
        {
            mutate();
            return Respond("<?xml version=\"1.0\"?><methodResponse><params/></methodResponse>");
        }
    }

    private static string Extract(string xml, string element)
    {
        var open = $"<{element}>";
        var start = xml.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        start += open.Length;
        var end = xml.IndexOf($"</{element}>", start, StringComparison.Ordinal);
        return end < 0 ? "" : xml[start..end];
    }

    private static HttpResponseMessage Respond(string xml) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(xml, Encoding.UTF8, "text/xml"),
    };

    private static string ValueXml(string value)
        => $"<?xml version=\"1.0\"?><methodResponse><params><param><value>{value}</value></param></params></methodResponse>";

    private static string TypedXml(string type, string value)
        => $"<?xml version=\"1.0\"?><methodResponse><params><param><value><{type}>{value}</{type}></value></param></params></methodResponse>";

    private static string ArrayXml(IEnumerable<string> values)
    {
        var data = string.Join("", values.Select(v => $"<value><string>{v}</string></value>"));
        return $"<?xml version=\"1.0\"?><methodResponse><params><param><value><array><data>{data}</data></array></value></param></params></methodResponse>";
    }

    private static string FaultXml(int code, string message)
        => "<?xml version=\"1.0\"?><methodResponse><fault><value><struct>" +
           $"<member><name>faultCode</name><value><int>{code}</int></value></member>" +
           $"<member><name>faultString</name><value><string>{message}</string></value></member>" +
           "</struct></value></fault></methodResponse>";
}
