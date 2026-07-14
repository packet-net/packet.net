using System.Globalization;
using Packet.Node.Core.Api;
using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Core.Rigs;

/// <summary>
/// The hamlib rig model catalogue, read once from <c>rigctl -l</c> — the table behind
/// <c>GET /api/v1/rigs/models</c> and the number-resolution tier of the rig scan's suggestions.
/// The catalogue is static per hamlib install, so the parsed list is cached for the process
/// lifetime (thread-safe lazy); rigctl runs through the guarded <see cref="IProcessRunner"/>
/// seam, so a rigctl-less host is a clean <see cref="Available"/> <c>false</c>, never a throw.
/// </summary>
/// <remarks>
/// <c>rigctl -l</c> emits a fixed-width columnar table (hamlib 4.5.5):
/// <code>
///  Rig #  Mfg                    Model                   Version         Status      Macro
///      1  Hamlib                 Dummy                   20221128.0      Stable      RIG_MODEL_DUMMY
///   3073  Icom                   IC-7300                 20230109.10     Stable      RIG_MODEL_IC7300
/// </code>
/// Model names contain spaces (<c>MARK-V FT-1000MP</c>, <c>Dummy No VFO</c>) and column widths
/// drift across hamlib versions, so the parser derives the column offsets from the header line
/// rather than splitting on whitespace, and skips malformed lines defensively.
/// </remarks>
public sealed class RigModelCatalogue
{
    /// <summary>Env override for the rigctl binary name/path (default <c>rigctl</c> on PATH).</summary>
    public const string BinaryEnvVar = "PDN_RIGCTL_BIN";

    private readonly Lazy<(bool Available, IReadOnlyList<RigCatalogueModel> Models)> state;

    /// <summary>Build the catalogue over <paramref name="runner"/>. Nothing runs until the first
    /// read of <see cref="Available"/> / <see cref="Models"/>.</summary>
    public RigModelCatalogue(IProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        state = new(() => Load(runner));
    }

    /// <summary>Whether a usable catalogue was read: rigctl launched, exited 0, and at least one
    /// model line parsed. False (with empty <see cref="Models"/>) on a host without rigctl.</summary>
    public bool Available => state.Value.Available;

    /// <summary>The parsed model rows, in rigctl's order. Empty when not <see cref="Available"/>.</summary>
    public IReadOnlyList<RigCatalogueModel> Models => state.Value.Models;

    /// <summary>
    /// The model number for a (manufacturer, model) pair — the runtime name→number resolution the
    /// suggestion tier uses, so suggestions survive hamlib version skew. Exact-ish match:
    /// case-insensitive on both fields, trimmed. Null on no match, more than one match (refuse to
    /// guess), or an unavailable catalogue.
    /// </summary>
    public int? ResolveNumber(string manufacturer, string modelName)
    {
        if (string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }
        var mfg = manufacturer.Trim();
        var model = modelName.Trim();

        int? number = null;
        foreach (var row in Models)
        {
            if (string.Equals(row.Manufacturer, mfg, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                if (number is not null)
                {
                    return null; // ambiguous — two rows claim the same name
                }
                number = row.Number;
            }
        }
        return number;
    }

    private static (bool, IReadOnlyList<RigCatalogueModel>) Load(IProcessRunner runner)
    {
        var binary = Environment.GetEnvironmentVariable(BinaryEnvVar) is { Length: > 0 } b
            ? b
            : "rigctl";
        var result = runner.Run(binary, ["-l"]);
        if (!result.Succeeded)
        {
            return (false, []);
        }
        var models = Parse(result.StandardOutput);
        return (models.Count > 0, models);
    }

    /// <summary>Parse <c>rigctl -l</c> output. Internal so the parser is testable against a
    /// verbatim captured sample without shelling out.</summary>
    internal static IReadOnlyList<RigCatalogueModel> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var models = new List<RigCatalogueModel>();
        // Column start offsets, derived from the header line ("Rig #" ends where "Mfg" starts).
        int mfgStart = -1, modelStart = -1, versionStart = -1, statusStart = -1, macroStart = -1;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (mfgStart < 0)
            {
                // Still hunting for the header. Anything before it (warnings, blank lines) skips.
                mfgStart = line.IndexOf("Mfg", StringComparison.Ordinal);
                modelStart = line.IndexOf("Model", StringComparison.Ordinal);
                versionStart = line.IndexOf("Version", StringComparison.Ordinal);
                statusStart = line.IndexOf("Status", StringComparison.Ordinal);
                macroStart = line.IndexOf("Macro", StringComparison.Ordinal); // absent pre-4.5
                if (!line.Contains("Rig #", StringComparison.Ordinal)
                    || mfgStart < 0 || modelStart <= mfgStart
                    || versionStart <= modelStart || statusStart <= versionStart)
                {
                    mfgStart = -1; // not the header — keep looking
                }
                continue;
            }

            if (line.Length <= modelStart
                || !int.TryParse(line[..mfgStart], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var number))
            {
                continue; // blank / footer / malformed — skip defensively
            }

            var manufacturer = Slice(line, mfgStart, modelStart);
            var model = Slice(line, modelStart, versionStart);
            if (manufacturer.Length == 0 || model.Length == 0)
            {
                continue;
            }
            var status = Slice(line, statusStart, macroStart >= 0 ? macroStart : line.Length);
            models.Add(new RigCatalogueModel(
                number, manufacturer, model, status.Length > 0 ? status : null));
        }
        return models;
    }

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length)
        {
            return "";
        }
        return line[start..Math.Min(end, line.Length)].Trim();
    }
}
