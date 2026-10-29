namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed record DriveFile(
    string Id,
    string Name,
    string? MimeType,
    IReadOnlyList<string> Parents,
    string? ModifiedTime,
    string? Md5Checksum,
    bool Trashed,
    string? WebViewLink);
