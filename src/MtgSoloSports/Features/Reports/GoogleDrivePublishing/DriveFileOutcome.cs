namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed record DriveFileOutcome(
    string Report,
    string Outcome,
    string? FileId,
    string? FileName,
    string? Fingerprint,
    string? WebViewLink,
    string? Detail);
