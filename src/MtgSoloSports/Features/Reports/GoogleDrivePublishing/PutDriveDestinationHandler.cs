using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Explicit destination adoption: stores the mtshistory folder id and the two
/// report file ids/names for one save. Existing user-created files (including
/// manually named ones) are adopted by id — never guessed, renamed, moved or
/// deleted. Cross-save id reuse needs explicit confirmation. Offline-safe:
/// verification happens at publish time with actionable errors.
/// </summary>
public sealed class PutDriveDestinationHandler
{
    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly DriveAutoPublishTracker _tracker;
    private readonly TimeProvider _timeProvider;

    public PutDriveDestinationHandler(
        SaveStore saves,
        DrivePublishingStore publishing,
        DriveAutoPublishTracker? tracker = null,
        TimeProvider? timeProvider = null)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _tracker = tracker ?? new DriveAutoPublishTracker();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DriveDestinationResponse> HandleAsync(
        Guid saveId,
        PutDriveDestinationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        // Serialize with in-flight publishes on the shared per-save gate.
        // Publish holds this gate across its whole read/Drive-write/save
        // sequence; without it a PUT landing mid-publish would be silently
        // overwritten when FinishAsync saves its previously loaded document.
        SemaphoreSlim gate = DriveSaveGates.ForSave(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await HandleLockedAsync(saveId, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DriveDestinationResponse> HandleLockedAsync(
        Guid saveId,
        PutDriveDestinationRequest request,
        CancellationToken cancellationToken)
    {
        SaveStore.SaveDetailRecord detail = await LoadSaveAsync(saveId, cancellationToken).ConfigureAwait(false);
        NormalizedDestination normalized = NormalizeRequest(request);
        RequireFolderForAdoption(normalized);

        await EnsureNoCrossSaveReuseAsync(saveId, normalized, request.ConfirmCrossSave, cancellationToken).ConfigureAwait(false);

        DrivePublishingStore.DestinationDocument? existing =
            await _publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);
        bool destinationChanged = IsDestinationIdentityChanged(existing, normalized, detail.CurrentSeason);
        DrivePublishingStore.DestinationDocument updated = BuildUpdated(detail, existing, normalized, destinationChanged);
        if (destinationChanged && updated.AutoPublishEnabled && !updated.AutoPausedForAuth)
        {
            // A new folder/file id has never been published: the old success
            // baseline must not imply UpToDate for the new destination.
            // Make the pending request due immediately so both the signal
            // path and the periodic retry scan can pick it up.
            updated = updated with { AutoNextRetryUtc = _timeProvider.GetUtcNow() };
        }

        await _publishing.SaveDestinationAsync(updated, cancellationToken).ConfigureAwait(false);
        if (destinationChanged && updated.AutoPublishEnabled && DriveAutoPublishStatus.HasPendingRequest(updated))
        {
            _tracker.Signal(saveId);
        }

        return await ToResponseAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SaveStore.SaveDetailRecord> LoadSaveAsync(Guid saveId, CancellationToken cancellationToken)
    {
        await _saves.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await _saves.OpenAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        catch (SaveNotFoundException)
        {
            throw;
        }
    }

    private sealed record NormalizedDestination(
        string? FolderId,
        string? FolderName,
        string? SeasonsFolderId,
        string? DossierId,
        string? DossierName,
        string? AlmanacId,
        string? AlmanacName);

    private static NormalizedDestination NormalizeRequest(PutDriveDestinationRequest request)
    {
        return new NormalizedDestination(
            NormalizeId(request.MtshistoryFolderId, "mtshistory folder id"),
            NormalizeOptionalName(request.MtshistoryFolderName, "mtshistory folder name", allowWithoutExtension: true),
            NormalizeId(request.SeasonsFolderId, "seasons folder id"),
            NormalizeId(request.DossierFileId, "dossier file id"),
            NormalizeReportName(request.DossierFileName, "dossier file name"),
            NormalizeId(request.AlmanacFileId, "almanac file id"),
            NormalizeReportName(request.AlmanacFileName, "almanac file name"));
    }

    private static void RequireFolderForAdoption(NormalizedDestination normalized)
    {
        if (normalized.FolderId is null && (normalized.SeasonsFolderId is not null || normalized.DossierId is not null || normalized.AlmanacId is not null))
        {
            throw new DriveConflictException(
                "A destination mtshistory folder id is required when adopting report files. Paste the mtshistory folder id first, then the file ids.");
        }
    }

    private static bool IsDestinationIdentityChanged(
        DrivePublishingStore.DestinationDocument? existing,
        NormalizedDestination normalized,
        int currentSeason)
    {
        if (existing is null)
        {
            return false;
        }

        if (!string.Equals(existing.MtshistoryFolderId, normalized.FolderId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(existing.SeasonsFolderId, normalized.SeasonsFolderId, StringComparison.Ordinal))
        {
            return true;
        }

        (string? currentId, string? currentName, _, _) =
            DrivePublishingStore.CurrentDossierView(existing, currentSeason);
        if (!string.Equals(currentId, normalized.DossierId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(currentName, normalized.DossierName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(existing.AlmanacFileId, normalized.AlmanacId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(existing.AlmanacFileName, normalized.AlmanacName, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static DrivePublishingStore.DestinationDocument BuildUpdated(
        SaveStore.SaveDetailRecord detail,
        DrivePublishingStore.DestinationDocument? existing,
        NormalizedDestination normalized,
        bool destinationChanged)
    {
        // Changing a file id resets its baseline: the next publish adopts the
        // remote content deliberately instead of treating edits as conflicts.
        (string? currentId, _, string? currentFingerprint, string? currentModified) =
            DrivePublishingStore.CurrentDossierView(existing, detail.CurrentSeason);
        bool sameDossier = string.Equals(currentId, normalized.DossierId, StringComparison.Ordinal);
        bool sameAlmanac = string.Equals(existing?.AlmanacFileId, normalized.AlmanacId, StringComparison.Ordinal);

        string? dossierFingerprint = sameDossier ? currentFingerprint : null;
        string? dossierModified = sameDossier ? currentModified : null;
        Dictionary<int, DrivePublishingStore.DossierSlot> map =
            BuildDossierMap(existing, detail.CurrentSeason, normalized, dossierFingerprint, dossierModified, currentId);

        // A new folder/file id has never been published: the old success
        // baseline (Last* checkpoint) must not imply UpToDate for the new
        // destination. Clear it so HasPendingRequest stays true until the
        // new destination is actually uploaded. Keep the requested checkpoint
        // so the worker knows what durable state to publish.
        bool invalidateBaseline = destinationChanged;
        return new DrivePublishingStore.DestinationDocument(
            detail.SaveId,
            detail.Name,
            normalized.FolderId,
            normalized.FolderName,
            normalized.SeasonsFolderId,
            normalized.DossierId,
            normalized.DossierName,
            normalized.AlmanacId,
            normalized.AlmanacName,
            dossierFingerprint,
            sameAlmanac ? existing?.AlmanacFingerprint : null,
            dossierModified,
            sameAlmanac ? existing?.AlmanacRemoteModifiedTime : null,
            invalidateBaseline ? null : existing?.LastPublishedUtc,
            invalidateBaseline ? null : existing?.LastSeasonNumber,
            invalidateBaseline ? null : existing?.LastPhase,
            invalidateBaseline ? null : existing?.LastGlobalStage,
            invalidateBaseline ? null : existing?.LastDossierOutcome,
            invalidateBaseline ? null : existing?.LastAlmanacOutcome,
            invalidateBaseline ? null : existing?.LastError,
            map,
            existing?.AutoPublishEnabled ?? false,
            existing?.AutoRequestedSeason,
            existing?.AutoRequestedGlobalStage,
            existing?.AutoRequestedPhase,
            existing?.AutoRequestedUtc,
            invalidateBaseline ? 0 : existing?.AutoAttemptCount ?? 0,
            existing?.AutoNextRetryUtc,
            invalidateBaseline ? null : existing?.AutoLastError,
            existing?.AutoPausedForAuth ?? false);
    }

    private static Dictionary<int, DrivePublishingStore.DossierSlot> BuildDossierMap(
        DrivePublishingStore.DestinationDocument? existing,
        int currentSeason,
        NormalizedDestination normalized,
        string? dossierFingerprint,
        string? dossierModified,
        string? currentId)
    {
        Dictionary<int, DrivePublishingStore.DossierSlot> map = existing?.DossierBySeason is null
            ? []
            : new Dictionary<int, DrivePublishingStore.DossierSlot>(existing.DossierBySeason);
        MigrateLegacySlot(existing, currentSeason, currentId, map);
        if (normalized.DossierId is null && normalized.DossierName is null)
        {
            map.Remove(currentSeason);
        }
        else
        {
            map[currentSeason] = new DrivePublishingStore.DossierSlot(
                normalized.DossierId,
                normalized.DossierName,
                dossierFingerprint,
                dossierModified);
        }

        return map;
    }

    private static void MigrateLegacySlot(
        DrivePublishingStore.DestinationDocument? existing,
        int currentSeason,
        string? currentId,
        Dictionary<int, DrivePublishingStore.DossierSlot> map)
    {
        if (map.Count != 0 || existing?.DossierFileId is null)
        {
            return;
        }

        if (existing.LastSeasonNumber.HasValue && existing.LastSeasonNumber.Value != currentSeason)
        {
            map[existing.LastSeasonNumber.Value] = new DrivePublishingStore.DossierSlot(
                existing.DossierFileId,
                existing.DossierFileName,
                existing.DossierFingerprint,
                existing.DossierRemoteModifiedTime);
        }
        else if (currentId is not null)
        {
            map[currentSeason] = new DrivePublishingStore.DossierSlot(
                existing.DossierFileId,
                existing.DossierFileName,
                existing.DossierFingerprint,
                existing.DossierRemoteModifiedTime);
        }
    }

    private async Task<DriveDestinationResponse> ToResponseAsync(
        DrivePublishingStore.DestinationDocument updated,
        CancellationToken cancellationToken)
    {
        DrivePublishingStore.ConnectionDocument? connection =
            await _publishing.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        bool connected = connection is not null && !string.IsNullOrWhiteSpace(connection.RefreshToken);
        return ToResponse(
            updated,
            DriveAutoPublishStatus.Compute(updated, connected, _tracker.IsPublishing(updated.SaveId)));
    }

    private static DriveDestinationResponse ToResponse(
        DrivePublishingStore.DestinationDocument updated,
        string autoStatus)
    {
        return new DriveDestinationResponse(
            updated.SaveId,
            updated.SaveName,
            updated.MtshistoryFolderId,
            updated.MtshistoryFolderName,
            updated.SeasonsFolderId,
            updated.DossierFileId,
            updated.DossierFileName,
            updated.AlmanacFileId,
            updated.AlmanacFileName,
            updated.DossierFingerprint,
            updated.AlmanacFingerprint,
            updated.LastPublishedUtc,
            updated.LastSeasonNumber,
            updated.LastPhase,
            updated.LastGlobalStage,
            updated.LastError,
            updated.AutoPublishEnabled,
            autoStatus,
            updated.AutoRequestedSeason,
            updated.AutoRequestedGlobalStage,
            updated.AutoRequestedPhase);
    }

    private async Task EnsureNoCrossSaveReuseAsync(
        Guid saveId,
        NormalizedDestination normalized,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (confirmed)
        {
            return;
        }

        if (normalized.FolderId is null && normalized.DossierId is null && normalized.AlmanacId is null)
        {
            return;
        }

        IReadOnlyList<DrivePublishingStore.DestinationDocument> all =
            await _publishing.ListDestinationsAsync(cancellationToken).ConfigureAwait(false);
        foreach (DrivePublishingStore.DestinationDocument other in all)
        {
            if (other.SaveId == saveId)
            {
                continue;
            }

            if (IsReuse(other, normalized))
            {
                throw new DriveConflictException(
                    $"That destination is already mapped to save '{other.SaveName}' ({other.SaveId:D}). " +
                    "Publishing a different universe into it would mix histories. Confirm cross-save use explicitly to proceed.");
            }
        }
    }

    private static bool IsReuse(
        DrivePublishingStore.DestinationDocument other,
        NormalizedDestination normalized)
    {
        if (normalized.FolderId is not null && string.Equals(other.MtshistoryFolderId, normalized.FolderId, StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.SeasonsFolderId is not null && string.Equals(other.SeasonsFolderId, normalized.SeasonsFolderId, StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.AlmanacId is not null && string.Equals(other.AlmanacFileId, normalized.AlmanacId, StringComparison.Ordinal))
        {
            return true;
        }

        if (normalized.DossierId is not null
            && (string.Equals(other.DossierFileId, normalized.DossierId, StringComparison.Ordinal)
                || (other.DossierBySeason is not null
                    && other.DossierBySeason.Values.Any(s => string.Equals(s.FileId, normalized.DossierId, StringComparison.Ordinal)))))
        {
            return true;
        }

        return false;
    }

    private static string? NormalizeId(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > 256 || trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException($"Invalid {label}: Drive ids are opaque strings without whitespace.", nameof(value));
        }

        return trimmed;
    }

    private static string? NormalizeOptionalName(string? value, string label, bool allowWithoutExtension)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeName(value.Trim(), label, allowWithoutExtension);
    }

    private static string? NormalizeReportName(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeName(value.Trim(), label, allowWithoutExtension: false);
    }

    private static string NormalizeName(string trimmed, string label, bool allowWithoutExtension)
    {
        if (trimmed.Length == 0 || trimmed.Length > 200)
        {
            throw new ArgumentException($"Invalid {label}: keep it under 200 characters.", nameof(trimmed));
        }

        if (trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            throw new ArgumentException($"Invalid {label}: names must not contain path separators.", nameof(trimmed));
        }

        if (!allowWithoutExtension && !trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid {label}: report files must end with .md.", nameof(trimmed));
        }

        return trimmed;
    }
}
