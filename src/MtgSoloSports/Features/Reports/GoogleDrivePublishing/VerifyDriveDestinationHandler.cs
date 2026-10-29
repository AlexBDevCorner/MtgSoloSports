using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Reports;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed class VerifyDriveDestinationHandler
{
    public sealed record DriveVerifyCheck(
        string Report,
        string Status,
        string? FileId,
        string? FileName,
        string? Detail);

    public sealed record DriveVerifyResponse(
        Guid SaveId,
        string SaveName,
        int CurrentSeason,
        IReadOnlyList<DriveVerifyCheck> Checks);

    private const string FolderMime = "application/vnd.google-apps.folder";

    private const string GrantGuidance =
        "With drive.file the app can only use files/folders it created or that were explicitly granted. " +
        "In Reports use Choose from Drive (Google Picker) with the same Google account to select the folder/file, " +
        "then Save destination and Verify again. Pasting an id alone does not grant access. " +
        "(Drive Open-with requires a public HTTPS app URL and is not available for localhost.) " +
        "Alternatively clear the id to let the app create a new file.";

    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly IGoogleOAuthClient _oauth;
    private readonly IGoogleDriveClient _drive;
    private readonly IOptions<DrivePublishingOptions> _options;
    private readonly TimeProvider _timeProvider;

    public VerifyDriveDestinationHandler(
        SaveStore saves,
        DrivePublishingStore publishing,
        IGoogleOAuthClient oauth,
        IGoogleDriveClient drive,
        IOptions<DrivePublishingOptions> options,
        TimeProvider timeProvider)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        _drive = drive ?? throw new ArgumentNullException(nameof(drive));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<DriveVerifyResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

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
                "No Drive destination is configured for this save. Set the mtshistory folder id first.");
        }

        List<DriveVerifyCheck> checks = [];
        string? seasonsFolderId = await CheckFoldersAsync(destination, checks, cancellationToken).ConfigureAwait(false);
        await CheckDossierAsync(destination, detail, seasonsFolderId, checks, cancellationToken).ConfigureAwait(false);
        await CheckAlmanacAsync(destination, checks, cancellationToken).ConfigureAwait(false);

        return new DriveVerifyResponse(detail.SaveId, detail.Name, detail.CurrentSeason, checks);
    }

    private async Task<string?> CheckFoldersAsync(
        DrivePublishingStore.DestinationDocument destination,
        List<DriveVerifyCheck> checks,
        CancellationToken cancellationToken)
    {
        DriveVerifyCheck folderCheck = await CheckFolderAsync(
            "mtshistory",
            destination.MtshistoryFolderId!,
            expectedParent: null,
            cancellationToken).ConfigureAwait(false);
        checks.Add(folderCheck);
        if (!string.Equals(folderCheck.Status, "ok", StringComparison.Ordinal))
        {
            return destination.SeasonsFolderId;
        }

        string? seasonsId = destination.SeasonsFolderId;
        if (!string.IsNullOrWhiteSpace(seasonsId))
        {
            DriveVerifyCheck seasonsCheck = await CheckFolderAsync(
                "seasons", seasonsId!, destination.MtshistoryFolderId, cancellationToken).ConfigureAwait(false);
            checks.Add(seasonsCheck);
            if (string.Equals(seasonsCheck.Status, "ok", StringComparison.Ordinal))
            {
                return seasonsId;
            }
        }
        else
        {
            try
            {
                DriveFile? found = await WithAuthAsync(
                    token => _drive.FindFolderAsync(token, destination.MtshistoryFolderId!, "Seasons", cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (found is not null)
                {
                    checks.Add(new DriveVerifyCheck("seasons", "ok", found.Id, found.Name, "Seasons/ folder is accessible; publishing will reuse it."));
                    return found.Id;
                }

                checks.Add(new DriveVerifyCheck("seasons", "not-found", null, "Seasons", "Seasons/ was not found among visible folders. With drive.file an existing but ungranted Seasons/ folder is hidden (empty listing does not prove the name is free); publishing fails closed without creating a duplicate unless creation of missing folders/files is explicitly confirmed. Adopt the existing Seasons/ folder with Choose from Drive or confirm creation on Publish."));
                return null;
            }
            catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                checks.Add(new DriveVerifyCheck("seasons", "needs-grant", null, "Seasons", $"Cannot list the mtshistory folder (403). {GrantGuidance}"));
                return null;
            }
        }

        return seasonsId;
    }

    private async Task<DriveVerifyCheck> CheckFolderAsync(
        string report,
        string folderId,
        string? expectedParent,
        CancellationToken cancellationToken)
    {
        DriveFile? remote;
        try
        {
            remote = await WithAuthAsync(
                token => _drive.GetFileAsync(token, folderId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new DriveVerifyCheck(report, "needs-grant", folderId, null, $"Google denied access to this folder id (403). {GrantGuidance}");
        }

        if (remote is null || remote.Trashed)
        {
            return new DriveVerifyCheck(report, "not-found", folderId, null, "That folder id was not found in Drive (or is trashed). Paste the mtshistory folder id from the Drive URL.");
        }

        if (!string.Equals(remote.MimeType, FolderMime, StringComparison.Ordinal))
        {
            return new DriveVerifyCheck(report, "is-folder", remote.Id, remote.Name, $"That id is a file ('{remote.Name}'), not a folder. Paste a folder id for {report}.");
        }

        if (string.Equals(report, "seasons", StringComparison.Ordinal)
            && !string.Equals(remote.Name, "Seasons", StringComparison.Ordinal))
        {
            return new DriveVerifyCheck(report, "outside-destination", remote.Id, remote.Name, $"That folder is '{remote.Name}', not the 'Seasons' folder inside mtshistory. Adopt the Seasons/ folder inside mtshistory or clear it to auto-resolve.");
        }

        if (expectedParent is not null && !remote.Parents.Contains(expectedParent, StringComparer.Ordinal))
        {
            return new DriveVerifyCheck(report, "outside-destination", remote.Id, remote.Name, "That Seasons/ folder is outside the configured mtshistory folder. Adopt the Seasons/ folder inside mtshistory or clear it to auto-resolve.");
        }

        try
        {
            // Folders are not downloadable media: verify content access with a
            // folder list operation. A 403 here means the drive.file grant is
            // missing; other API quirks (e.g. fileNotDownloadable from a
            // download probe) must not be mistaken for a missing grant.
            _ = await WithAuthAsync(
                token => _drive.ListByNameInFolderAsync(token, remote.Id, "__drive_verify__", cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new DriveVerifyCheck(report, "needs-grant", remote.Id, remote.Name, $"Folder metadata is visible but listing its content was denied (403). {GrantGuidance}");
        }
        catch (DriveApiException)
        {
            // Non-grant API errors are not grant failures.
        }

        return new DriveVerifyCheck(report, "ok", remote.Id, remote.Name, "Folder id is accessible.");
    }

    private async Task CheckDossierAsync(
        DrivePublishingStore.DestinationDocument destination,
        SaveStore.SaveDetailRecord detail,
        string? seasonsFolderId,
        List<DriveVerifyCheck> checks,
        CancellationToken cancellationToken)
    {
        string stable = ReportFilenames.SeasonDossierFileName(detail.CurrentSeason, "md");
        (string? fileId, string fileName) = DrivePublishingStore.ResolveDossierForSeason(destination, detail.CurrentSeason, stable);
        if (fileId is null)
        {
            checks.Add(await CheckNameAvailabilityAsync("dossier", seasonsFolderId, fileName, cancellationToken).ConfigureAwait(false));
            return;
        }

        if (seasonsFolderId is null)
        {
            checks.Add(new DriveVerifyCheck("dossier", "not-found", fileId, fileName, "Cannot verify the dossier parent until the Seasons/ folder is resolved. Fix the folder checks first."));
            return;
        }

        checks.Add(await CheckFileAsync("dossier", fileId, fileName, seasonsFolderId, cancellationToken).ConfigureAwait(false));
    }

    private async Task CheckAlmanacAsync(
        DrivePublishingStore.DestinationDocument destination,
        List<DriveVerifyCheck> checks,
        CancellationToken cancellationToken)
    {
        if (destination.AlmanacFileId is null)
        {
            string desired = string.IsNullOrWhiteSpace(destination.AlmanacFileName)
                ? "universe-almanac.md"
                : destination.AlmanacFileName;
            checks.Add(await CheckNameAvailabilityAsync("almanac", destination.MtshistoryFolderId!, desired, cancellationToken).ConfigureAwait(false));
            return;
        }

        checks.Add(await CheckFileAsync(
            "almanac",
            destination.AlmanacFileId,
            destination.AlmanacFileName ?? "universe-almanac.md",
            destination.MtshistoryFolderId!,
            cancellationToken).ConfigureAwait(false));
    }

    private async Task<DriveVerifyCheck> CheckFileAsync(
        string report,
        string fileId,
        string desiredName,
        string parentFolderId,
        CancellationToken cancellationToken)
    {
        DriveFile? remote;
        try
        {
            remote = await WithAuthAsync(
                token => _drive.GetFileAsync(token, fileId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new DriveVerifyCheck(report, "needs-grant", fileId, desiredName, $"Google denied access to the adopted {report} file (403). {GrantGuidance}");
        }

        if (remote is null || remote.Trashed)
        {
            return new DriveVerifyCheck(report, "not-found", fileId, desiredName, $"That {report} id was not found (deleted, trashed, or never granted — drive.file hides ungranted files as missing). Publishing will fail closed without creating a duplicate. Clear the id to create '{desiredName}' in the destination, or re-pick a granted file with Choose from Drive.");
        }

        if (string.Equals(remote.MimeType, FolderMime, StringComparison.Ordinal))
        {
            return new DriveVerifyCheck(report, "is-folder", remote.Id, remote.Name, $"That {report} id is a folder, not a Markdown file. Adopt a Markdown file id or clear it to create a new file.");
        }

        if (!remote.Parents.Contains(parentFolderId, StringComparer.Ordinal))
        {
            string expected = string.Equals(report, "dossier", StringComparison.Ordinal) ? "Seasons/" : "mtshistory";
            return new DriveVerifyCheck(report, "outside-destination", remote.Id, remote.Name, $"That {report} file is outside {expected}. Adopt a file inside the destination or clear the id to create a new one.");
        }

        try
        {
            _ = await WithAuthAsync(
                token => _drive.DownloadFileAsync(token, remote.Id, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new DriveVerifyCheck(report, "needs-grant", remote.Id, remote.Name, $"Metadata is visible but reading content was denied (403). {GrantGuidance}");
        }

        return new DriveVerifyCheck(report, "ok", remote.Id, remote.Name, $"Adopted {report} file is accessible and inside the destination; publishing will update it in place.");
    }

    private async Task<DriveVerifyCheck> CheckNameAvailabilityAsync(
        string report,
        string? parentFolderId,
        string desiredName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parentFolderId))
        {
            return new DriveVerifyCheck(report, "not-found", null, desiredName, "No destination folder is available to check names; fix the folder checks first.");
        }

        IReadOnlyList<DriveFile> matches;
        try
        {
            matches = await WithAuthAsync(
                token => _drive.ListByNameInFolderAsync(token, parentFolderId!, desiredName, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new DriveVerifyCheck(report, "needs-grant", null, desiredName, $"Cannot list the destination folder (403). {GrantGuidance}");
        }

        return matches.Count switch
        {
            0 => new DriveVerifyCheck(report, "ok", null, desiredName, $"No visible file named '{desiredName}' was found. With drive.file an ungranted pre-existing file with the same name is hidden (empty listing does not prove the name is free); publishing fails closed without creating a duplicate unless creation of missing folders/files is explicitly confirmed, or adopt the existing file with Choose from Drive."),
            1 => new DriveVerifyCheck(report, "needs-adoption", matches[0].Id, desiredName, $"A file named '{desiredName}' already exists (id {matches[0].Id}). Adopt it explicitly to update in place instead of creating a duplicate."),
            _ => new DriveVerifyCheck(report, "duplicate", null, desiredName, $"Found {matches.Count} files named '{desiredName}'. Adopt exactly one file id explicitly."),
        };
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
            throw new DriveNotConfiguredException("Google Drive publishing is not configured on the server.");
        }

        DrivePublishingStore.ConnectionDocument? connection =
            await _publishing.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null || string.IsNullOrWhiteSpace(connection.RefreshToken))
        {
            throw new DriveNotConnectedException("No Google account is connected. Use Connect Google Drive first.");
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
