namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>Safe Drive DTOs: ids, names, fingerprints and outcomes only. Tokens never leave the server.</summary>
public sealed record DriveReportSlotDto(
    string? FileId,
    string? FileName,
    string? Fingerprint,
    string? RemoteModifiedTime,
    string? LastOutcome,
    string? WebViewLink);
