namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-091: manual "Sync all changed reports". Null categories sync every
/// supported category; <c>OnlyKeys</c> restricts to explicit report keys
/// (selective refresh used by automatic mode). <c>DryRun</c> previews
/// create/update/unchanged/failed counts without any Drive write or
/// destination save. <c>MaxFiles</c> bounds one request; excess items report
/// <c>Truncated</c> so the client retries for the remainder (resumable.
/// </summary>
public sealed record DriveLibrarySyncRequest(
    bool ConfirmCrossSave,
    bool AllowOverwriteEditedRemote,
    bool AllowCreateMissing,
    bool DryRun = false,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? OnlyKeys = null,
    int? MaxFiles = null);
