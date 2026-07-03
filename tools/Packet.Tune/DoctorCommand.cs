using System.Globalization;
using System.Text.Json;
using Packet.Tune.Core;

namespace Packet.Tune;

/// <summary>
/// <c>doctor</c>: run the <see cref="TuningDoctor"/> capability probes
/// against a TNC (and optionally its radio) and print the results as a
/// table, or as JSON with <c>--json</c> for machine use. Exit code 0 = no
/// failed probe, 1 = at least one failure.
/// </summary>
internal static class DoctorCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> Run(string tncPort, string[] rest)
    {
        string? ccdiPort = null;
        bool json = false;
        string callsign = "N0CALL";
        byte mode = 6;
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--callsign" when i + 1 < rest.Length:
                    callsign = rest[++i];
                    break;
                case "--mode" when i + 1 < rest.Length &&
                                   byte.TryParse(rest[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var m):
                    mode = m;
                    i++;
                    break;
                default:
                    ccdiPort = rest[i];
                    break;
            }
        }

        if (!json)
        {
            Console.WriteLine($"doctor — TNC {tncPort}" + (ccdiPort is null ? string.Empty : $", radio CCDI {ccdiPort}"));
            Console.WriteLine("(note: the TXDELAY, SDM and pairing probes transmit briefly)");
            Console.WriteLine();
        }

        var options = new TuningDoctorOptions { Callsign = callsign, PinMode = mode };
        var results = await TuningDoctor.RunAsync(
            tncPort, ccdiPort, options,
            progress: json ? null : Console.WriteLine);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                results.Select(r => new
                {
                    r.Name,
                    Outcome = r.Outcome.ToString().ToLowerInvariant(),
                    r.Detail,
                    r.Remedy,
                }),
                JsonOptions));
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"{"probe",-26} {"outcome",-8} detail");
            foreach (var r in results)
            {
                Console.WriteLine($"{r.Name,-26} {TuningDoctor.OutcomeToken(r.Outcome),-8} {r.Detail}");
                if (r.Remedy is not null)
                {
                    Console.WriteLine($"{string.Empty,-26} {string.Empty,-8} → {r.Remedy}");
                }
            }
        }

        return results.Any(r => r.Outcome == DoctorOutcome.Fail) ? 1 : 0;
    }
}
