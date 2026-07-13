using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Packet.Rig.Flrig;

/// <summary>
/// The sliver of XML-RPC flrig actually uses, hand-rolled — every C# flrig consumer surveyed
/// hand-rolls this rather than take an XML-RPC package dependency, and flrig's dialect is tiny:
/// scalar params in, one scalar out, faults for errors. Kept IO-free for direct testing.
/// </summary>
internal static class XmlRpcCodec
{
    /// <summary>Serialise a call. Args are pre-typed: strings go as <c>&lt;string&gt;</c>,
    /// doubles as <c>&lt;double&gt;</c>, ints as <c>&lt;i4&gt;</c> — flrig is picky about
    /// receiving the type its method table declares (set_frequency wants a double).</summary>
    internal static string BuildCall(string methodName, params object[] args)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><methodCall><methodName>")
          .Append(methodName)
          .Append("</methodName><params>");

        foreach (var arg in args)
        {
            sb.Append("<param><value>");
            switch (arg)
            {
                case string s:
                    sb.Append("<string>").Append(EscapeXml(s)).Append("</string>");
                    break;
                case double d:
                    sb.Append("<double>").Append(d.ToString("R", CultureInfo.InvariantCulture)).Append("</double>");
                    break;
                case int i:
                    sb.Append("<i4>").Append(i.ToString(CultureInfo.InvariantCulture)).Append("</i4>");
                    break;
                default:
                    throw new ArgumentException($"Unsupported XML-RPC argument type {arg.GetType()}.", nameof(args));
            }

            sb.Append("</value></param>");
        }

        sb.Append("</params></methodCall>");
        return sb.ToString();

        static string EscapeXml(string s) => s
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    /// <summary>
    /// Extract the single return value of a <c>methodResponse</c> as its string form (flrig is
    /// stringly-typed — even frequencies come back as <c>&lt;value&gt;14074000&lt;/value&gt;</c>;
    /// callers parse). Void replies yield <c>""</c>. Faults throw <see cref="RigCommandException"/>
    /// with flrig's faultCode/faultString.
    /// </summary>
    internal static string ParseResponse(string xml, string methodName)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new RigProtocolException($"flrig reply to {methodName} was not XML: {ex.Message}");
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "methodResponse")
        {
            throw new RigProtocolException($"flrig reply to {methodName} was not a methodResponse.");
        }

        if (root.Element("fault") is { } fault)
        {
            var members = fault.Descendants("member").ToDictionary(
                m => m.Element("name")?.Value ?? "",
                m => m.Element("value"));
            var codeText = members.GetValueOrDefault("faultCode")?.Value.Trim();
            _ = int.TryParse(codeText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code);
            var message = members.GetValueOrDefault("faultString")?.Value.Trim() ?? "unspecified fault";
            throw new RigCommandException($"flrig rejected {methodName}: {message}", code);
        }

        var value = root.Element("params")?.Element("param")?.Element("value");
        if (value is null)
        {
            return "";
        }

        // Typed scalar child if present (<string>/<i4>/<int>/<double>/<boolean>); XML-RPC's
        // untyped default is string, i.e. the value element's own text.
        var typed = value.Elements().FirstOrDefault();
        if (typed is null)
        {
            return value.Value;
        }

        if (typed.Name.LocalName is "array" or "struct")
        {
            // flrig's list replies (get_modes, get_bws). Flatten array members to
            // newline-separated entries; callers that need them split.
            return string.Join(
                '\n',
                typed.Descendants("value").Select(v => v.Elements().FirstOrDefault()?.Value ?? v.Value));
        }

        return typed.Value;
    }
}
