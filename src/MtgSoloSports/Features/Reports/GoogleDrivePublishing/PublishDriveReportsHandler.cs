using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Reports.SeasonDossier;
using MtgSoloSports.Features.Reports.UniverseAlmanac;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// One-click publishing: regenerates the current season dossier + universe
/// almanac from persisted reports (same bytes as the download endpoints) and
/// uploads the Markdown payloads to the save's adopted Drive destination.
/// Update-in-place by stored file id; content-fingerprint idempotency (the
/// volatile Generated timestamp is ignored); duplicate/ambiguous names stop
/// with adoption guidance; independently edited remotes need explicit
/// overwrite confirmation. Read-only toward simulation: network failures never
/// mutate sporting state.
/// </summary>
public sealed class PublishDriveReportsHandler
{
    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly IGoogleOAuthClient _oauth;
    private readonly IGoogleDriveClient _drive;
    private readonly IOptions<DrivePublishingOptions> _options;
    private readonly GetSeasonDossierHandler _dossier;
    private readonly GetUniverseAlmanacHandler _almanac;
    private readonly GetSeasonStatusHandler _status;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PublishDriveReportsHandler> _logger;

    public PublishDriveReportsHandler(
        SaveStore saves,
        DrivePublishingStore publishing,
        IGoogleOAuthClient oauth,
        IGoogleDriveClient drive,
        IOptions<DrivePublishingOptions> options,
        GetSeasonDossierHandler dossier,
        GetUniverseAlmanacHandler almanac,
        GetSeasonStatusHandler status,
        TimeProvider timeProvider,
        ILogger<PublishDriveReportsHandler> logger)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        _drive = drive ?? throw new ArgumentNullException(nameof(drive));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dossier = dossier ?? throw new ArgumentNullException(nameof(dossier));
        _almanac = almanac ?? throw new ArgumentNullException(nameof(almanac));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private sealed record PublishContext(
        SaveStore.SaveDetailRecord Detail,
        DrivePublishingStore.DestinationDocument Destination,
        GetSeasonStatusResponse SeasonStatus);

    private sealed record ReportSnapshots(
        string DossierMarkdown,
        string AlmanacMarkdown,
        string DossierFingerprint,
        string AlmanacFingerprint,
        string DossierName,
        string AlmanacName);

