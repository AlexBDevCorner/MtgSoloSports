using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Features.Catalog.ImportFromScryfall;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Catalog;

public sealed class ScryfallBulkGatewayTests
{
    [Fact]
    public async Task Metadata_SelectsDefaultCards_WithJsonlUri()
    {
        string metadata = MetadataEnvelope(
            Entry("oracle_cards", "Oracle Cards", "2026-09-28T09:01:57Z", "https://data.scryfall.io/oracle-cards/x.jsonl.gz", null),
            Entry("default_cards", "Default Cards", "2026-09-28T09:05:39Z", "https://data.scryfall.io/default-cards/default-cards-20260928090539.jsonl.gz", 78602874));

        ScryfallBulkGateway gateway = CreateGateway(MetadataHandler(metadata));
        ScryfallImportSource source = await gateway.GetDefaultCardsSourceAsync();

        source.Type.ShouldBe("default_cards");
        source.Name.ShouldBe("Default Cards");
        source.UpdatedAt.ShouldBe("2026-09-28T09:05:39Z");
        source.DownloadUri.ShouldBe("https://data.scryfall.io/default-cards/default-cards-20260928090539.jsonl.gz");
        source.CompressedSize.ShouldBe(78602874);
        source.IsJsonLines.ShouldBeTrue();
        source.IsGzip.ShouldBeTrue();
    }

    [Fact]
    public async Task Metadata_LegacyDownloadUri_IsAcceptedAsFallback()
    {
        string metadata = """
            {"object":"list","has_more":false,"data":[
              {"object":"bulk_data","id":"x","type":"default_cards","name":"Default Cards","description":"d","updated_at":"2026-01-01T00:00:00Z","uri":"https://api.scryfall.com/bulk-data/x","download_uri":"https://data.scryfall.io/legacy.json","compressed_size":10}
            ]}
            """;

        ScryfallBulkGateway gateway = CreateGateway(MetadataHandler(metadata));
        ScryfallImportSource source = await gateway.GetDefaultCardsSourceAsync();

        source.DownloadUri.ShouldBe("https://data.scryfall.io/legacy.json");
        source.IsJsonLines.ShouldBeFalse();
        source.IsGzip.ShouldBeFalse();
    }

    [Fact]
    public async Task Metadata_MissingDefaultCards_ThrowsFailed()
    {
        string metadata = MetadataEnvelope(
            Entry("oracle_cards", "Oracle Cards", "2026-09-28T09:01:57Z", "https://data.scryfall.io/oracle-cards/x.jsonl.gz", null));

        ScryfallBulkGateway gateway = CreateGateway(MetadataHandler(metadata));
        ScryfallImportFailedException ex = await Should.ThrowAsync<ScryfallImportFailedException>(
            () => gateway.GetDefaultCardsSourceAsync());
        ex.Message.ShouldContain("default_cards");
    }

    [Fact]
    public async Task Metadata_ServerError_ThrowsUnavailable()
    {
        ScryfallBulkGateway gateway = CreateGateway(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await Should.ThrowAsync<ScryfallUnavailableException>(() => gateway.GetDefaultCardsSourceAsync());
    }

    [Fact]
    public async Task Metadata_MalformedJson_ThrowsFailed()
    {
        ScryfallBulkGateway gateway = CreateGateway(MetadataHandler("not json"));
        await Should.ThrowAsync<ScryfallImportFailedException>(() => gateway.GetDefaultCardsSourceAsync());
    }

    [Fact]
    public async Task Download_JsonLinesPlain_ParsesRecords()
    {
        string jsonl =
            """{"name": "Serra Angel", "layout": "normal", "type_line": "Creature — Angel", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "m10", "image_uris": null}""" + "\n" +
            """{"name": "Wind Drake", "layout": "normal", "type_line": "Creature — Drake", "mana_cost": "{U}", "colors": ["U"], "keywords": [], "oracle_text": "", "set": "m10", "image_uris": null}""" + "\n";

        ScryfallBulkGateway gateway = CreateGateway(new StubHandler(request =>
        {
            string uri = request.RequestUri?.ToString() ?? string.Empty;
            return uri.Contains("bulk-data", StringComparison.Ordinal)
                ? JsonResponse(MetadataEnvelope(Entry("default_cards", "Default Cards", "2026-09-28T00:00:00Z", "https://data.test/cards.jsonl", null)))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jsonl, Encoding.UTF8, "application/json") };
        }));

