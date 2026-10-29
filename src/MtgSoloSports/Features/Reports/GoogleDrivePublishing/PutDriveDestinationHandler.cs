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

    public PutDriveDestinationHandler(SaveStore saves, DrivePublishingStore publishing)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
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

        SaveStore.SaveDetailRecord detail = await LoadSaveAsync(saveId, cancellationToken).ConfigureAwait(false);
        NormalizedDestination normalized = NormalizeRequest(request);
        RequireFolderForAdoption(normalized);

        await EnsureNoCrossSaveReuseAsync(saveId, normalized, request.ConfirmCrossSave, cancellationToken).ConfigureAwait(false);

        DrivePublishingStore.DestinationDocument? existing =
            await _publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);
        DrivePublishingStore.DestinationDocument updated = BuildUpdated(detail, existing, normalized);
        await _publishing.SaveDestinationAsync(updated, cancellationToken).ConfigureAwait(false);

        return ToResponse(updated);
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
        string? DossierId,
        string? DossierName,
        string? AlmanacId,
        string? AlmanacName);

    private static NormalizedDestination NormalizeRequest(PutDriveDestinationRequest request)
    {
        return new NormalizedDestination(
            NormalizeId(request.MtshistoryFolderId, "mtshistory folder id"),
            NormalizeOptionalName(request.MtshistoryFolderName, "mtshistory folder name", allowWithoutExtension: true),
            NormalizeId(request.DossierFileId, "dossier file id"),
            NormalizeReportName(request.DossierFileName, "dossier file name"),
            NormalizeId(request.AlmanacFileId, "almanac file id"),
            NormalizeReportName(request.AlmanacFileName, "almanac file name"));
    }

    private static void RequireFolderForAdoption(NormalizedDestination normalized)
    {
        if (normalized.FolderId is null && (normalized.DossierId is not null || normalized.AlmanacId is not null))
        {
            throw new DriveConflictException(
                "A destination mtshistory folder id is required when adopting report files. Paste the mtshistory folder id first, then the file ids.");
        }
    }

    private static DrivePublishingStore.DestinationDocument BuildUpdated(
        SaveStore.SaveDetailRecord detail,
        DrivePublishingStore.DestinationDocument? existing,
        NormalizedDestination normalized)
    {
        // Changing a file id resets its baseline: the next publish adopts the
        // remote content deliberately instead of treating edits as conflicts.
        bool sameDossier = string.Equals(existing?.DossierFileId, normalized.DossierId, StringComparison.Ordinal);
        bool sameAlmanac = string.Equals(existing?.AlmanacFileId, normalized.AlmanacId, StringComparison.Ordinal);
        return new DrivePublishingStore.DestinationDocument(
            detail.SaveId,
            detail.Name,
            normalized.FolderId,
            normalized.FolderName,
            existing?.SeasonsFolderId,
            normalized.DossierId,
            normalized.DossierName,
            normalized.AlmanacId,
            normalized.AlmanacName,
            sameDossier ? existing?.DossierFingerprint : null,
            sameAlmanac ? existing?.AlmanacFingerprint : null,
            sameDossier ? existing?.DossierRemoteModifiedTime : null,
            sameAlmanac ? existing?.AlmanacRemoteModifiedTime : null,
            existing?.LastPublishedUtc,
            existing?.LastSeasonNumber,
            existing?.LastPhase,
            existing?.LastGlobalStage,
            existing?.LastDossierOutcome,
            existing?.LastAlmanacOutcome,
            existing?.LastError);
    }

    private static DriveDestinationResponse ToResponse(DrivePublishingStore.DestinationDocument updated)
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
            updated.LastError);
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
        return (normalized.FolderId is not null && string.Equals(other.MtshistoryFolderId, normalized.FolderId, StringComparison.Ordinal))
            || (normalized.DossierId is not null && string.Equals(other.DossierFileId, normalized.DossierId, StringComparison.Ordinal))
            || (normalized.AlmanacId is not null && string.Equals(other.AlmanacFileId, normalized.AlmanacId, StringComparison.Ordinal));
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
