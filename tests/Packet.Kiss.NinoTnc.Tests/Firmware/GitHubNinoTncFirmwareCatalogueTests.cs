using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    [Fact]
    public async Task Dir_Entries_Are_Ignored_Even_If_Their_Name_Matches_The_Hex_Pattern()
    {
        // GitHub's contents API returns "dir" type entries alongside files.
        // Even if a directory happens to be named like a firmware hex
        // (unlikely in practice but defensible), it should not be treated
        // as a release.
        const string json = """
            [
              {"name":"N9600A-v3-44.hex","type":"dir","download_url":null},
              {"name":"N9600A-v3-44.hex","type":"file","download_url":"https://example.test/N9600A-v3-44.hex"}
            ]
            """;
        var catalogue = NewCatalogue(json);
        var releases = await catalogue.ListReleasesAsync();
        releases.Should().HaveCount(1);
        releases[0].DownloadUrl.Host.Should().Be("example.test");
    }

    [Fact]
    public async Task Hex_File_With_Null_Download_Url_Is_Skipped()
    {
        const string json = """
            [
              {"name":"N9600A-v3-44.hex","type":"file","download_url":null},
              {"name":"N9600A-v4-44.hex","type":"file","download_url":"https://example.test/N9600A-v4-44.hex"}
            ]
            """;
        var catalogue = NewCatalogue(json);
        var releases = await catalogue.ListReleasesAsync();
        releases.Should().HaveCount(1);
        releases[0].ChipVariant.Should().Be(NinoTncChipVariant.Dspic33Ep512);
    }

    [Fact]
    public async Task Missing_Checksum_Sibling_Leaves_MplabChecksumUrl_Null()
    {
        // Hex file present, no v3-44-mplab-checksums.txt sibling.
        const string json = """
            [
              {"name":"N9600A-v3-44.hex","type":"file","download_url":"https://example.test/N9600A-v3-44.hex"}
            ]
            """;
        var catalogue = NewCatalogue(json);
        var releases = await catalogue.ListReleasesAsync();
        releases.Should().HaveCount(1);
        releases[0].MplabChecksumUrl.Should().BeNull();
    }

    [Fact]
    public async Task Checksum_Sibling_With_Null_Download_Url_Still_Yields_Null_Checksum()
    {
        const string json = """
            [
              {"name":"N9600A-v3-44.hex","type":"file","download_url":"https://example.test/N9600A-v3-44.hex"},
              {"name":"v3-44-mplab-checksums.txt","type":"file","download_url":null}
            ]
            """;
        var catalogue = NewCatalogue(json);
        var releases = await catalogue.ListReleasesAsync();
        releases.Should().HaveCount(1);
        releases[0].MplabChecksumUrl.Should().BeNull();
    }

    [Fact]
    public async Task Non_2xx_Response_Propagates_HttpRequestException()
    {
        var handler = new ConfigurableHandler { StatusCode = HttpStatusCode.BadGateway, Body = "upstream down" };
        var catalogue = NewCatalogue(handler);
        var act = async () => await catalogue.ListReleasesAsync();
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Malformed_Json_Propagates_JsonException()
    {
        var catalogue = NewCatalogue("not-valid-json{");
        var act = async () => await catalogue.ListReleasesAsync();
        await act.Should().ThrowAsync<JsonException>();
    }

    [Fact]
    public async Task Network_Failure_Propagates()
    {
        var handler = new ConfigurableHandler { ThrowOnSend = new HttpRequestException("simulated DNS failure") };
        var catalogue = NewCatalogue(handler);
        var act = async () => await catalogue.ListReleasesAsync();
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*DNS failure*");
    }

    [Fact]
    public async Task Cancellation_Token_Is_Honoured()
    {
        // Hand the catalogue an already-cancelled token; the request
        // should never complete, the OperationCanceledException (or a
        // TaskCanceledException, which derives from it) should surface.
        var handler = new ConfigurableHandler { DelayMs = 5000, Body = "[]" };
        var catalogue = NewCatalogue(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await catalogue.ListReleasesAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Url_Includes_Configured_Owner_Repo_And_Branch()
    {
        var handler = new ConfigurableHandler { Body = "[]" };
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("test", "1.0"));
        var catalogue = new GitHubNinoTncFirmwareCatalogue(http,
            owner: "fork-owner",
            repo: "fork-repo",
            branch: "develop");

        await catalogue.ListReleasesAsync();

        handler.LastRequestUri.Should().NotBeNull();
        handler.LastRequestUri!.ToString().Should().Be("https://api.github.com/repos/fork-owner/fork-repo/contents?ref=develop");
    }

    [Fact]
    public void Constructor_Rejects_Null_HttpClient()
    {
        ((Action)(() => new GitHubNinoTncFirmwareCatalogue(null!)))
            .Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Constructor_Rejects_Empty_Owner(string? owner)
    {
        var http = new HttpClient();
        ((Action)(() => new GitHubNinoTncFirmwareCatalogue(http, owner: owner!)))
            .Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Constructor_Rejects_Empty_Repo(string? repo)
    {
        var http = new HttpClient();
        ((Action)(() => new GitHubNinoTncFirmwareCatalogue(http, repo: repo!)))
            .Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Constructor_Rejects_Empty_Branch(string? branch)
    {
        var http = new HttpClient();
        ((Action)(() => new GitHubNinoTncFirmwareCatalogue(http, branch: branch!)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task CheckForUpdateAsync_Dispatched_Through_Interface_Uses_Catalogue_State()
    {
        // Default-interface-method test: verify that
        // INinoTncFirmwareCatalogue.CheckForUpdateAsync works on the real
        // concrete class through an interface-typed reference. This
        // catches "default method exists but isn't dispatched" regressions
        // that the FakeCatalogue tests can mask.
        INinoTncFirmwareCatalogue catalogue = NewCatalogue(SampleContents);

        var availability = await catalogue.CheckForUpdateAsync(new NinoTncFirmwareVersion(3, 43));

        availability.CurrentVersion.Should().Be(new NinoTncFirmwareVersion(3, 43));
        availability.ChipVariant.Should().Be(NinoTncChipVariant.Dspic33Ep256);
        availability.UpdateAvailable.Should().BeTrue();
        availability.LatestAvailable!.Version.Should().Be(new NinoTncFirmwareVersion(3, 44));
    }

    private static GitHubNinoTncFirmwareCatalogue NewCatalogue(string responseJson) =>
        NewCatalogue(new StubHttpMessageHandler(responseJson));

    private static GitHubNinoTncFirmwareCatalogue NewCatalogue(HttpMessageHandler handler)
    {
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

    /// <summary>
    /// Configurable handler for testing error paths: status code, body,
    /// optional simulated network exception, optional delay (for cancellation
    /// tests), and request capture.
    /// </summary>
    private sealed class ConfigurableHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public string Body { get; init; } = "[]";
        public Exception? ThrowOnSend { get; init; }
        public int DelayMs { get; init; }
        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }
            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, cancellationToken).ConfigureAwait(false);
            }
            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }
}