        ScryfallImportSource source = await gateway.GetDefaultCardsSourceAsync();
        IReadOnlyList<BulkCardRecord> records = await gateway.DownloadCardsAsync(source);

        records.Count.ShouldBe(2);
        BulkCatalogParser.BuildAthletes(records).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Download_GzippedJsonLines_DecompressesAndParses()
    {
        string jsonl =
            """{"name": "Serra Angel", "layout": "normal", "type_line": "Creature — Angel", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "m10", "image_uris": null}""" + "\n";
        byte[] gzipped = Gzip(Encoding.UTF8.GetBytes(jsonl));

        ScryfallBulkGateway gateway = CreateGateway(new StubHandler(request =>
        {
            string uri = request.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("bulk-data", StringComparison.Ordinal))
            {
                return JsonResponse(MetadataEnvelope(Entry("default_cards", "Default Cards", "2026-09-28T00:00:00Z", "https://data.test/cards.jsonl.gz", null)));
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(gzipped),
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/gzip");
            return response;
        }));

        ScryfallImportSource source = await gateway.GetDefaultCardsSourceAsync();
        source.IsGzip.ShouldBeTrue();
        IReadOnlyList<BulkCardRecord> records = await gateway.DownloadCardsAsync(source);
        records.Count.ShouldBe(1);
        records[0].Name.ShouldBe("Serra Angel");
    }

    [Fact]
    public async Task Download_NotFound_ThrowsUnavailable()
    {
        ScryfallBulkGateway gateway = CreateGateway(new StubHandler(request =>
        {
            string uri = request.RequestUri?.ToString() ?? string.Empty;
            return uri.Contains("bulk-data", StringComparison.Ordinal)
                ? JsonResponse(MetadataEnvelope(Entry("default_cards", "Default Cards", "t", "https://data.test/cards.jsonl.gz", null)))
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        ScryfallImportSource source = await gateway.GetDefaultCardsSourceAsync();
        await Should.ThrowAsync<ScryfallUnavailableException>(() => gateway.DownloadCardsAsync(source));
    }

    [Fact]
    public async Task Download_MalformedJsonLines_ThrowsFailed()
    {
        ScryfallBulkGateway gateway = CreateGateway(new StubHandler(request =>
        {
            string uri = request.RequestUri?.ToString() ?? string.Empty;
            return uri.Contains("bulk-data", StringComparison.Ordinal)
                ? JsonResponse(MetadataEnvelope(Entry("default_cards", "Default Cards", "t", "https://data.test/cards.jsonl", null)))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json\n", Encoding.UTF8, "application/json") };
        }));

        ScryfallImportSource source = await gateway.GetDefaultCardsSourceAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => gateway.DownloadCardsAsync(source));
    }

    private static ScryfallBulkGateway CreateGateway(HttpMessageHandler handler)
    {
        HttpClient http = new(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MtgSoloSports-Tests/1.0");
        IOptions<ScryfallBulkOptions> options = Options.Create(new ScryfallBulkOptions());
        return new ScryfallBulkGateway(http, options);
    }

    private static StubHandler MetadataHandler(string body) =>
        new(_ => JsonResponse(body));

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string MetadataEnvelope(params string[] entries) =>
        """{"object":"list","has_more":false,"data":[""" + string.Join(",", entries) + "]}";

    private static string Entry(string type, string name, string updatedAt, string jsonlUri, long? size)
    {
        return JsonSerializer.Serialize(new
        {
            @object = "bulk_data",
            id = Guid.NewGuid().ToString("N"),
            type,
            name,
            description = "test",
            updated_at = updatedAt,
            uri = "https://api.scryfall.com/bulk-data/x",
            jsonl_download_uri = jsonlUri,
            compressed_size = size,
        });
    }

    private static byte[] Gzip(byte[] raw)
    {
        using MemoryStream output = new();
        using (System.IO.Compression.GZipStream gzip = new(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }
    }
}
