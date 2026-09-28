using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.ImportCatalog;

namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// HTTP implementation of <see cref="IScryfallBulkGateway"/>.
/// Discovers the live bulk file through metadata (never hardcoded timestamped
/// URLs), downloads the advertised file with streaming/bounded-memory parsing,
/// and reuses the existing catalog parser for eligibility and classification.
/// Keeps all network concerns outside <c>SimulationKernel</c>.
/// Respects Scryfall usage guidelines: descriptive User-Agent, Accept header,
/// single metadata + single download per user action, no background polling.
/// Docs: https://scryfall.com/docs/api/bulk-data
/// </summary>
public sealed class ScryfallBulkGateway : IScryfallBulkGateway
{
    public const string ScryfallDocsUrl = "https://scryfall.com/docs/api/bulk-data";

    private static readonly JsonSerializerOptions MetadataOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly IOptions<ScryfallBulkOptions> _options;

    public ScryfallBulkGateway(HttpClient http, IOptions<ScryfallBulkOptions> options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ScryfallImportSource> GetDefaultCardsSourceAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendMetadataRequestAsync(cancellationToken).ConfigureAwait(false);
        ScryfallBulkMetadataResponse envelope = await ReadMetadataEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        if (envelope.Data is not { Count: > 0 })
        {
            throw new ScryfallImportFailedException(
                "Scryfall returned an empty bulk-data listing. Try again later.");
        }

        return SelectSource(envelope.Data);
    }

    public async Task<IReadOnlyList<BulkCardRecord>> DownloadCardsAsync(
        ScryfallImportSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.DownloadUri))
        {
            throw new ScryfallImportFailedException("Scryfall did not advertise a bulk download URL.");
        }

        using HttpResponseMessage response = await SendDownloadRequestAsync(source, cancellationToken).ConfigureAwait(false);
        return await ReadDownloadContentAsync(response, source, cancellationToken).ConfigureAwait(false);
    }

    internal ScryfallImportSource SelectSource(IReadOnlyList<ScryfallBulkItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        string preferred = PreferredType;
        ScryfallBulkItem? match = FindPreferred(items, preferred);
        if (match is null)
        {
            throw new ScryfallImportFailedException(
                $"Scryfall no longer offers a '{preferred}' bulk file. See {ScryfallDocsUrl} for the current offerings.");
        }

        string? downloadUri = match.EffectiveDownloadUri;
        if (string.IsNullOrWhiteSpace(downloadUri))
        {
            throw new ScryfallImportFailedException(
                "Scryfall did not advertise a download URL for the card dataset. Try again later.");
        }

        return new ScryfallImportSource(
            match.Type ?? preferred,
            match.Name,
            match.UpdatedAt,
            downloadUri.Trim(),
            match.CompressedSize);
    }

    internal static async Task<IReadOnlyList<BulkCardRecord>> ParseBulkStreamAsync(
        Stream content,
        ScryfallImportSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            if (source.IsJsonLines)
            {
                return await BulkCatalogParser.ParseJsonLinesAsync(content, cancellationToken).ConfigureAwait(false);
            }

            return await BulkCatalogParser.ParseJsonAsync(content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ScryfallImportFailedException(
                "The Scryfall bulk file is malformed or uses an unsupported format. Try again later.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendMetadataRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, BulkDataUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new ScryfallUnavailableException(
                "Scryfall bulk data is unavailable. Check your internet connection and try again.", ex);
        }
    }

    private static async Task<ScryfallBulkMetadataResponse> ReadMetadataEnvelopeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new ScryfallUnavailableException(
                $"Scryfall bulk data is unavailable (HTTP {(int)response.StatusCode}). Try again later.");
        }

        try
        {
            using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            ScryfallBulkMetadataResponse? envelope =
                await JsonSerializer.DeserializeAsync<ScryfallBulkMetadataResponse>(content, MetadataOptions, cancellationToken).ConfigureAwait(false);
            return envelope ?? throw new ScryfallImportFailedException(
                "Scryfall returned malformed bulk-data metadata. Try again later.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ScryfallImportFailedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            throw new ScryfallImportFailedException(
                "Scryfall returned malformed bulk-data metadata. Try again later.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(
        ScryfallImportSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, source.DownloadUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new ScryfallUnavailableException(
                "Could not download card data from Scryfall. Check your internet connection and try again.", ex);
        }
    }

    private static async Task<IReadOnlyList<BulkCardRecord>> ReadDownloadContentAsync(
        HttpResponseMessage response,
        ScryfallImportSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.IsSuccessStatusCode)
        {
            throw new ScryfallUnavailableException(
                $"Scryfall download failed (HTTP {(int)response.StatusCode}). Try again later.");
        }

        try
        {
            using Stream network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (source.IsGzip)
            {
                using GZipStream gzip = new(network, CompressionMode.Decompress, leaveOpen: false);
                return await ParseBulkStreamAsync(gzip, source, cancellationToken).ConfigureAwait(false);
            }

            return await ParseBulkStreamAsync(network, source, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ScryfallImportFailedException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new ScryfallImportFailedException(
                "The Scryfall bulk file could not be decompressed or parsed. Try again later.", ex);
        }
        catch (IOException ex)
        {
            throw new ScryfallImportFailedException(
                "The Scryfall bulk file was truncated or unreadable. Try again later.", ex);
        }
    }

    private static ScryfallBulkItem? FindPreferred(IReadOnlyList<ScryfallBulkItem> items, string preferred)
    {
        foreach (ScryfallBulkItem item in items)
        {
            if (string.Equals(item.Type, preferred, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private string PreferredType
    {
        get
        {
            string configured = _options.Value.PreferredType?.Trim() ?? string.Empty;
            return configured.Length == 0 ? "default_cards" : configured;
        }
    }

    private string BulkDataUri
    {
        get
        {
            string configured = _options.Value.BulkDataUri?.Trim() ?? string.Empty;
            return configured.Length == 0 ? "https://api.scryfall.com/bulk-data" : configured;
        }
    }
}
