using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Packet.Kiss.NinoTnc;

namespace Packet.Node.Api;

/// <summary>
/// The modem-catalogue read surface: the fixed mode tables a port editor needs to offer a
/// modem's operating modes. Today that is the NinoTNC DIP-switch table
/// (<c>GET /api/v1/modems/nino-tnc/modes</c>), served straight out of
/// <see cref="NinoTncCatalog"/> so there is exactly ONE mode table in the product.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the web UI used to carry its OWN hand-written NinoTNC mode list, and it
/// was wrong: it ran 0-8 with invented names (mode 5 was labelled "9600 baud GFSK AX.25 (G3RUH)"
/// where a NinoTNC's mode 5 is in fact 3600 QPSK IL2P+CRC), so the Ports editor offered nine
/// fictional modes and wrote the operator's choice through to the TNC as a DIP-switch number
/// that meant something else entirely. The server's table is the researched one - reconciled
/// against Nino's own v44 mode table and the OARC wiki, and cross-checked by the
/// firmware-byte map in <see cref="NinoTncCatalog.FirmwareByteToMode"/> - so the fix is to
/// serve it rather than to re-type it in TypeScript and let it drift again.
/// </para>
/// <para>
/// Read-scoped and completely static: no serial I/O, no device, no config. It answers the same
/// 16 rows whether or not a NinoTNC is attached, which is what a port editor needs (an operator
/// configures a port for a TNC that is not plugged in yet). Modelled on
/// <c>GET /api/v1/rigs/models</c>, the other "catalogue the editor picks from" endpoint.
/// </para>
/// </remarks>
public static class PdnModemsApi
{
    /// <summary>
    /// One row of the NinoTNC mode table as the wire carries it. A projection of
    /// <see cref="NinoTncMode"/> plus the wide-channel flag, so the editor can warn that a mode
    /// wants a 25 kHz channel without shipping its own copy of that rule.
    /// </summary>
    /// <param name="Mode">DIP-switch position, 0-15.</param>
    /// <param name="Name">Nino's own name for the mode, e.g. "3600 QPSK IL2P+CRC".</param>
    /// <param name="BitRateHz">Raw bit rate; 0 for mode 15 ("Set from KISS"), which is variable.</param>
    /// <param name="RequiresWideChannel">
    /// True when the mode's published occupied bandwidth needs a wide (25 kHz) channel -
    /// see <see cref="NinoTncCatalog.WideChannelModes"/>.
    /// </param>
    public sealed record NinoTncModeRow(byte Mode, string Name, int BitRateHz, bool RequiresWideChannel);

    /// <summary>The NinoTNC table, projected once - it is a compile-time constant set.</summary>
    private static readonly IReadOnlyList<NinoTncModeRow> NinoTncModes =
        NinoTncCatalog.ByMode.Values
            .OrderBy(m => m.Mode)
            .Select(m => new NinoTncModeRow(m.Mode, m.Name, m.BitRateHz, NinoTncCatalog.RequiresWideChannel(m.Mode)))
            .ToArray();

    /// <summary>Map the modem-catalogue endpoints under <c>/api/v1</c>. Mapped before the SPA
    /// fallback so the specific routes win over the <c>/api/{**rest}</c> catch-all.</summary>
    public static void MapPdnModemsApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1").RequireAuthorization(PdnAuthPolicies.Read);

        // The NinoTNC DIP-switch mode table. Static, no query param - the client renders all 16.
        v1.MapGet("/modems/nino-tnc/modes", () => Results.Ok(new { modes = NinoTncModes }));
    }
}
