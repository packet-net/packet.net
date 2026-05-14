using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Packet.Kiss.NinoTnc.Firmware;

namespace Packet.Kiss.NinoTnc.Tests.Firmware;

public class GitHubNinoTncFirmwareCatalogueTests
{
    /// <summary>
    /// A trimmed-down version of the JSON GitHub's contents API returns
    /// for <c>ninocarrillo/flashtnc@master</c>. Real responses have many
    /// more fields per entry; the catalogue only looks at three of them
    /// (<c>name</c>, <c>type</c>, <c>download_url</c>) so the fake omits
    /// the rest for readability.
    /// </summary>
    private const string SampleContents = """
        [
          {"name":"LICENSE","type":"file","download_url":"https://raw.githubusercontent.com/ninocarrillo/flashtnc/master/LICENSE"},
          {"name":"N9600A-v3-44.hex","type":"file","download_url":"https://raw.githubusercontent.com/ninocarrillo/flashtnc/master/N9600A-v3-44.hex"},
          {"name":"N9600A-v4-44.hex","type":"file","download_url":"https://raw.githubusercontent.com/ninocarrillo/flashtnc/master/N9600A-v4-44.hex"},
          {"name":"README.md","type":"file","download_url":"https://raw.githubusercontent.com/ninocarrillo/flashtnc/master/README.md"},
          {"name":"flashtnc.py","type":"file","download_url":"https://raw.githubusercontent.com/ninocarrillo/flashtnc/master/flashtnc.py"},
          {"name":"release-notes.txt","type":"file","download_url":"https://raw.githubusercontent.com/ninocarrillo/flashtnc/master/release-notes.txt"},
          {"name":"v3-44-mplab-checksums.txt","type":"file","download_url":"https://raw.githubusercontent.com/ninocarrillo/flashtnc/master/v3-44-mplab-checksums.txt"},
          {"name":"v4-44-mplab-checksums.txt","type":"file","download_url":"https://raw.githubusercontent.com/ninocarrillo/flashtnc/master/v4-44-mplab-checksums.txt"},
          {"name":"v44-op-modes.png","type":"file","download_url":"https://raw.githubusercontent.com/ninocarrillo/flashtnc/master/v44-op-modes.png"}
        ]
        """;

    [Fact]
    public async Task Lists_Both_Variants_From_Tip_Of_Master()
    {
        var catalogue = NewCatalogue(SampleContents);
        var releases = await catalogue.ListReleasesAsync();

        releases.Should().HaveCount(2);
        releases.Should().ContainSingle(r => r.ChipVariant == NinoTncChipVariant.Dspic33Ep256);
        releases.Should().ContainSingle(r => r.ChipVariant == NinoTncChipVariant.Dspic33Ep512);
    }

    [Fact]
    public async Task Each_Release_Carries_The_Right_Download_And_Checksum_Urls()
    {
        var catalogue = NewCatalogue(SampleContents);
        var releases = await catalogue.ListReleasesAsync();

        var v3 = releases.Single(r => r.ChipVariant == NinoTncChipVariant.Dspic33Ep256);
        v3.Version.Should().Be(new NinoTncFirmwareVersion(3, 44));
        v3.DownloadUrl.ToString().Should().EndWith("/N9600A-v3-44.hex");
        v3.MplabChecksumUrl.Should().NotBeNull();
        v3.MplabChecksumUrl!.ToString().Should().EndWith("/v3-44-mplab-checksums.txt");

        var v4 = releases.Single(r => r.ChipVariant == NinoTncChipVariant.Dspic33Ep512);
        v4.Version.Should().Be(new NinoTncFirmwareVersion(4, 44));
        v4.DownloadUrl.ToString().Should().EndWith("/N9600A-v4-44.hex");
        v4.MplabChecksumUrl!.ToString().Should().EndWith("/v4-44-mplab-checksums.txt");
    }

    [Fact]
    public async Task GetLatestForVariantAsync_Returns_The_Matching_Release()
    {
        var catalogue = NewCatalogue(SampleContents);

        var for256 = await catalogue.GetLatestForVariantAsync(NinoTncChipVariant.Dspic33Ep256);
        for256!.Version.Should().Be(new NinoTncFirmwareVersion(3, 44));

        var for512 = await catalogue.GetLatestForVariantAsync(NinoTncChipVariant.Dspic33Ep512);
        for512!.Version.Should().Be(new NinoTncFirmwareVersion(4, 44));
    }

    [Fact]
    public async Task GetLatestForVariantAsync_Unknown_Returns_Null()
    {
        var catalogue = NewCatalogue(SampleContents);
        var result = await catalogue.GetLatestForVariantAsync(NinoTncChipVariant.Unknown);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Empty_Repository_Yields_No_Releases()
    {
        var catalogue = NewCatalogue("[]");
        var releases = await catalogue.ListReleasesAsync();
        releases.Should().BeEmpty();
    }

    [Fact]
    public async Task Multiple_Releases_For_Same_Variant_Picks_Highest_Minor()
    {
        // Synthetic edge case: somebody pushed two v3-* hex files
        // simultaneously. The catalogue should pick the higher minor.
        const string json = """
            [
              {"name":"N9600A-v3-43.hex","type":"file","download_url":"https://example.test/v3-43.hex"},
              {"name":"N9600A-v3-44.hex","type":"file","download_url":"https://example.test/v3-44.hex"}
            ]
            """;
        var catalogue = NewCatalogue(json);
        var latest = await catalogue.GetLatestForVariantAsync(NinoTncChipVariant.Dspic33Ep256);
        latest!.Version.Should().Be(new NinoTncFirmwareVersion(3, 44));
    }

    [Fact]
    public async Task File_Names_Outside_The_N9600A_Pattern_Are_Ignored()
    {
        const string json = """
            [
              {"name":"N9600A.hex","type":"file","download_url":"https://example.test/N9600A.hex"},
              {"name":"someother-v3-44.hex","type":"file","download_url":"https://example.test/someother.hex"},
              {"name":"N9600A-v3-44.txt","type":"file","download_url":"https://example.test/wrongext.txt"}
            ]
            """;
        var catalogue = NewCatalogue(json);
        var releases = await catalogue.ListReleasesAsync();
        releases.Should().BeEmpty();
    }

    [Fact]
    public async Task Files_With_Major_Outside_3_4_Are_Skipped()
    {
        // Hypothetical N9600A-v5-* — we don't know the chip mapping for
        // major 5 yet, so we shouldn't guess.
        const string json = """
            [
              {"name":"N9600A-v5-1.hex","type":"file","download_url":"https://example.test/v5-1.hex"}
            ]
            """;
        var catalogue = NewCatalogue(json);
        var releases = await catalogue.ListReleasesAsync();
        releases.Should().BeEmpty();
    }

    private static GitHubNinoTncFirmwareCatalogue NewCatalogue(string responseJson)
    {
        var handler = new StubHttpMessageHandler(responseJson);
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("packet.net-test", "1.0"));
        return new GitHubNinoTncFirmwareCatalogue(http);
    }

    private sealed class StubHttpMessageHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            return Task.FromResult(response);
        }
    }
}
