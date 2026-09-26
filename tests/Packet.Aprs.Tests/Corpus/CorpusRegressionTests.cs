using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Aprs.Tests.Corpus;

/// <summary>
/// A regression net over real traffic: <c>samples.txt</c> holds one or two APRS-IS packets for
/// every distinct shape seen in the corpus (packet type, diagnostics, optional elements; chosen by
/// <c>aprs-corpus curate</c>). Each is decoded and the result compared with the approved snapshot,
/// so any change in behaviour shows up as a reviewable diff. If a change is intended, copy
/// <c>samples.received.txt</c> over <c>samples.approved.txt</c>.
/// </summary>
public class CorpusRegressionTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Every_sample_decodes_as_approved()
    {
        string[] samples = File.ReadAllLines(PathOf("samples.txt"));
        var received = new StringBuilder();
        foreach (string sample in samples)
        {
            byte[] line = Unescape(sample);
            received.Append(sample).Append('\n');
            if (!AprsPacket.TryDecode(line, out AprsPacket? packet))
            {
                received.Append("  header error\n");
                continue;
            }

            received.Append("  ").Append(Describe(packet.Data)).Append('\n');
            foreach (AprsDiagnostic d in packet.Diagnostics)
            {
                received.Append("  ").Append(d.Severity).Append(' ').Append(d.Code).Append(d.Offset is { } o ? $" @{o}" : "").Append('\n');
            }

            // Every sample that decoded cleanly must re-encode to data that decodes identically, or
            // be refused by the encoder. Refusals go in the snapshot, so a new one is a reviewed
            // change: some clean packets can be read but not sent, such as message text over the
            // 67 characters senders are held to (APRS12c ch. 14).
            if (!packet.HasWarnings && !packet.HasErrors && packet.Data is not AprsUnrecognizedData)
            {
                byte[] again;
                try
                {
                    again = packet.Data.ToInformationField();
                }
                catch (ArgumentException ex)
                {
                    received.Append("  encoder refuses: ").Append(ex.Message).Append('\n');
                    continue;
                }

                AprsPacket.Decode(packet.Source, packet.Destination, packet.Path, again).Data.Should().Be(packet.Data, sample);
            }
        }

        string approvedPath = PathOf("samples.approved.txt");
        string approved = File.Exists(approvedPath) ? File.ReadAllText(approvedPath) : "";
        string actual = received.ToString();
        if (actual != approved)
        {
            File.WriteAllText(PathOf("samples.received.txt"), actual);
        }

        actual.Should().Be(approved, "decoding of the corpus samples changed; review samples.received.txt and approve it if intended");
    }

    private static string Describe(AprsData data) => data switch
    {
        AprsThirdPartyTraffic t => $"ThirdParty {t.Packet.Source}>{t.Packet.Destination} {Describe(t.Packet.Data)}",
        AprsUnrecognizedData u => $"Unrecognized {u.Reason}",
        _ => data.GetType().Name + " " + JsonSerializer.Serialize(data, data.GetType(), Json),
    };

    private static byte[] Unescape(string s)
    {
        var bytes = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 3 < s.Length && s[i + 1] == 'x')
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 2, 2), 16));
                i += 3;
            }
            else
            {
                bytes.Add((byte)s[i]);
            }
        }

        return [.. bytes];
    }

    private static string PathOf(string name, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, name);
}
