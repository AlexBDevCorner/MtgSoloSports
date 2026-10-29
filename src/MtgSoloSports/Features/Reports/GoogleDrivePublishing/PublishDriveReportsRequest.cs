namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed record PublishDriveReportsRequest(
    bool ConfirmCrossSave,
    bool AllowOverwriteEditedRemote);
