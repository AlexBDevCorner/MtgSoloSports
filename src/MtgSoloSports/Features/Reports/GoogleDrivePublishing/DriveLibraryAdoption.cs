namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-091: explicit one-time adoption of a pre-existing Drive file for a
/// single library report. The key is semantic (<c>color:3</c> for an edition,
/// <c>color:red</c> for a team, season number string for a dossier); the file
/// id pins the exact remote object so publishing updates it in place instead
/// of creating a duplicate. A changed id resets the stored baseline so the
/// next sync adopts the remote content deliberately.
/// </summary>
public sealed record DriveLibraryAdoption(
    string? FileId,
    string? FileName);