    public async Task<PublishDriveReportsResponse> HandleAsync(
        Guid saveId,
        PublishDriveReportsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        PublishContext context = await LoadContextAsync(saveId, request.ConfirmCrossSave, cancellationToken).ConfigureAwait(false);
        ReportSnapshots snapshots = await GenerateSnapshotsAsync(context, cancellationToken).ConfigureAwait(false);
        (List<DriveFileOutcome> Outcomes, DrivePublishingStore.DestinationDocument Current) published =
            await PublishBothAsync(context, snapshots, request.AllowOverwriteEditedRemote, cancellationToken).ConfigureAwait(false);
        return await FinishAsync(context, snapshots, published.Outcomes, published.Current, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PublishContext> LoadContextAsync(Guid saveId, bool confirmCrossSave, CancellationToken cancellationToken)
    {
        if (!_options.Value.IsGoogleConfigured)
        {
            throw new DriveNotConfiguredException(
                "Google Drive publishing is not configured. Set the DrivePublishing Google client id/secret on the server (see docs/drive-publishing.md), then retry.");
        }

        await _saves.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        SaveStore.SaveDetailRecord detail;
        try
        {
            detail = await _saves.OpenAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        catch (SaveNotFoundException)
        {
            throw;
        }

        DrivePublishingStore.DestinationDocument? destination =
            await _publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (destination?.MtshistoryFolderId is null)
        {
            throw new DriveConflictException(
                "No Drive destination is configured for this save. Set the mtshistory folder id (and adopt the dossier/almanac files) in destination settings first.");
        }

        await EnsureNoCrossSaveReuseAsync(destination, confirmCrossSave, cancellationToken).ConfigureAwait(false);

        GetSeasonStatusResponse seasonStatus;
        try
        {
            seasonStatus = await _status.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        catch (SaveNotFoundException)
        {
            throw;
        }

        return new PublishContext(detail, destination, seasonStatus);
    }

    private async Task<ReportSnapshots> GenerateSnapshotsAsync(PublishContext context, CancellationToken cancellationToken)
    {
        GetSeasonDossierHandler.ReportFile dossierFile;
        GetUniverseAlmanacHandler.ReportFile almanacFile;
        try
        {
            dossierFile = await _dossier.HandleAsync(context.Detail.SaveId, context.Detail.CurrentSeason, "md", cancellationToken).ConfigureAwait(false);
            almanacFile = await _almanac.HandleAsync(context.Detail.SaveId, "md", cancellationToken).ConfigureAwait(false);
        }
        catch (SeasonDossierNotFoundException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (UniverseAlmanacNotFoundException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        string dossierMarkdown = Encoding.UTF8.GetString(dossierFile.Content);
        string almanacMarkdown = Encoding.UTF8.GetString(almanacFile.Content);
        string stableDossierName = ReportFilenames.SeasonDossierFileName(context.Detail.CurrentSeason, "md");
        (_, string dossierName) = DrivePublishingStore.ResolveDossierForSeason(
            context.Destination,
            context.Detail.CurrentSeason,
            stableDossierName);
        string almanacName = string.IsNullOrWhiteSpace(context.Destination.AlmanacFileName)
            ? ReportFilenames.UniverseAlmanacFileName(context.Detail.Name, "md")
            : context.Destination.AlmanacFileName;
        return new ReportSnapshots(
            dossierMarkdown,
            almanacMarkdown,
            DrivePublishingFingerprint.ComputeHex(dossierMarkdown),
            DrivePublishingFingerprint.ComputeHex(almanacMarkdown),
            dossierName,
            almanacName);
    }

    private async Task<(List<DriveFileOutcome> Outcomes, DrivePublishingStore.DestinationDocument Current)> PublishBothAsync(
        PublishContext context,
        ReportSnapshots snapshots,
        bool allowOverwriteEditedRemote,
        CancellationToken cancellationToken)
    {
        string seasonsFolderId = await EnsureSeasonsFolderAsync(context.Destination, cancellationToken).ConfigureAwait(false);
        DrivePublishingStore.DestinationDocument current =
            await _publishing.GetDestinationAsync(context.Detail.SaveId, cancellationToken).ConfigureAwait(false)
            ?? context.Destination;

        // Per-season dossier identity: never reuse a prior season's file id for
        // a new season. Resolve the current season's slot (migrating legacy
        // data) so season N+1 creates Seasons/season-N+1-dossier.md while the
        // prior season's Drive file and mapping are preserved.
        string stableDossierName = ReportFilenames.SeasonDossierFileName(context.Detail.CurrentSeason, "md");
        (string? dossierFileId, string dossierName) = DrivePublishingStore.ResolveDossierForSeason(
            current,
            context.Detail.CurrentSeason,
            stableDossierName);
        // Snapshots were generated from the load-time destination; if the
        // destination changed concurrently, prefer the freshly loaded mapping
        // but keep the already-generated Markdown payload.
        string effectiveDossierName = string.Equals(snapshots.DossierName, dossierName, StringComparison.Ordinal)
            ? snapshots.DossierName
            : dossierName;
        (string? dossierFingerprint, string? dossierLast) = ResolveDossierFingerprints(current, context.Detail.CurrentSeason);

        List<DriveFileOutcome> outcomes = [];
        (DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated) dossierResult =
            await PublishOneAsync(current, "dossier", dossierFileId, effectiveDossierName,
                seasonsFolderId, snapshots.DossierMarkdown, snapshots.DossierFingerprint,
                dossierLast, allowOverwriteEditedRemote, context.Detail.CurrentSeason, cancellationToken).ConfigureAwait(false);
        outcomes.Add(dossierResult.Outcome);
        current = dossierResult.Updated;

        (DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated) almanacResult =
            await PublishOneAsync(current, "almanac", current.AlmanacFileId, snapshots.AlmanacName,
                current.MtshistoryFolderId!, snapshots.AlmanacMarkdown, snapshots.AlmanacFingerprint,
                current.AlmanacFingerprint, allowOverwriteEditedRemote, null, cancellationToken).ConfigureAwait(false);
        outcomes.Add(almanacResult.Outcome);
        current = almanacResult.Updated;
        return (outcomes, current);
    }

    private static (string? Fingerprint, string? Last) ResolveDossierFingerprints(
        DrivePublishingStore.DestinationDocument destination,
        int currentSeason)
    {
        if (destination.DossierBySeason is not null
            && destination.DossierBySeason.TryGetValue(currentSeason, out DrivePublishingStore.DossierSlot? slot))
        {
            return (slot.Fingerprint, slot.Fingerprint);
        }

        if (destination.DossierBySeason is not null && destination.DossierBySeason.Count > 0)
        {
            return (null, null);
        }

        if (destination.DossierFileId is not null
            && destination.LastSeasonNumber.HasValue
            && destination.LastSeasonNumber.Value != currentSeason)
        {
            return (null, null);
        }

        return (destination.DossierFingerprint, destination.DossierFingerprint);
    }

    private async Task<PublishDriveReportsResponse> FinishAsync(
        PublishContext context,
        ReportSnapshots snapshots,
        List<DriveFileOutcome> outcomes,
        DrivePublishingStore.DestinationDocument current,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool anySettled = outcomes.Any(o => !string.Equals(o.Outcome, "failed", StringComparison.Ordinal));
        string? firstError = outcomes
            .Where(o => string.Equals(o.Outcome, "failed", StringComparison.Ordinal))
            .Select(o => $"{o.Report}: {o.Detail}")
            .FirstOrDefault();

        DrivePublishingStore.DestinationDocument finished = current with
        {
            SaveName = context.Detail.Name,
            LastPublishedUtc = anySettled ? now : current.LastPublishedUtc,
            LastSeasonNumber = anySettled ? context.Detail.CurrentSeason : current.LastSeasonNumber,
            LastPhase = anySettled ? context.Detail.Phase : current.LastPhase,
            LastGlobalStage = anySettled ? context.SeasonStatus.GlobalStage : current.LastGlobalStage,
            LastDossierOutcome = outcomes[0].Outcome,
            LastAlmanacOutcome = outcomes[1].Outcome,
            LastError = firstError,
        };
        // Preserve per-season dossier history: file ids/names are maintained by
        // WithSlot only for settled outcomes. Do not overwrite the current
        // season's legacy name with an attempted name when the publish failed,
        // otherwise a stale id could be paired with a new-season name.
        await _publishing.SaveDestinationAsync(finished, cancellationToken).ConfigureAwait(false);

        return new PublishDriveReportsResponse(
            context.Detail.SaveId,
            context.Detail.CurrentSeason,
            context.Detail.Phase,
            context.SeasonStatus.GlobalStage,
            now,
            outcomes);
    }

    private async Task EnsureNoCrossSaveReuseAsync(
        DrivePublishingStore.DestinationDocument destination,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (confirmed)
        {
            return;
        }

        IReadOnlyList<DrivePublishingStore.DestinationDocument> all =
            await _publishing.ListDestinationsAsync(cancellationToken).ConfigureAwait(false);
        foreach (DrivePublishingStore.DestinationDocument other in all)
        {
            if (other.SaveId != destination.SaveId && IsReuse(other, destination))
            {
                throw new DriveConflictException(
                    $"That destination is already mapped to save '{other.SaveName}' ({other.SaveId:D}). " +
                    "Re-run with cross-save confirmation to publish this universe into a folder used by another save.");
            }
        }
    }

    private static bool IsReuse(
        DrivePublishingStore.DestinationDocument other,
        DrivePublishingStore.DestinationDocument destination)
    {
        if (destination.MtshistoryFolderId is not null
            && string.Equals(other.MtshistoryFolderId, destination.MtshistoryFolderId, StringComparison.Ordinal))
        {
            return true;
        }

        if (destination.AlmanacFileId is not null
            && string.Equals(other.AlmanacFileId, destination.AlmanacFileId, StringComparison.Ordinal))
        {
            return true;
        }

        if (destination.DossierFileId is not null
            && (string.Equals(other.DossierFileId, destination.DossierFileId, StringComparison.Ordinal)
                || (other.DossierBySeason is not null
                    && other.DossierBySeason.Values.Any(s => string.Equals(s.FileId, destination.DossierFileId, StringComparison.Ordinal)))))
        {
            return true;
        }

        if (destination.DossierBySeason is not null)
        {
            foreach (DrivePublishingStore.DossierSlot slot in destination.DossierBySeason.Values)
            {
                if (slot.FileId is null)
                {
                    continue;
                }

                if (string.Equals(other.DossierFileId, slot.FileId, StringComparison.Ordinal))
                {
                    return true;
                }

                if (other.DossierBySeason is not null
                    && other.DossierBySeason.Values.Any(s => string.Equals(s.FileId, slot.FileId, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<string> EnsureSeasonsFolderAsync(
        DrivePublishingStore.DestinationDocument destination,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(destination.SeasonsFolderId))
        {
            DriveFile? cached = await WithAuthAsync(
                token => _drive.GetFileAsync(token, destination.SeasonsFolderId!, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (cached is not null
                && !cached.Trashed
                && cached.Parents.Contains(destination.MtshistoryFolderId!, StringComparer.Ordinal))
            {
                return cached.Id;
            }
        }

        DriveFile? existing = await WithAuthAsync(
            token => _drive.FindFolderAsync(token, destination.MtshistoryFolderId!, "Seasons", cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await _publishing.SaveDestinationAsync(destination with { SeasonsFolderId = existing.Id }, cancellationToken).ConfigureAwait(false);
            return existing.Id;
        }

        DriveFile created = await WithAuthAsync(
            token => _drive.CreateFolderAsync(token, destination.MtshistoryFolderId!, "Seasons", cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await _publishing.SaveDestinationAsync(destination with { SeasonsFolderId = created.Id }, cancellationToken).ConfigureAwait(false);
        return created.Id;
    }

    private async Task<(DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated)> PublishOneAsync(
        DrivePublishingStore.DestinationDocument destination,
        string report,
        string? storedFileId,
        string desiredName,
        string parentFolderId,
        string markdown,
        string newFingerprint,
        string? lastFingerprint,
        bool allowOverwriteEditedRemote,
        int? dossierSeason,
        CancellationToken cancellationToken)
    {
        if (storedFileId is null)
        {
            return await CreateNewAsync(destination, report, desiredName, parentFolderId, markdown, newFingerprint, dossierSeason, cancellationToken).ConfigureAwait(false);
        }

        (DriveFile? remote, DriveFileOutcome? denied) = await LoadRemoteFileAsync(report, storedFileId, desiredName, newFingerprint, cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return (denied, destination);
        }

        if (remote is null)
        {
            // Defensive fail-closed: an adopted id must never silently fall
            // through to create. LoadRemoteFileAsync already returns a failed
            // outcome for missing/trashed ids; this guards future paths.
            return (Failed(report, storedFileId, desiredName,
                $"The adopted {report} file id {storedFileId} is no longer visible in Drive (deleted, trashed, or never granted to this app — drive.file hides ungranted files as missing). Publishing did not create a duplicate. Clear the id to create a new '{desiredName}' in the destination, or re-adopt a granted file id, then retry. Nothing was changed.", newFingerprint),
                destination);
        }

        // P1: never update a file outside the configured destination. An
        // adopted id is opaque; without a parent/MIME check a pasted id could
        // overwrite (and rename) unrelated Drive content.
        DriveFileOutcome? locationDenied = ValidateRemoteLocation(report, remote, desiredName, parentFolderId, newFingerprint);
        if (locationDenied is not null)
        {
            return (locationDenied, destination);
        }

        string? remoteFingerprint = await LoadRemoteFingerprintAsync(remote, cancellationToken).ConfigureAwait(false);
        if (string.Equals(remoteFingerprint, "denied", StringComparison.Ordinal))
        {
            return (Failed(report, remote.Id, desiredName,
                "Google denied access to read the adopted file (403). With drive.file the app needs an explicit Choose from Drive (Google Picker) grant for pre-existing files. Use Verify access, re-pick the file with Choose from Drive, or adopt a file it created. Nothing was changed.", newFingerprint),
                destination);
        }

        if (string.Equals(newFingerprint, remoteFingerprint, StringComparison.Ordinal))
        {
            return (Unchanged(report, remote, desiredName, newFingerprint), WithSlot(destination, report, remote.Id, desiredName, newFingerprint, remote.ModifiedTime, dossierSeason));
        }

        if (IsStaleRemoteEdit(lastFingerprint, remoteFingerprint, newFingerprint) && !allowOverwriteEditedRemote)
        {
            return (Failed(report, remote.Id, desiredName,
                "The Drive file was edited independently since the last publish. Re-run with overwrite confirmation to replace those edits, or adopt a different file. Nothing was changed.", newFingerprint),
                destination);
        }

        return await UpdateRemoteAsync(destination, report, remote, desiredName, markdown, newFingerprint, dossierSeason, cancellationToken).ConfigureAwait(false);
    }

    private static DriveFileOutcome? ValidateRemoteLocation(
        string report,
        DriveFile remote,
        string desiredName,
        string parentFolderId,
        string newFingerprint)
    {
        if (string.Equals(remote.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
        {
            return Failed(report, remote.Id, desiredName,
                $"The adopted {report} id {remote.Id} is a Drive folder, not a Markdown file. Adopt a Markdown file id inside the configured destination, or clear the id to create a new file. Nothing was changed.",
                newFingerprint);
        }

        if (!remote.Parents.Contains(parentFolderId, StringComparer.Ordinal))
        {
            string expected = string.Equals(report, "dossier", StringComparison.Ordinal)
                ? "the Seasons/ subfolder of the configured mtshistory folder"
                : "the configured mtshistory folder";
            return Failed(report, remote.Id, desiredName,
                $"The adopted {report} file '{remote.Name}' (id {remote.Id}) is outside the configured destination; expected it directly under {expected}. Adopt a file inside the destination or clear the id to create a new one. Nothing was changed.",
                newFingerprint);
        }

        return null;
    }

    private async Task<(DriveFile? Remote, DriveFileOutcome? Denied)> LoadRemoteFileAsync(
        string report,
        string storedFileId,
        string desiredName,
        string newFingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            DriveFile? remote = await WithGetRetryAsync(
                () => WithAuthAsync(token => _drive.GetFileAsync(token, storedFileId, cancellationToken), cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (remote is null)
            {
                // Fail closed: with drive.file an existing but ungranted file
                // is hidden as 404, and listing may omit it too. Creating a
                // replacement here would leave two competing reports.
                return (null, Failed(report, storedFileId, desiredName,
                    $"The adopted {report} file id {storedFileId} was not found in Drive (deleted, or never granted to this app — drive.file hides ungranted files as missing). Publishing did not create a duplicate. Clear the id to create a new '{desiredName}' in the destination, or re-adopt a granted file id (use Choose from Drive), then retry. Nothing was changed.", newFingerprint));
            }

            if (remote.Trashed)
            {
                return (null, Failed(report, storedFileId, desiredName,
                    $"The adopted {report} file '{remote.Name}' (id {storedFileId}) is trashed in Drive. Publishing did not create a duplicate. Restore it in Drive, or clear the id to create a new '{desiredName}' in the destination, then retry. Nothing was changed.", newFingerprint));
            }

            return (remote, null);
        }
        catch (DriveApiException ex) when (IsForbidden(ex))
        {
            DriveFileOutcome outcome = Failed(report, storedFileId, desiredName,
                "Google denied access to the adopted file (403). With the least-privilege drive.file scope the app can only read files it created or that were explicitly granted via Choose from Drive (Google Picker) with the same Google account. Use Verify access to check the grant, re-pick the file with Choose from Drive, or clear the id to create a new file instead. Nothing was changed.", newFingerprint);
            return (null, outcome);
        }
    }

    private async Task<string?> LoadRemoteFingerprintAsync(DriveFile remote, CancellationToken cancellationToken)
    {
        try
        {
            string? content = await WithGetRetryAsync(
                () => WithAuthAsync(token => _drive.DownloadFileAsync(token, remote.Id, cancellationToken), cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return content is null ? null : DrivePublishingFingerprint.ComputeHex(content);
        }
        catch (DriveApiException ex) when (IsForbidden(ex))
        {
            return "denied";
        }
    }

    private static bool IsStaleRemoteEdit(string? lastFingerprint, string? remoteFingerprint, string newFingerprint)
    {
        return lastFingerprint is not null
            && remoteFingerprint is not null
            && !string.Equals(remoteFingerprint, "denied", StringComparison.Ordinal)
            && !string.Equals(remoteFingerprint, lastFingerprint, StringComparison.Ordinal)
            && !string.Equals(remoteFingerprint, newFingerprint, StringComparison.Ordinal);
    }

    private static DriveFileOutcome Unchanged(string report, DriveFile remote, string desiredName, string newFingerprint) =>
        new(report, "unchanged", remote.Id, desiredName, newFingerprint, remote.WebViewLink, "Content fingerprint matches; no write was needed.");

    private async Task<(DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated)> UpdateRemoteAsync(
        DrivePublishingStore.DestinationDocument destination,
        string report,
        DriveFile remote,
        string desiredName,
        string markdown,
        string newFingerprint,
        int? dossierSeason,
        CancellationToken cancellationToken)
    {
        string? rename = string.Equals(remote.Name, desiredName, StringComparison.Ordinal) ? null : desiredName;
        try
        {
            DriveFile updated = await WithAuthAsync(
                token => _drive.UpdateContentAsync(token, remote.Id, markdown, rename, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return (new DriveFileOutcome(report, "updated", updated.Id, desiredName, newFingerprint, updated.WebViewLink, $"Updated in place (file id {updated.Id})."),
                WithSlot(destination, report, updated.Id, desiredName, newFingerprint, updated.ModifiedTime, dossierSeason));
        }
        catch (DriveUpstreamException ex)
        {
            // Retry-safe: the update may have applied before the timeout.
            _logger.LogWarning(ex, "Drive update for {Report} timed out; re-checking remote content.", report);
            return await ConfirmUpdateAfterTimeoutAsync(destination, report, remote.Id, desiredName, newFingerprint, ex, dossierSeason, cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (IsForbidden(ex))
        {
            return (Failed(report, remote.Id, desiredName,
                "Google denied the update (403). The adopted file is not writable with the current Choose from Drive (Google Picker) grant. Use Verify access to check the grant. Nothing was changed.", newFingerprint),
                destination);
        }
        catch (DriveApiException ex) when (ex.IsTransient)
        {
            return (Failed(report, remote.Id, desiredName,
                $"Google Drive is temporarily unavailable ({(int)ex.StatusCode}). Re-run Publish now to retry; nothing was changed.", newFingerprint),
                destination);
        }
    }

    private async Task<(DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated)> ConfirmUpdateAfterTimeoutAsync(
        DrivePublishingStore.DestinationDocument destination,
        string report,
        string fileId,
        string desiredName,
        string newFingerprint,
        DriveUpstreamException ex,
        int? dossierSeason,
        CancellationToken cancellationToken)
    {
        string? rechecked = await WithGetRetryAsync(
            () => WithAuthAsync(token => _drive.DownloadFileAsync(token, fileId, cancellationToken), cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (rechecked is not null && string.Equals(DrivePublishingFingerprint.ComputeHex(rechecked), newFingerprint, StringComparison.Ordinal))
        {
            DriveFile? meta = await WithGetRetryAsync(
                () => WithAuthAsync(token => _drive.GetFileAsync(token, fileId, cancellationToken), cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return (new DriveFileOutcome(report, "updated", fileId, desiredName, newFingerprint, meta?.WebViewLink, "Update confirmed on re-check after a timeout."),
                WithSlot(destination, report, fileId, desiredName, newFingerprint, meta?.ModifiedTime, dossierSeason));
        }

        return (Failed(report, fileId, desiredName,
            $"Upload failed and the remote content does not match the new report: {TrimMessage(ex.Message)} Re-run Publish now to retry.", newFingerprint),
            destination);
    }

    private async Task<(DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated)> CreateNewAsync(
        DrivePublishingStore.DestinationDocument destination,
        string report,
        string desiredName,
        string parentFolderId,
        string markdown,
        string newFingerprint,
        int? dossierSeason,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DriveFile>? matches;
        try
        {
            matches = await WithGetRetryAsync(
                () => WithAuthAsync(token => _drive.ListByNameInFolderAsync(token, parentFolderId, desiredName, cancellationToken), cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (IsForbidden(ex))
        {
            return (Failed(report, null, desiredName,
                "Google denied access to the destination folder (403). With the least-privilege drive.file scope, the app can only use folders it created or that were explicitly granted via Choose from Drive (Google Picker). Use Verify access to check the grant, then retry. Nothing was created.", newFingerprint),
                destination);
        }

        if (matches.Count == 1)
        {
            return await HandleExistingNameAsync(destination, report, matches[0], desiredName, newFingerprint, dossierSeason, cancellationToken).ConfigureAwait(false);
        }

        if (matches.Count > 1)
        {
            return (Failed(report, null, desiredName,
                $"Found {matches.Count} files named '{desiredName}' in the destination. Adopt exactly one file id explicitly; nothing was created.", newFingerprint),
                destination);
        }

        return await DoCreateAsync(destination, report, desiredName, parentFolderId, markdown, newFingerprint, dossierSeason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated)> HandleExistingNameAsync(
        DrivePublishingStore.DestinationDocument destination,
        string report,
        DriveFile only,
        string desiredName,
        string newFingerprint,
        int? dossierSeason,
        CancellationToken cancellationToken)
    {
        string? content = await TryDownloadAsync(only.Id, cancellationToken).ConfigureAwait(false);
        if (content is not null && string.Equals(DrivePublishingFingerprint.ComputeHex(content), newFingerprint, StringComparison.Ordinal))
        {
            // A timed-out earlier create already landed: adopt it, no duplicate.
            return (new DriveFileOutcome(report, "updated", only.Id, desiredName, newFingerprint, only.WebViewLink, "A previous timed-out create had already landed; adopted the existing file instead of duplicating."),
                WithSlot(destination, report, only.Id, desiredName, newFingerprint, only.ModifiedTime, dossierSeason));
        }

        return (Failed(report, null, desiredName,
            $"A file named '{desiredName}' already exists in Drive (id {only.Id}). Adopt it explicitly in destination settings instead of creating a duplicate. Nothing was created.", newFingerprint),
            destination);
    }

    private async Task<string?> TryDownloadAsync(string fileId, CancellationToken cancellationToken)
    {
        try
        {
            return await WithGetRetryAsync(
                () => WithAuthAsync(token => _drive.DownloadFileAsync(token, fileId, cancellationToken), cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException)
        {
            return null;
        }
    }

    private async Task<(DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated)> DoCreateAsync(
        DrivePublishingStore.DestinationDocument destination,
        string report,
        string desiredName,
        string parentFolderId,
        string markdown,
        string newFingerprint,
        int? dossierSeason,
        CancellationToken cancellationToken)
    {
        try
        {
            DriveFile created = await WithAuthAsync(
                token => _drive.CreateMarkdownAsync(token, parentFolderId, desiredName, markdown, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return (new DriveFileOutcome(report, "created", created.Id, desiredName, newFingerprint, created.WebViewLink, $"Created {desiredName} (file id {created.Id})."),
                WithSlot(destination, report, created.Id, desiredName, newFingerprint, created.ModifiedTime, dossierSeason));
        }
        catch (DriveUpstreamException ex)
        {
            _logger.LogWarning(ex, "Drive create for {Report} timed out; re-checking for duplicates.", report);
            return await ConfirmCreateAfterTimeoutAsync(destination, report, desiredName, parentFolderId, newFingerprint, ex, dossierSeason, cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.IsTransient)
        {
            return (Failed(report, null, desiredName,
                $"Google Drive is temporarily unavailable ({(int)ex.StatusCode}). Re-run Publish now to retry; nothing was created.", newFingerprint),
                destination);
        }
    }

    private async Task<(DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated)> ConfirmCreateAfterTimeoutAsync(
        DrivePublishingStore.DestinationDocument destination,
        string report,
        string desiredName,
        string parentFolderId,
        string newFingerprint,
        DriveUpstreamException ex,
        int? dossierSeason,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DriveFile> rechecked = await WithGetRetryAsync(
            () => WithAuthAsync(token => _drive.ListByNameInFolderAsync(token, parentFolderId, desiredName, cancellationToken), cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (rechecked.Count == 1)
        {
            string? content = await TryDownloadAsync(rechecked[0].Id, cancellationToken).ConfigureAwait(false);
            if (content is not null && string.Equals(DrivePublishingFingerprint.ComputeHex(content), newFingerprint, StringComparison.Ordinal))
            {
                return (new DriveFileOutcome(report, "created", rechecked[0].Id, desiredName, newFingerprint, rechecked[0].WebViewLink, "Create confirmed on re-check after a timeout; no duplicate was made."),
                    WithSlot(destination, report, rechecked[0].Id, desiredName, newFingerprint, rechecked[0].ModifiedTime, dossierSeason));
            }
        }

        return (Failed(report, null, desiredName,
            $"Upload timed out and the create could not be confirmed: {TrimMessage(ex.Message)} Re-run Publish now; it re-checks before creating so no duplicate is made.", newFingerprint),
            destination);
    }

    private static DrivePublishingStore.DestinationDocument WithSlot(
        DrivePublishingStore.DestinationDocument destination,
        string report,
        string fileId,
        string fileName,
        string fingerprint,
        string? modifiedTime,
        int? dossierSeason)
    {
        if (string.Equals(report, "dossier", StringComparison.Ordinal) && dossierSeason.HasValue)
        {
            return DrivePublishingStore.WithDossierSlot(
                destination, dossierSeason.Value, fileId, fileName, fingerprint, modifiedTime);
        }

        return string.Equals(report, "dossier", StringComparison.Ordinal)
            ? destination with
            {
                DossierFileId = fileId,
                DossierFileName = fileName,
                DossierFingerprint = fingerprint,
                DossierRemoteModifiedTime = modifiedTime,
            }
            : destination with
            {
                AlmanacFileId = fileId,
                AlmanacFileName = fileName,
                AlmanacFingerprint = fingerprint,
                AlmanacRemoteModifiedTime = modifiedTime,
            };
    }

    private static DriveFileOutcome Failed(
        string report,
        string? fileId,
        string fileName,
        string detail,
        string fingerprint) =>
        new(report, "failed", fileId, fileName, fingerprint, null, detail);

    private static string TrimMessage(string message)
    {
        string trimmed = message.Trim();
        return trimmed.Length > 180 ? trimmed[..180] : trimmed;
    }

    private static bool IsForbidden(DriveApiException ex) =>
        ex.StatusCode == System.Net.HttpStatusCode.Forbidden;

    private async Task<T> WithGetRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.IsTransient)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            return await operation().ConfigureAwait(false);
        }
    }

    private async Task<T> WithAuthAsync<T>(Func<string, Task<T>> operation, CancellationToken cancellationToken)
    {
        string token = await GetAccessTokenAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(token).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            string refreshed = await GetAccessTokenAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(refreshed).ConfigureAwait(false);
            }
            catch (DriveApiException retry) when (retry.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await _publishing.ClearConnectionAsync(cancellationToken).ConfigureAwait(false);
                throw new DriveNotConnectedException(
                    "Google authorization expired or was revoked. Reconnect with Connect Google Drive.");
            }
        }
    }

    private async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        DrivePublishingOptions options = _options.Value;
        if (!options.IsGoogleConfigured)
        {
            throw new DriveNotConfiguredException(
                "Google Drive publishing is not configured on the server.");
        }

        DrivePublishingStore.ConnectionDocument? connection =
            await _publishing.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null || string.IsNullOrWhiteSpace(connection.RefreshToken))
        {
            throw new DriveNotConnectedException(
                "No Google account is connected. Use Connect Google Drive first.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!forceRefresh
            && !string.IsNullOrWhiteSpace(connection.AccessToken)
            && connection.AccessTokenExpiresUtc.HasValue
            && connection.AccessTokenExpiresUtc.Value > now.AddSeconds(60))
        {
            return connection.AccessToken;
        }

        OAuthTokenResponse refreshed;
        try
        {
            refreshed = await _oauth.RefreshAsync(
                options.GoogleClientId,
                options.GoogleClientSecret,
                connection.RefreshToken,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveNotConnectedException)
        {
            await _publishing.ClearConnectionAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        await _publishing.SaveConnectionAsync(
            connection with
            {
                RefreshToken = string.IsNullOrWhiteSpace(refreshed.RefreshToken)
                    ? connection.RefreshToken
                    : refreshed.RefreshToken,
                AccessToken = refreshed.AccessToken,
                AccessTokenExpiresUtc = now.AddSeconds(Math.Max(60, refreshed.ExpiresInSeconds - 60)),
                Scopes = string.IsNullOrWhiteSpace(refreshed.Scope) ? connection.Scopes : refreshed.Scope,
            },
            cancellationToken).ConfigureAwait(false);
        return refreshed.AccessToken;
    }
}
