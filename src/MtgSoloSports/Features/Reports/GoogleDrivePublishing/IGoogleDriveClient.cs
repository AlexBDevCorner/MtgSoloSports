namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>Small typed Drive API client: metadata, content, name search, create/update, folders.</summary>
public interface IGoogleDriveClient
{
    Task<DriveFile?> GetFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default);

    Task<string?> DownloadFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DriveFile>> ListByNameInFolderAsync(
        string accessToken,
        string folderId,
        string name,
        CancellationToken cancellationToken = default);

    Task<DriveFile?> FindFolderAsync(
        string accessToken,
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default);

    Task<DriveFile> CreateFolderAsync(
        string accessToken,
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default);

    Task<DriveFile> CreateMarkdownAsync(
        string accessToken,
        string folderId,
        string name,
        string content,
        CancellationToken cancellationToken = default);

    Task<DriveFile> UpdateContentAsync(
        string accessToken,
        string fileId,
        string content,
        string? newName,
        CancellationToken cancellationToken = default);
}
