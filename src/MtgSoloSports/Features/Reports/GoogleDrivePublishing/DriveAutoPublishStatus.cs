namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Pure derivation of the user-facing automatic-publish state from the
/// app-level destination document plus connection liveness. Never implies
/// success: <c>UpToDate</c> requires the requested checkpoint to equal the
/// last successfully published checkpoint; any unmatched requested checkpoint
/// stays <c>Pending</c> or <c>Failed</c>. Publishing is an optional side
/// effect of read-only report generation and never affects sporting state.
/// </summary>
internal static class DriveAutoPublishStatus
{
    public const string Disabled = "Disabled";
    public const string UpToDate = "UpToDate";
    public const string Publishing = "Publishing";
    public const string Pending = "Pending";
    public const string Failed = "Failed";
    public const string Disconnected = "Disconnected";

    public static bool HasPendingRequest(
        DrivePublishingStore.DestinationDocument? destination)
    {
        if (destination is null || !destination.AutoRequestedSeason.HasValue)
        {
            return false;
        }

        return !IsCaughtUp(destination);
    }

    public static bool IsCaughtUp(DrivePublishingStore.DestinationDocument destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.AutoRequestedSeason.HasValue)
        {
            return true;
        }

        return destination.LastSeasonNumber == destination.AutoRequestedSeason
            && destination.LastGlobalStage == destination.AutoRequestedGlobalStage
            && string.Equals(
                destination.LastPhase,
                destination.AutoRequestedPhase,
                StringComparison.Ordinal);
    }

    public static string Compute(
        DrivePublishingStore.DestinationDocument? destination,
        bool connected,
        bool publishing)
    {
        if (destination is null || !destination.AutoPublishEnabled)
        {
            return Disabled;
        }

        if (!connected)
        {
            return Disconnected;
        }

        if (publishing)
        {
            return Publishing;
        }

        if (!HasPendingRequest(destination))
        {
            return UpToDate;
        }

        if (destination.AutoPausedForAuth)
        {
            return Disconnected;
        }

        if (!string.IsNullOrWhiteSpace(destination.AutoLastError))
        {
            return Failed;
        }

        return Pending;
    }
}
