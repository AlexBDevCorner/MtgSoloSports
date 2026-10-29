using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>Live Drive v3 implementation: files.get/list/create/update over HTTPS.</summary>
public sealed class GoogleDriveClient : IGoogleDriveClient
{
    private const string FilesBase = "https://www.googleapis.com/drive/v3/files";
    private const string UploadBase = "https://www.googleapis.com/upload/drive/v3/files";
    private const string FileFields = "id,name,mimeType,parents,modifiedTime,md5Checksum,trashed,webViewLink";

    private readonly HttpClient _http;

    public GoogleDriveClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<DriveFile?> GetFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get,
            $"{FilesBase}/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(FileFields)}&supportsAllDrives=false");
        AddBearer(request, accessToken);
        return await SendFileAsync(request, allowNotFound: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> DownloadFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get,
            $"{FilesBase}/{Uri.EscapeDataString(fileId)}?alt=media&supportsAllDrives=false");
        AddBearer(request, accessToken);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DriveUpstreamException("Google Drive is unreachable. Check internet access and retry.", ex);
        }

        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<DriveFile>> ListByNameInFolderAsync(
        string accessToken,
        string folderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        string escaped = name.Replace("'", "\\'", StringComparison.Ordinal);
        string query = $"'{folderId}' in parents and name = '{escaped}' and trashed = false";
        string url = $"{FilesBase}?q={Uri.EscapeDataString(query)}"
            + $"&fields={Uri.EscapeDataString($"files({FileFields})")}"
            + "&pageSize=10&supportsAllDrives=false";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        AddBearer(request, accessToken);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DriveUpstreamException("Google Drive is unreachable. Check internet access and retry.", ex);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseFileList(payload);
        }
    }

    public async Task<DriveFile?> FindFolderAsync(
        string accessToken,
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DriveFile> matches = await ListByNameInFolderAsync(accessToken, parentFolderId, name, cancellationToken).ConfigureAwait(false);
        List<DriveFile> folders = matches.Where(f => string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)).ToList();
        return folders.Count switch
        {
            0 => null,
            1 => folders[0],
            _ => throw new DriveConflictException(
                $"Found {folders.Count} folders named '{name}' in the destination. Adopt one explicitly by folder ID."),
        };
    }

    public async Task<DriveFile> CreateFolderAsync(
        string accessToken,
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        using StringContent metadata = new(
            JsonSerializer.Serialize(new { name, mimeType = "application/vnd.google-apps.folder", parents = new[] { parentFolderId } }),
            Encoding.UTF8,
            "application/json");
        using HttpRequestMessage request = new(HttpMethod.Post, $"{FilesBase}?fields={Uri.EscapeDataString(FileFields)}")
        {
            Content = metadata,
        };
        AddBearer(request, accessToken);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DriveUpstreamException("Google Drive is unreachable. Check internet access and retry.", ex);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseSingleFile(payload, "Google Drive returned an unreadable folder.");
        }
    }

    public async Task<DriveFile> CreateMarkdownAsync(
        string accessToken,
        string folderId,
        string name,
        string content,
        CancellationToken cancellationToken = default)
    {
        using MultipartContent multipart = BuildMultipart(name, folderId, content);
        using HttpRequestMessage request = new(HttpMethod.Post,
            $"{UploadBase}?uploadType=multipart&fields={Uri.EscapeDataString(FileFields)}")
        {
            Content = multipart,
        };
        AddBearer(request, accessToken);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DriveUpstreamException(
                "Upload timed out before Google confirmed the create. Re-run Publish now: it re-checks file identity before creating, so no duplicate is made.", ex);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return await ParseFileResponseAsync(response, "Google Drive returned an unreadable created file.", cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DriveFile> UpdateContentAsync(
        string accessToken,
        string fileId,
        string content,
        string? newName,
        CancellationToken cancellationToken = default)
    {
        using MultipartContent multipart = BuildMultipart(newName, null, content);
        using HttpRequestMessage request = new(new HttpMethod("PATCH"),
            $"{UploadBase}/{Uri.EscapeDataString(fileId)}?uploadType=multipart&fields={Uri.EscapeDataString(FileFields)}")
        {
            Content = multipart,
        };
        AddBearer(request, accessToken);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DriveUpstreamException(
                "Upload timed out before Google confirmed the update. Re-run Publish now: it re-checks file identity and content before writing again.", ex);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return await ParseFileResponseAsync(response, "Google Drive returned an unreadable updated file.", cancellationToken).ConfigureAwait(false);
        }
    }

    private static MultipartContent BuildMultipart(string? name, string? folderId, string content)
    {
        MultipartContent multipart = new("related");
        string metadataJson = folderId is null
            ? JsonSerializer.Serialize(new { name })
            : JsonSerializer.Serialize(new { name, parents = new[] { folderId }, mimeType = "text/markdown" });
        StringContent metadata = new(metadataJson, Encoding.UTF8, "application/json");
        ByteArrayContent media = new(Encoding.UTF8.GetBytes(content));
        media.Headers.ContentType = new MediaTypeHeaderValue("text/markdown") { CharSet = "utf-8" };
        multipart.Add(metadata);
        multipart.Add(media);
        return multipart;
    }

    private async Task<DriveFile?> SendFileAsync(HttpRequestMessage request, bool allowNotFound, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DriveUpstreamException("Google Drive is unreachable. Check internet access and retry.", ex);
        }

        using (response)
        {
            if (allowNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseSingleFile(payload, "Google Drive returned an unreadable file.");
        }
    }

    private static IReadOnlyList<DriveFile> ParseFileList(string payload)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(payload);
            List<DriveFile> files = [];
            if (json.RootElement.TryGetProperty("files", out JsonElement array))
            {
                foreach (JsonElement item in array.EnumerateArray())
                {
                    files.Add(ParseFile(item));
                }
            }

            return files;
        }
        catch (JsonException ex)
        {
            throw new DriveUpstreamException("Google Drive returned an unreadable file list.", ex);
        }
    }

    private static DriveFile ParseSingleFile(string payload, string error)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(payload);
            return ParseFile(json.RootElement);
        }
        catch (JsonException ex)
        {
            throw new DriveUpstreamException(error, ex);
        }
    }

    private static async Task<DriveFile> ParseFileResponseAsync(HttpResponseMessage response, string error, CancellationToken cancellationToken)
    {
        string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseSingleFile(payload, error);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string payload = await ReadErrorPayloadAsync(response, cancellationToken).ConfigureAwait(false);
        throw new DriveApiException(response.StatusCode, BuildApiMessage(response, payload));
    }

    private static async Task<string> ReadErrorPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return string.Empty;
        }
    }

    private static string BuildApiMessage(HttpResponseMessage response, string payload)
    {
        StringBuilder message = new($"Google Drive failed ({(int)response.StatusCode}).");
        string detail = ExtractApiError(payload);
        if (!string.IsNullOrEmpty(detail))
        {
            message.Append(detail);
        }

        return message.ToString();
    }

    private static string ExtractApiError(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement message)
                && message.GetString() is string text
                && !string.IsNullOrWhiteSpace(text))
            {
                string trimmed = text.Trim();
                return trimmed.Length > 200 ? " " + trimmed[..200] : " " + trimmed;
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    internal static DriveFile ParseFile(JsonElement element)
    {
        string id = element.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
        string name = element.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
        string? mimeType = element.TryGetProperty("mimeType", out JsonElement mimeElement) ? mimeElement.GetString() : null;
        List<string> parents = CollectParents(element);
        string? modifiedTime = element.TryGetProperty("modifiedTime", out JsonElement modifiedElement) ? modifiedElement.GetString() : null;
        string? md5 = element.TryGetProperty("md5Checksum", out JsonElement md5Element) ? md5Element.GetString() : null;
        bool trashed = element.TryGetProperty("trashed", out JsonElement trashedElement)
            && trashedElement.ValueKind == JsonValueKind.True;
        string? link = element.TryGetProperty("webViewLink", out JsonElement linkElement) ? linkElement.GetString() : null;
        return new DriveFile(id, name, mimeType, parents, modifiedTime, md5, trashed, link);
    }

    private static List<string> CollectParents(JsonElement element)
    {
        List<string> parents = [];
        if (element.TryGetProperty("parents", out JsonElement parentsElement) && parentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement parent in parentsElement.EnumerateArray())
            {
                if (parent.GetString() is string parentId && !string.IsNullOrWhiteSpace(parentId))
                {
                    parents.Add(parentId);
                }
            }
        }

        return parents;
    }

    private static void AddBearer(HttpRequestMessage request, string accessToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }
}
