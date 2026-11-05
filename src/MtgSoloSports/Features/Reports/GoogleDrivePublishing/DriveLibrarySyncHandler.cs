using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Reports.CupEditionReport;
using MtgSoloSports.Features.Reports.CupTeamReport;
using MtgSoloSports.Features.Reports.SeasonDossier;
using MtgSoloSports.Features.Reports.UniverseAlmanac;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-091: manual "Sync all changed reports" over the historical library
/// (every season dossier, each Color/Type Cup edition, each Color/Type team
/// history, plus the almanac) plus the selective refresh used by automatic
/// mode. Inventory comes from persisted sporting rows; every file reuses the
/// MSS-089 single-file update implementation (update-in-place by stored file
/// id, content-fingerprint idempotence with the volatile Generated timestamp
/// ignored, duplicate-name protection, fail-closed adoption). Sequential and
/// cancellable; files are generated one at a time so large universes never
/// buffer the whole library. Progress persists incrementally, so a retry
/// skips already-settled files without re-uploading them.
/// </summary>
public sealed class DriveLibrarySyncHandler
{
    private const string FolderMime = "application/vnd.google-apps.folder";

    private const int SaveEveryFiles = 25;

    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly IGoogleDriveClient _drive;
    private readonly IOptions<DrivePublishingOptions> _options;
    private readonly GetSeasonDossierHandler _dossier;
    private readonly GetUniverseAlmanacHandler _almanac;
    private readonly GetCupEditionReportHandler _cupEdition;
    private readonly GetCupTeamReportHandler _cupTeam;
    private readonly PublishDriveReportsHandler _singleFile;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DriveLibrarySyncHandler> _logger;

    public DriveLibrarySyncHandler(
        SaveStore saves,
        DrivePublishingStore publishing,
        IGoogleDriveClient drive,
        IOptions<DrivePublishingOptions> options,
        GetSeasonDossierHandler dossier,
        GetUniverseAlmanacHandler almanac,
        GetCupEditionReportHandler cupEdition,
        GetCupTeamReportHandler cupTeam,
        PublishDriveReportsHandler singleFile,
        TimeProvider timeProvider,
        ILogger<DriveLibrarySyncHandler> logger)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _drive = drive ?? throw new ArgumentNullException(nameof(drive));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dossier = dossier ?? throw new ArgumentNullException(nameof(dossier));
        _almanac = almanac ?? throw new ArgumentNullException(nameof(almanac));
        _cupEdition = cupEdition ?? throw new ArgumentNullException(nameof(cupEdition));
        _cupTeam = cupTeam ?? throw new ArgumentNullException(nameof(cupTeam));
        _singleFile = singleFile ?? throw new ArgumentNullException(nameof(singleFile));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private sealed record LibraryContext(
        SaveStore.SaveDetailRecord Detail,
        DrivePublishingStore.DestinationDocument Destination);

    private sealed record FolderResolution(
        string? FolderId,
        bool WouldCreate,
        string? FailureReason);

    private sealed record LibraryFolders(
        string MtshistoryId,
        FolderResolution Seasons,
        FolderResolution ColorCups,
        FolderResolution ColorTeams,
        FolderResolution TypeCups,
        FolderResolution TypeTeams,
        DrivePublishingStore.DestinationDocument Destination);

    private sealed record GenerationResult(bool Ok, string Value);

    public async Task<DriveLibrarySyncResponse> HandleAsync(
        Guid saveId,
        DriveLibrarySyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = DriveSaveGates.ForSave(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExecuteLockedAsync(saveId, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// MSS-091: recent Cup report keys for automatic selective refresh.
    /// Covers source seasons within <paramref name="seasonsBack"/> of the
    /// current season (postseason lag: a cup finalizes while the next season
    /// has started). Pure read-only DB queries, no markdown generation.
    /// </summary>
    internal async Task<IReadOnlyList<string>> RecentCupKeysAsync(
        Guid saveId,
        int seasonsBack,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        await _saves.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        SaveStore.SaveDetailRecord detail = await _saves.OpenAsync(saveId, cancellationToken).ConfigureAwait(false);
        int cutoff = Math.Max(1, detail.CurrentSeason - Math.Max(0, seasonsBack));
        using SaveDbContext context = _saves.OpenDbContext(saveId);
        return await LoadRecentKeysAsync(context, cutoff, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> LoadRecentKeysAsync(
        SaveDbContext context,
        int cutoff,
        CancellationToken cancellationToken)
    {
        List<int> colorSeasons = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonNumber >= cutoff)
            .Select(e => e.SourceSeasonNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<int> typeSeasons = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonNumber >= cutoff)
            .Select(e => e.SourceSeasonNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<string> recentTeams = await LoadRecentTeamKeysAsync(context, cutoff, cancellationToken).ConfigureAwait(false);

        List<string> keys = [];
        keys.AddRange(colorSeasons.Select(s => DriveLibraryKind.EditionReport("color", s)));
        keys.AddRange(typeSeasons.Select(s => DriveLibraryKind.EditionReport("type", s)));
        keys.AddRange(recentTeams);
        return keys;
    }

    private static async Task<List<string>> LoadRecentTeamKeysAsync(
        SaveDbContext context,
        int cutoff,
        CancellationToken cancellationToken)
    {
        List<int> colorValues = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonNumber >= cutoff)
            .Select(e => e.SportingColor)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<string> typeKeys = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonNumber >= cutoff)
            .Select(e => e.CreatureType)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        typeKeys.Sort(StringComparer.Ordinal);

        List<string> keys = colorValues
            .Select(v => DriveLibraryKind.TeamReport(
                "color",
                MtgSoloSports.Features.Cups.CupHistory.CupTeamKeys.ColorKey(
                    (MtgSoloSports.SimulationKernel.Catalog.SportingColor)v)))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        keys.AddRange(typeKeys.Select(k => DriveLibraryKind.TeamReport("type", k)));
        return keys;
    }

    private async Task<DriveLibrarySyncResponse> ExecuteLockedAsync(
        Guid saveId,
        DriveLibrarySyncRequest request,
        CancellationToken cancellationToken)
    {
        LibraryContext context = await LoadContextAsync(saveId, request.ConfirmCrossSave, cancellationToken).ConfigureAwait(false);
        List<DriveLibraryItem> filtered = await LoadFilteredItemsAsync(saveId, context, request, cancellationToken).ConfigureAwait(false);
        int maxFiles = ResolveMaxFiles(request);
        bool truncated = filtered.Count > maxFiles;
        List<DriveLibraryItem> batch = truncated ? filtered.GetRange(0, maxFiles) : filtered;
        LibraryFolders folders = await ResolveFoldersAsync(context.Destination, request, cancellationToken).ConfigureAwait(false);

        List<DriveFileOutcome> outcomes = [];
        DrivePublishingStore.DestinationDocument current = folders.Destination;
        int processed = 0;
        foreach (DriveLibraryItem item in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated) settled =
                await SettleItemAsync(current, folders, item, context.Detail, saveId, request, cancellationToken).ConfigureAwait(false);
            outcomes.Add(settled.Outcome);
            current = settled.Updated;
            processed++;
            if (!request.DryRun && processed % SaveEveryFiles == 0)
            {
                await _publishing.SaveDestinationAsync(current, cancellationToken).ConfigureAwait(false);
            }
        }

        if (request.DryRun)
        {
            return BuildResponse(saveId, request, batch, truncated, outcomes, _timeProvider.GetUtcNow());
        }

        return await FinishAsync(saveId, request, batch, truncated, outcomes, current, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LibraryContext> LoadContextAsync(
        Guid saveId,
        bool confirmCrossSave,
        CancellationToken cancellationToken)
    {
        if (!_options.Value.IsGoogleConfigured)
        {
            throw new DriveNotConfiguredException(
                "Google Drive publishing is not configured. Set the DrivePublishing Google client id/secret on the server (see docs/drive-publishing.md), then retry.");
        }

        await _saves.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        SaveStore.SaveDetailRecord detail = await _saves.OpenAsync(saveId, cancellationToken).ConfigureAwait(false);
        DrivePublishingStore.DestinationDocument? destination =
            await _publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (destination?.MtshistoryFolderId is null)
        {
            throw new DriveConflictException(
                "No Drive destination is configured for this save. Set the mtshistory folder id (and adopt the library folders/files) in destination settings first.");
        }

        await EnsureNoCrossSaveReuseAsync(destination, confirmCrossSave, cancellationToken).ConfigureAwait(false);
        return new LibraryContext(detail, destination);
    }

    private async Task<List<DriveLibraryItem>> LoadFilteredItemsAsync(
        Guid saveId,
        LibraryContext context,
        DriveLibrarySyncRequest request,
        CancellationToken cancellationToken)
    {
        HashSet<string>? categories = NormalizeCategories(request.Categories);
        HashSet<string>? onlyKeys = request.OnlyKeys is null
            ? null
            : new HashSet<string>(request.OnlyKeys.Where(k => !string.IsNullOrWhiteSpace(k)), StringComparer.Ordinal);

        using SaveDbContext db = _saves.OpenDbContext(saveId);
        DriveLibraryInventory inventory = await DriveLibraryInventory.LoadAsync(db, context.Detail.Name, cancellationToken).ConfigureAwait(false);
        return inventory.Items
            .Where(item => categories is null || categories.Contains(item.Category))
            .Where(item => onlyKeys is null || onlyKeys.Contains(item.Report))
            .ToList();
    }

    private static HashSet<string>? NormalizeCategories(IReadOnlyList<string>? categories)
    {
        if (categories is null || categories.Count == 0)
        {
            return null;
        }

        HashSet<string> normalized = new(StringComparer.Ordinal);
        foreach (string category in categories)
        {
            string trimmed = category?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!IsKnownCategory(trimmed))
            {
                throw new ArgumentException(
                    $"Unsupported library category '{category}'. Use season-dossier, color-edition, type-edition, color-team, type-team or almanac.",
                    nameof(categories));
            }

            normalized.Add(trimmed);
        }

        return normalized;
    }

    private static bool IsKnownCategory(string category) =>
        string.Equals(category, DriveLibraryKind.SeasonDossier, StringComparison.Ordinal)
        || string.Equals(category, DriveLibraryKind.ColorEdition, StringComparison.Ordinal)
        || string.Equals(category, DriveLibraryKind.TypeEdition, StringComparison.Ordinal)
        || string.Equals(category, DriveLibraryKind.ColorTeam, StringComparison.Ordinal)
        || string.Equals(category, DriveLibraryKind.TypeTeam, StringComparison.Ordinal)
        || string.Equals(category, DriveLibraryKind.Almanac, StringComparison.Ordinal);

    private int ResolveMaxFiles(DriveLibrarySyncRequest request)
    {
        if (request.MaxFiles.HasValue)
        {
            if (request.MaxFiles.Value < 1 || request.MaxFiles.Value > 5000)
            {
                throw new ArgumentException("MaxFiles must be between 1 and 5000.", nameof(request));
            }

            return request.MaxFiles.Value;
        }

        int configured = _options.Value.LibraryMaxFilesPerSync;
        return configured < 1 || configured > 5000 ? 500 : configured;
    }

    private async Task<LibraryFolders> ResolveFoldersAsync(
        DrivePublishingStore.DestinationDocument destination,
        DriveLibrarySyncRequest request,
        CancellationToken cancellationToken)
    {
        string mtshistoryId = await LoadMtshistoryAsync(destination, cancellationToken).ConfigureAwait(false);
        FolderResolution seasons = await EnsureSeasonsFolderAsync(
            destination, mtshistoryId, request.AllowCreateMissing, request.DryRun, cancellationToken).ConfigureAwait(false);
        FolderResolution colorCups = await EnsureCupLeafAsync(
            "Color Cup", "Cups",
            static (doc, id) => doc with { ColorCupFolderId = id },
            static (doc, id) => doc with { ColorCupsFolderId = id },
            destination, destination.ColorCupFolderId, destination.ColorCupsFolderId,
            mtshistoryId, request.AllowCreateMissing, request.DryRun, cancellationToken).ConfigureAwait(false);
        FolderResolution colorTeams = await EnsureCupLeafAsync(
            "Color Cup", "Teams Histories",
            static (doc, id) => doc with { ColorCupFolderId = id },
            static (doc, id) => doc with { ColorTeamsFolderId = id },
            destination, destination.ColorCupFolderId, destination.ColorTeamsFolderId,
            mtshistoryId, request.AllowCreateMissing, request.DryRun, cancellationToken).ConfigureAwait(false);
        FolderResolution typeCups = await EnsureCupLeafAsync(
            "Type Cup", "Cups",
            static (doc, id) => doc with { TypeCupFolderId = id },
            static (doc, id) => doc with { TypeCupsFolderId = id },
            destination, destination.TypeCupFolderId, destination.TypeCupsFolderId,
            mtshistoryId, request.AllowCreateMissing, request.DryRun, cancellationToken).ConfigureAwait(false);
        FolderResolution typeTeams = await EnsureCupLeafAsync(
            "Type Cup", "Teams Histories",
            static (doc, id) => doc with { TypeCupFolderId = id },
            static (doc, id) => doc with { TypeTeamsFolderId = id },
            destination, destination.TypeCupFolderId, destination.TypeTeamsFolderId,
            mtshistoryId, request.AllowCreateMissing, request.DryRun, cancellationToken).ConfigureAwait(false);

        DrivePublishingStore.DestinationDocument current =
            await _publishing.GetDestinationAsync(destination.SaveId, cancellationToken).ConfigureAwait(false)
            ?? destination;
        return new LibraryFolders(mtshistoryId, seasons, colorCups, colorTeams, typeCups, typeTeams, current);
    }

    private async Task<string> LoadMtshistoryAsync(
        DrivePublishingStore.DestinationDocument destination,
        CancellationToken cancellationToken)
    {
        DriveFile? folder = await LoadFileOrDeniedAsync(destination.MtshistoryFolderId!, cancellationToken).ConfigureAwait(false);
        if (folder is null)
        {
            throw new DriveConflictException(
                "The configured mtshistory folder id was not found in Drive (deleted, trashed, or never granted — drive.file hides ungranted folders as missing). Re-pick it with Choose from Drive, then retry. Nothing was created.");
        }

        if (!string.Equals(folder.MimeType, FolderMime, StringComparison.Ordinal))
        {
            throw new DriveConflictException(
                $"The configured mtshistory id {folder.Id} is a file, not a folder. Adopt the mtshistory folder, then retry. Nothing was created.");
        }

        await ProbeFolderAccessAsync(folder.Id, "mtshistory", cancellationToken).ConfigureAwait(false);
        return folder.Id;
    }

    private async Task<DriveFile?> LoadFileOrDeniedAsync(string fileId, CancellationToken cancellationToken)
    {
        try
        {
            return await _singleFile.WithAuthAsync(
                token => _drive.GetFileAsync(token, fileId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new DriveConflictException(
                "Google denied access to a configured folder id (403). With the least-privilege drive.file scope the app can only use folders it created or that were explicitly granted via Choose from Drive (Google Picker) with the same Google account. Re-pick the folder, then retry. Nothing was created.");
        }
    }

    private async Task ProbeFolderAccessAsync(string folderId, string label, CancellationToken cancellationToken)
    {
        try
        {
            await _singleFile.WithGetRetryAsync(
                () => _singleFile.WithAuthAsync(
                    token => _drive.ListByNameInFolderAsync(token, folderId, "__drive_verify__", cancellationToken),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new DriveConflictException(
                $"The {label} folder is visible but listing its content was denied (403). Grant access with Choose from Drive, then retry. Nothing was created.");
        }
    }

    private async Task<FolderResolution> EnsureSeasonsFolderAsync(
        DrivePublishingStore.DestinationDocument destination,
        string mtshistoryId,
        bool allowCreateMissing,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(destination.SeasonsFolderId))
        {
            return await VerifyFolderAsync(
                destination.SeasonsFolderId!, "Seasons", mtshistoryId, "Seasons", cancellationToken).ConfigureAwait(false);
        }

        string? inferred = await TryInferSeasonsFromAdoptedDossierAsync(destination, mtshistoryId, cancellationToken).ConfigureAwait(false);
        if (inferred is not null)
        {
            await PersistFolderAdoptionAsync(destination, static (doc, id) => doc with { SeasonsFolderId = id }, inferred, dryRun, cancellationToken).ConfigureAwait(false);
            return new FolderResolution(inferred, false, null);
        }

        return await FindOrCreateFolderAsync(
            mtshistoryId, "Seasons", static (doc, id) => doc with { SeasonsFolderId = id },
            destination, allowCreateMissing, dryRun, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FolderResolution> VerifyFolderAsync(
        string folderId,
        string label,
        string? expectedParentId,
        string? expectedName,
        CancellationToken cancellationToken)
    {
        DriveFile? folder = await LoadFileOrDeniedAsync(folderId, cancellationToken).ConfigureAwait(false);
        if (folder is null || folder.Trashed)
        {
            return Failure($"The configured {label} folder id was not found (deleted or trashed). Re-pick it with Choose from Drive or clear it to auto-resolve. Nothing was created.");
        }

        if (!string.Equals(folder.MimeType, FolderMime, StringComparison.Ordinal))
        {
            return Failure($"The configured {label} id {folder.Id} is a file, not a folder. Adopt the {label} folder inside the destination or clear it to auto-resolve. Nothing was created.");
        }

        if (expectedName is not null && !string.Equals(folder.Name, expectedName, StringComparison.Ordinal))
        {
            return Failure($"The configured {label} folder id {folder.Id} is folder '{folder.Name}', not '{expectedName}'. Adopt the '{expectedName}' folder or clear it to auto-resolve. Nothing was created.");
        }

        if (expectedParentId is not null && !folder.Parents.Contains(expectedParentId, StringComparer.Ordinal))
        {
            return Failure($"That {label} folder is outside the configured destination. Adopt a folder inside the destination or clear it to auto-resolve. Nothing was created.");
        }

        try
        {
            await ProbeFolderAccessAsync(folder.Id, label, cancellationToken).ConfigureAwait(false);
        }
        catch (DriveConflictException ex)
        {
            return Failure(ex.Message);
        }

        return new FolderResolution(folder.Id, false, null);
    }

    private static FolderResolution Failure(string reason) => new(null, false, reason);

    private async Task<string?> TryInferSeasonsFromAdoptedDossierAsync(
        DrivePublishingStore.DestinationDocument destination,
        string mtshistoryId,
        CancellationToken cancellationToken)
    {
        List<string> adoptedIds = [];
        if (destination.DossierBySeason is not null)
        {
            adoptedIds.AddRange(destination.DossierBySeason.Values
                .Select(s => s.FileId)
                .Where(id => id is not null)
                .Cast<string>());
        }

        if (destination.DossierFileId is not null)
        {
            adoptedIds.Add(destination.DossierFileId);
        }

        foreach (string dossierId in adoptedIds.Distinct(StringComparer.Ordinal))
        {
            string? inferred = await TryInferSeasonsFromFileAsync(dossierId, "Seasons", mtshistoryId, cancellationToken).ConfigureAwait(false);
            if (inferred is not null)
            {
                return inferred;
            }
        }

        return null;
    }

    private async Task<string?> TryInferSeasonsFromFileAsync(
        string fileId,
        string expectedFolderName,
        string mtshistoryId,
        CancellationToken cancellationToken)
    {
        DriveFile? file;
        try
        {
            file = await _singleFile.WithAuthAsync(
                token => _drive.GetFileAsync(token, fileId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return null;
        }

        if (file is null || file.Trashed)
        {
            return null;
        }

        string? parentId = file.Parents.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(parentId))
        {
            return null;
        }

        DriveFile? parent;
        try
        {
            parent = await _singleFile.WithAuthAsync(
                token => _drive.GetFileAsync(token, parentId!, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return null;
        }

        if (parent is null
            || parent.Trashed
            || !string.Equals(parent.MimeType, FolderMime, StringComparison.Ordinal)
            || !string.Equals(parent.Name, expectedFolderName, StringComparison.Ordinal)
            || !parent.Parents.Contains(mtshistoryId, StringComparer.Ordinal))
        {
            return null;
        }

        return parent.Id;
    }

    private async Task<FolderResolution> FindOrCreateFolderAsync(
        string parentId,
        string name,
        Func<DrivePublishingStore.DestinationDocument, string, DrivePublishingStore.DestinationDocument> adopt,
        DrivePublishingStore.DestinationDocument destination,
        bool allowCreateMissing,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        DriveFile? found = await TryFindFolderAsync(parentId, name, cancellationToken).ConfigureAwait(false);
        if (found is not null)
        {
            await PersistFolderAdoptionAsync(destination, adopt, found.Id, dryRun, cancellationToken).ConfigureAwait(false);
            return new FolderResolution(found.Id, false, null);
        }

        if (!allowCreateMissing)
        {
            return Failure(
                $"'{name}' was not found among visible folders. With drive.file an existing but ungranted folder is hidden (empty listing does not prove the name is free). To avoid a duplicate folder tree, adopt the existing folder explicitly with Choose from Drive (then Save + Verify), or re-run sync with creation of missing folders/files confirmed. Nothing was created.");
        }

        if (dryRun)
        {
            return new FolderResolution(null, true, null);
        }

        DriveFile created = await _singleFile.WithAuthAsync(
            token => _drive.CreateFolderAsync(token, parentId, name, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await PersistFolderAdoptionAsync(destination, adopt, created.Id, dryRun, cancellationToken).ConfigureAwait(false);
        return new FolderResolution(created.Id, false, null);
    }

    private async Task<DriveFile?> TryFindFolderAsync(string parentId, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await _singleFile.WithAuthAsync(
                token => _drive.FindFolderAsync(token, parentId, name, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return null;
        }
        catch (DriveConflictException)
        {
            throw new DriveConflictException(
                $"Found multiple folders named '{name}' in the destination. Adopt exactly one folder id explicitly instead of creating a duplicate.");
        }
    }

    private async Task PersistFolderAdoptionAsync(
        DrivePublishingStore.DestinationDocument destination,
        Func<DrivePublishingStore.DestinationDocument, string, DrivePublishingStore.DestinationDocument> adopt,
        string folderId,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            return;
        }

        DrivePublishingStore.DestinationDocument? latest =
            await _publishing.GetDestinationAsync(destination.SaveId, cancellationToken).ConfigureAwait(false)
            ?? destination;
        await _publishing.SaveDestinationAsync(adopt(latest, folderId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<FolderResolution> EnsureCupLeafAsync(
        string containerName,
        string leafName,
        Func<DrivePublishingStore.DestinationDocument, string, DrivePublishingStore.DestinationDocument> adoptContainer,
        Func<DrivePublishingStore.DestinationDocument, string, DrivePublishingStore.DestinationDocument> adoptLeaf,
        DrivePublishingStore.DestinationDocument destination,
        string? explicitContainerId,
        string? explicitLeafId,
        string mtshistoryId,
        bool allowCreateMissing,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitLeafId))
        {
            return await VerifyExplicitCupLeafAsync(
                adoptContainer, destination, explicitContainerId, explicitLeafId!,
                mtshistoryId, dryRun, cancellationToken).ConfigureAwait(false);
        }

        FolderResolution container = await EnsureCupContainerAsync(
            containerName, adoptContainer, destination, explicitContainerId, mtshistoryId,
            allowCreateMissing, dryRun, cancellationToken).ConfigureAwait(false);
        if (container.FolderId is null)
        {
            return container;
        }

        DriveFile? found = await TryFindFolderAsync(container.FolderId, leafName, cancellationToken).ConfigureAwait(false);
        if (found is not null)
        {
            await PersistFolderAdoptionAsync(destination, adoptLeaf, found.Id, dryRun, cancellationToken).ConfigureAwait(false);
            return new FolderResolution(found.Id, false, null);
        }

        if (!allowCreateMissing)
        {
            return Failure(
                $"'{leafName}' was not found among visible folders. With drive.file an existing but ungranted folder is hidden (empty listing does not prove the name is free). To avoid a duplicate folder tree, adopt the existing folder explicitly with Choose from Drive (then Save + Verify), or re-run sync with creation of missing folders/files confirmed. Nothing was created.");
        }

        if (dryRun)
        {
            return new FolderResolution(null, true, null);
        }

        DriveFile created = await _singleFile.WithAuthAsync(
            token => _drive.CreateFolderAsync(token, container.FolderId, leafName, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await PersistFolderAdoptionAsync(destination, adoptLeaf, created.Id, dryRun, cancellationToken).ConfigureAwait(false);
        return new FolderResolution(created.Id, false, null);
    }

    private async Task<FolderResolution> VerifyExplicitCupLeafAsync(
        Func<DrivePublishingStore.DestinationDocument, string, DrivePublishingStore.DestinationDocument> adoptContainer,
        DrivePublishingStore.DestinationDocument destination,
        string? explicitContainerId,
        string explicitLeafId,
        string mtshistoryId,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        // Explicit cup folders keep custom names: only the MIME type and the
        // destination location are verified, never the folder name.
        FolderResolution leaf = await VerifyFolderAsync(explicitLeafId, "Cup folder", null, null, cancellationToken).ConfigureAwait(false);
        if (leaf.FolderId is null)
        {
            return leaf;
        }

        if (!string.IsNullOrWhiteSpace(explicitContainerId))
        {
            DriveFile? resolved = await LoadFileOrDeniedAsync(leaf.FolderId, cancellationToken).ConfigureAwait(false);
            if (resolved is null || !resolved.Parents.Contains(explicitContainerId!, StringComparer.Ordinal))
            {
                return Failure(
                    "That Cup folder is outside its configured container. Adopt folders inside the same mtshistory hierarchy or clear the ids to auto-resolve. Nothing was created.");
            }

            return leaf;
        }

        string? inferred = await TryInferCupContainerAsync(leaf.FolderId, mtshistoryId, cancellationToken).ConfigureAwait(false);
        if (inferred is null)
        {
            return Failure(
                "That Cup folder's parent is not inside the configured mtshistory folder. Adopt folders inside the same mtshistory hierarchy or clear the ids to auto-resolve. Nothing was created.");
        }

        await PersistFolderAdoptionAsync(destination, adoptContainer, inferred, dryRun, cancellationToken).ConfigureAwait(false);
        return leaf;
    }

    private async Task<string?> TryInferCupContainerAsync(
        string leafFolderId,
        string mtshistoryId,
        CancellationToken cancellationToken)
    {
        DriveFile? leaf;
        try
        {
            leaf = await _singleFile.WithAuthAsync(
                token => _drive.GetFileAsync(token, leafFolderId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return null;
        }

        string? parentId = leaf?.Parents.FirstOrDefault();
        if (leaf is null || leaf.Trashed || string.IsNullOrWhiteSpace(parentId))
        {
            return null;
        }

        DriveFile? parent;
        try
        {
            parent = await _singleFile.WithAuthAsync(
                token => _drive.GetFileAsync(token, parentId!, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return null;
        }

        if (parent is null
            || parent.Trashed
            || !string.Equals(parent.MimeType, FolderMime, StringComparison.Ordinal)
            || !parent.Parents.Contains(mtshistoryId, StringComparer.Ordinal))
        {
            return null;
        }

        return parent.Id;
    }

    private async Task<(DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated)> SettleItemAsync(
        DrivePublishingStore.DestinationDocument current,
        LibraryFolders folders,
        DriveLibraryItem item,
        SaveStore.SaveDetailRecord detail,
        Guid saveId,
        DriveLibrarySyncRequest request,
        CancellationToken cancellationToken)
    {
        FolderResolution resolution = ResolutionFor(folders, item.Category);
        if (resolution.FolderId is null)
        {
            return (LibraryFailed(item, resolution, request.DryRun), current);
        }

        if (request.DryRun)
        {
            DriveFileOutcome predicted = await PredictItemAsync(
                current, item, resolution.FolderId, saveId, request, cancellationToken).ConfigureAwait(false);
            return (predicted, current);
        }

        GenerationResult generated = await TryGenerateAsync(item, saveId, cancellationToken).ConfigureAwait(false);
        if (!generated.Ok)
        {
            return (FailedOutcome(item, generated.Value), current);
        }

        try
        {
            return await PublishGeneratedAsync(
                current, item, resolution.FolderId, generated.Value, request, cancellationToken).ConfigureAwait(false);
        }
        catch (DriveNotConnectedException)
        {
            throw;
        }
        catch (DriveNotConfiguredException)
        {
            throw;
        }
    }

    private static FolderResolution ResolutionFor(LibraryFolders folders, string category) =>
        category switch
        {
            var c when string.Equals(c, DriveLibraryKind.SeasonDossier, StringComparison.Ordinal) => folders.Seasons,
            var c when string.Equals(c, DriveLibraryKind.ColorEdition, StringComparison.Ordinal) => folders.ColorCups,
            var c when string.Equals(c, DriveLibraryKind.TypeEdition, StringComparison.Ordinal) => folders.TypeCups,
            var c when string.Equals(c, DriveLibraryKind.ColorTeam, StringComparison.Ordinal) => folders.ColorTeams,
            var c when string.Equals(c, DriveLibraryKind.TypeTeam, StringComparison.Ordinal) => folders.TypeTeams,
            _ => new FolderResolution(folders.MtshistoryId, false, null),
        };

    private static DriveFileOutcome LibraryFailed(DriveLibraryItem item, FolderResolution resolution, bool dryRun)
    {
        if (resolution.WouldCreate)
        {
            return new DriveFileOutcome(
                item.Report, "failed", null, item.DefaultFileName, null, null,
                (dryRun ? "Dry run: " : string.Empty)
                + $"The destination folder for '{item.DefaultFileName}' does not exist yet. Re-run sync with creation of missing folders/files confirmed to create it. Nothing was created.");
        }

        return new DriveFileOutcome(
            item.Report, "failed", null, item.DefaultFileName, null, null,
            resolution.FailureReason ?? "The destination folder is unavailable. Fix the folder mapping, then retry. Nothing was created.");
    }

    private static DriveFileOutcome FailedOutcome(DriveLibraryItem item, string detail) =>
        new(item.Report, "failed", null, item.DefaultFileName, null, null, detail);

    private async Task<GenerationResult> TryGenerateAsync(
        DriveLibraryItem item,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        try
        {
            string markdown = item.Category switch
            {
                var c when string.Equals(c, DriveLibraryKind.Almanac, StringComparison.Ordinal) =>
                    await GenerateAlmanacAsync(saveId, cancellationToken).ConfigureAwait(false),
                var c when string.Equals(c, DriveLibraryKind.SeasonDossier, StringComparison.Ordinal) =>
                    await GenerateDossierAsync(saveId, item.SeasonNumber!.Value, cancellationToken).ConfigureAwait(false),
                var c when string.Equals(c, DriveLibraryKind.ColorEdition, StringComparison.Ordinal)
                    || string.Equals(c, DriveLibraryKind.TypeEdition, StringComparison.Ordinal) =>
                    await GenerateEditionAsync(saveId, item, cancellationToken).ConfigureAwait(false),
                _ => await GenerateTeamAsync(saveId, item, cancellationToken).ConfigureAwait(false),
            };
            return new GenerationResult(true, markdown);
        }
        catch (SeasonDossierNotFoundException ex)
        {
            return new GenerationResult(false, $"That report is no longer available in this save: {Trim(ex.Message)} Nothing was changed.");
        }
        catch (UniverseAlmanacNotFoundException ex)
        {
            return new GenerationResult(false, $"That report is no longer available in this save: {Trim(ex.Message)} Nothing was changed.");
        }
        catch (CupEditionReportNotFoundException ex)
        {
            return new GenerationResult(false, $"That report is no longer available in this save: {Trim(ex.Message)} Nothing was changed.");
        }
        catch (CupTeamReportNotFoundException ex)
        {
            return new GenerationResult(false, $"That report is no longer available in this save: {Trim(ex.Message)} Nothing was changed.");
        }
        catch (InvalidOperationException ex)
        {
            return new GenerationResult(false, $"That report could not be generated from persisted rows: {Trim(ex.Message)} Nothing was changed.");
        }
    }

    private async Task<string> GenerateAlmanacAsync(Guid saveId, CancellationToken cancellationToken)
    {
        GetUniverseAlmanacHandler.ReportFile file = await _almanac.HandleAsync(saveId, "md", cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(file.Content);
    }

    private async Task<string> GenerateDossierAsync(Guid saveId, int season, CancellationToken cancellationToken)
    {
        GetSeasonDossierHandler.ReportFile file = await _dossier.HandleAsync(saveId, season, "md", cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(file.Content);
    }

    private async Task<string> GenerateEditionAsync(Guid saveId, DriveLibraryItem item, CancellationToken cancellationToken)
    {
        GetCupEditionReportHandler.ReportFile file = await _cupEdition.HandleAsync(
            saveId, item.Cup!, item.SeasonNumber!.Value, "md", cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(file.Content);
    }

    private async Task<string> GenerateTeamAsync(Guid saveId, DriveLibraryItem item, CancellationToken cancellationToken)
    {
        GetCupTeamReportHandler.ReportFile file = await _cupTeam.HandleAsync(
            saveId, item.Cup!, item.TeamKey!, "md", cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(file.Content);
    }

    private static string Trim(string message)
    {
        string trimmed = message.Trim();
        return trimmed.Length > 180 ? trimmed[..180] : trimmed;
    }

    private async Task<FolderResolution> EnsureCupContainerAsync(
        string containerName,
        Func<DrivePublishingStore.DestinationDocument, string, DrivePublishingStore.DestinationDocument> adopt,
        DrivePublishingStore.DestinationDocument destination,
        string? explicitContainerId,
        string mtshistoryId,
        bool allowCreateMissing,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitContainerId))
        {
            return await VerifyFolderAsync(explicitContainerId!, containerName, mtshistoryId, null, cancellationToken).ConfigureAwait(false);
        }

        DriveFile? found = await TryFindFolderAsync(mtshistoryId, containerName, cancellationToken).ConfigureAwait(false);
        if (found is not null)
        {
            await PersistFolderAdoptionAsync(destination, adopt, found.Id, dryRun, cancellationToken).ConfigureAwait(false);
            return new FolderResolution(found.Id, false, null);
        }

        if (!allowCreateMissing)
        {
            return Failure(
                $"'{containerName}' was not found among visible folders. With drive.file an existing but ungranted folder is hidden (empty listing does not prove the name is free). To avoid a duplicate folder tree, adopt the existing folder explicitly with Choose from Drive (then Save + Verify), or re-run sync with creation of missing folders/files confirmed. Nothing was created.");
        }

        if (dryRun)
        {
            return new FolderResolution(null, true, null);
        }

        DriveFile created = await _singleFile.WithAuthAsync(
            token => _drive.CreateFolderAsync(token, mtshistoryId, containerName, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await PersistFolderAdoptionAsync(destination, adopt, created.Id, dryRun, cancellationToken).ConfigureAwait(false);
        return new FolderResolution(created.Id, false, null);
    }

    private async Task<(DriveFileOutcome Outcome, DrivePublishingStore.DestinationDocument Updated)> PublishGeneratedAsync(
        DrivePublishingStore.DestinationDocument current,
        DriveLibraryItem item,
        string parentFolderId,
        string markdown,
        DriveLibrarySyncRequest request,
        CancellationToken cancellationToken)
    {
        string fingerprint = DrivePublishingFingerprint.ComputeHex(markdown);
        SlotResolution slot = ResolveSlot(current, item);
        PublishDriveReportsHandler.ApplySlotFn applySlot = BuildApplySlot(item);
        return await _singleFile.PublishOneFileAsync(
            current,
            item.Report,
            slot.StoredFileId,
            slot.ExplicitName,
            item.DefaultFileName,
            parentFolderId,
            markdown,
            fingerprint,
            slot.LastFingerprint,
            request.AllowOverwriteEditedRemote,
            request.AllowCreateMissing,
            item.ExpectedLocation,
            applySlot,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record SlotResolution(string? StoredFileId, string? ExplicitName, string? LastFingerprint);

    private static SlotResolution ResolveSlot(
        DrivePublishingStore.DestinationDocument current,
        DriveLibraryItem item)
    {
        if (string.Equals(item.Category, DriveLibraryKind.SeasonDossier, StringComparison.Ordinal))
        {
            // Per-season dossier identity: never reuse another season's file
            // id. Missing slots take the stable create path (or adopt a
            // same-named file already in Seasons/).
            (string? fileId, _, string? fingerprint, _) =
                DrivePublishingStore.CurrentDossierView(current, item.SeasonNumber!.Value);
            string? explicitName = fileId is null
                ? null
                : PublishDriveReportsHandler.ExplicitDossierName(current, item.SeasonNumber!.Value);
            return new SlotResolution(fileId, explicitName, fingerprint);
        }

        if (string.Equals(item.Category, DriveLibraryKind.Almanac, StringComparison.Ordinal))
        {
            return new SlotResolution(current.AlmanacFileId, current.AlmanacFileName, current.AlmanacFingerprint);
        }

        bool isEdition = string.Equals(item.Category, DriveLibraryKind.ColorEdition, StringComparison.Ordinal)
            || string.Equals(item.Category, DriveLibraryKind.TypeEdition, StringComparison.Ordinal);
        IReadOnlyDictionary<string, DrivePublishingStore.LibrarySlot>? map =
            isEdition ? current.CupEditionByKey : current.CupTeamByKey;
        string key = isEdition
            ? DriveLibraryKind.EditionKey(item.Cup!, item.SeasonNumber!.Value)
            : DriveLibraryKind.TeamKey(item.Cup!, item.TeamKey!);
        (string? libraryId, string? libraryName, string? libraryFingerprint, _) =
            DrivePublishingStore.LibraryView(map, key);
        return new SlotResolution(libraryId, libraryName, libraryFingerprint);
    }

    private static PublishDriveReportsHandler.ApplySlotFn BuildApplySlot(DriveLibraryItem item)
    {
        if (string.Equals(item.Category, DriveLibraryKind.SeasonDossier, StringComparison.Ordinal))
        {
            int season = item.SeasonNumber!.Value;
            return (current, fileId, fileName, fingerprint, modifiedTime) =>
                DrivePublishingStore.WithDossierSlot(current, season, fileId, fileName, fingerprint, modifiedTime);
        }

        if (string.Equals(item.Category, DriveLibraryKind.Almanac, StringComparison.Ordinal))
        {
            return (current, fileId, fileName, fingerprint, modifiedTime) => current with
            {
                AlmanacFileId = fileId,
                AlmanacFileName = fileName,
                AlmanacFingerprint = fingerprint,
                AlmanacRemoteModifiedTime = modifiedTime,
            };
        }

        bool isEdition = string.Equals(item.Category, DriveLibraryKind.ColorEdition, StringComparison.Ordinal)
            || string.Equals(item.Category, DriveLibraryKind.TypeEdition, StringComparison.Ordinal);
        string key = isEdition
            ? DriveLibraryKind.EditionKey(item.Cup!, item.SeasonNumber!.Value)
            : DriveLibraryKind.TeamKey(item.Cup!, item.TeamKey!);
        return (current, fileId, fileName, fingerprint, modifiedTime) =>
            DrivePublishingStore.WithLibrarySlot(current, isEdition, key, fileId, fileName, fingerprint, modifiedTime);
    }

    private async Task<DriveFileOutcome> PredictItemAsync(
        DrivePublishingStore.DestinationDocument current,
        DriveLibraryItem item,
        string parentFolderId,
        Guid saveId,
        DriveLibrarySyncRequest request,
        CancellationToken cancellationToken)
    {
        GenerationResult generated = await TryGenerateAsync(item, saveId, cancellationToken).ConfigureAwait(false);
        if (!generated.Ok)
        {
            return FailedOutcome(item, generated.Value);
        }

        string fingerprint = DrivePublishingFingerprint.ComputeHex(generated.Value);
        SlotResolution slot = ResolveSlot(current, item);
        if (slot.StoredFileId is null)
        {
            return await PredictCreateAsync(
                item, parentFolderId, generated.Value, fingerprint, request, cancellationToken).ConfigureAwait(false);
        }

        return await PredictUpdateAsync(
            item, parentFolderId, generated.Value, fingerprint, slot, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DriveFileOutcome> PredictCreateAsync(
        DriveLibraryItem item,
        string parentFolderId,
        string markdown,
        string fingerprint,
        DriveLibrarySyncRequest request,
        CancellationToken cancellationToken)
    {
        _ = markdown;
        IReadOnlyList<DriveFile> matches = await ListByNameAsync(parentFolderId, item.DefaultFileName, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return request.AllowCreateMissing
                ? Predicted(item, null, "created", fingerprint, $"Dry run: would create '{item.DefaultFileName}' in {item.ExpectedLocation}.")
                : FailedOutcome(item,
                    $"Dry run: no visible file named '{item.DefaultFileName}' was found in {item.ExpectedLocation}. With drive.file an ungranted pre-existing file is hidden; re-run sync with creation of missing folders/files confirmed, or adopt the existing file explicitly. Nothing would be created.");
        }

        if (matches.Count > 1)
        {
            return FailedOutcome(item,
                $"Dry run: found {matches.Count} files named '{item.DefaultFileName}' in {item.ExpectedLocation}. Adopt exactly one file id explicitly (resolve names like '{item.DefaultFileName} (1)' in Drive first). Nothing would be created.");
        }

        string? remote = await TryDownloadAsync(matches[0].Id, cancellationToken).ConfigureAwait(false);
        if (remote is not null && string.Equals(DrivePublishingFingerprint.ComputeHex(remote), fingerprint, StringComparison.Ordinal))
        {
            return Predicted(item, matches[0].Id, "unchanged", fingerprint, $"Dry run: existing '{item.DefaultFileName}' already matches; would adopt it with no write.");
        }

        return FailedOutcome(item,
            $"Dry run: a file named '{item.DefaultFileName}' already exists in Drive (id {matches[0].Id}). Adopt it explicitly in destination settings instead of creating a duplicate. Nothing would be created.");
    }

    private async Task<DriveFileOutcome> PredictUpdateAsync(
        DriveLibraryItem item,
        string parentFolderId,
        string markdown,
        string fingerprint,
        SlotResolution slot,
        DriveLibrarySyncRequest request,
        CancellationToken cancellationToken)
    {
        _ = parentFolderId;
        _ = markdown;
        DriveFile? remote = await TryGetFileAsync(slot.StoredFileId!, cancellationToken).ConfigureAwait(false);
        if (remote is null || remote.Trashed)
        {
            return FailedOutcome(item,
                $"Dry run: the adopted {item.Report} file id {slot.StoredFileId} is no longer visible in Drive (deleted, trashed, or never granted). Nothing would be changed.");
        }

        if (string.Equals(remote.MimeType, FolderMime, StringComparison.Ordinal))
        {
            return FailedOutcome(item,
                $"Dry run: the adopted {item.Report} id {remote.Id} is a Drive folder, not a Markdown file. Nothing would be changed.");
        }

        string effectiveName = string.IsNullOrWhiteSpace(slot.ExplicitName) ? remote.Name : slot.ExplicitName;
        string? remoteFingerprint = await TryRemoteFingerprintAsync(remote.Id, cancellationToken).ConfigureAwait(false);
        if (remoteFingerprint is null)
        {
            return FailedOutcome(item,
                $"Dry run: Google denied access to the adopted {item.Report} file (403). Grant access with Choose from Drive, then retry. Nothing would be changed.");
        }

        if (string.Equals(fingerprint, remoteFingerprint, StringComparison.Ordinal))
        {
            return Predicted(item, remote.Id, "unchanged", fingerprint, "Dry run: content fingerprint matches; no write would be needed.");
        }

        if (IsStaleRemote(slot.LastFingerprint, remoteFingerprint, fingerprint) && !request.AllowOverwriteEditedRemote)
        {
            return FailedOutcome(item,
                "Dry run: the Drive file was edited independently since the last publish. Re-run with overwrite confirmation to replace those edits, or adopt a different file. Nothing would be changed.");
        }

        string verb = string.Equals(remote.Name, effectiveName, StringComparison.Ordinal) ? "update in place" : $"rename to '{effectiveName}' and update in place";
        return Predicted(item, remote.Id, "updated", fingerprint, $"Dry run: would {verb} (file id {remote.Id}).");
    }

    private static DriveFileOutcome Predicted(
        DriveLibraryItem item,
        string? fileId,
        string outcome,
        string fingerprint,
        string detail) =>
        new(item.Report, outcome, fileId, item.DefaultFileName, fingerprint, null, detail);

    private async Task<IReadOnlyList<DriveFile>> ListByNameAsync(
        string folderId,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _singleFile.WithGetRetryAsync(
                () => _singleFile.WithAuthAsync(
                    token => _drive.ListByNameInFolderAsync(token, folderId, name, cancellationToken),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new DriveConflictException(
                "Google denied access to the destination folder (403). Grant access with Choose from Drive (Google Picker), then retry. Nothing was created.");
        }
    }

    private async Task<DriveFile?> TryGetFileAsync(string fileId, CancellationToken cancellationToken)
    {
        try
        {
            return await _singleFile.WithGetRetryAsync(
                () => _singleFile.WithAuthAsync(
                    token => _drive.GetFileAsync(token, fileId, cancellationToken),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return null;
        }
    }

    private async Task<string?> TryDownloadAsync(string fileId, CancellationToken cancellationToken)
    {
        try
        {
            return await _singleFile.WithGetRetryAsync(
                () => _singleFile.WithAuthAsync(
                    token => _drive.DownloadFileAsync(token, fileId, cancellationToken),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DriveApiException)
        {
            return null;
        }
    }

    private async Task<string?> TryRemoteFingerprintAsync(string fileId, CancellationToken cancellationToken)
    {
        string? content = await TryDownloadAsync(fileId, cancellationToken).ConfigureAwait(false);
        return content is null ? null : DrivePublishingFingerprint.ComputeHex(content);
    }

    private static bool IsStaleRemote(string? lastFingerprint, string? remoteFingerprint, string newFingerprint)
    {
        return lastFingerprint is not null
            && remoteFingerprint is not null
            && !string.Equals(remoteFingerprint, lastFingerprint, StringComparison.Ordinal)
            && !string.Equals(remoteFingerprint, newFingerprint, StringComparison.Ordinal);
    }

    private async Task<DriveLibrarySyncResponse> FinishAsync(
        Guid saveId,
        DriveLibrarySyncRequest request,
        List<DriveLibraryItem> batch,
        bool truncated,
        List<DriveFileOutcome> outcomes,
        DrivePublishingStore.DestinationDocument current,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool allSettled = outcomes.All(o => !string.Equals(o.Outcome, "failed", StringComparison.Ordinal));
        string? firstError = outcomes
            .Where(o => string.Equals(o.Outcome, "failed", StringComparison.Ordinal))
            .Select(o => $"{o.Report}: {o.Detail}")
            .FirstOrDefault();

        Dictionary<string, DrivePublishingStore.LibraryCategoryCounts> byCategory = CountByCategory(batch, outcomes);
        int created = outcomes.Count(o => string.Equals(o.Outcome, "created", StringComparison.Ordinal));
        int updated = outcomes.Count(o => string.Equals(o.Outcome, "updated", StringComparison.Ordinal));
        int unchanged = outcomes.Count(o => string.Equals(o.Outcome, "unchanged", StringComparison.Ordinal));
        int failed = outcomes.Count(o => string.Equals(o.Outcome, "failed", StringComparison.Ordinal));

        DrivePublishingStore.DestinationDocument finished = current with
        {
            LastLibraryPublishedUtc = allSettled ? now : current.LastLibraryPublishedUtc,
            LastLibraryError = allSettled ? null : firstError,
            LastLibraryCreated = created,
            LastLibraryUpdated = updated,
            LastLibraryUnchanged = unchanged,
            LastLibraryFailed = failed,
            LastLibraryByCategory = byCategory,
        };
        // Post-commit durability: persist the settled maps plus the library
        // summary even when the HTTP caller disconnected mid-sync, so a
        // retry skips already-settled files instead of re-uploading them.
        await _publishing.SaveDestinationAsync(finished, CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "Drive library sync for save {SaveId} settled {Created} created, {Updated} updated, {Unchanged} unchanged, {Failed} failed.",
            saveId,
            created,
            updated,
            unchanged,
            failed);
        return BuildResponse(saveId, request, batch, truncated, outcomes, now);
    }

    private static Dictionary<string, DrivePublishingStore.LibraryCategoryCounts> CountByCategory(
        List<DriveLibraryItem> batch,
        List<DriveFileOutcome> outcomes)
    {
        Dictionary<string, DrivePublishingStore.LibraryCategoryCounts> byCategory = new(StringComparer.Ordinal);
        for (int i = 0; i < batch.Count && i < outcomes.Count; i++)
        {
            string category = batch[i].Category;
            DriveFileOutcome outcome = outcomes[i];
            byCategory.TryGetValue(category, out DrivePublishingStore.LibraryCategoryCounts? counts);
            counts ??= new DrivePublishingStore.LibraryCategoryCounts(0, 0, 0, 0);
            byCategory[category] = outcome.Outcome switch
            {
                "created" => counts with { Created = counts.Created + 1 },
                "updated" => counts with { Updated = counts.Updated + 1 },
                "unchanged" => counts with { Unchanged = counts.Unchanged + 1 },
                _ => counts with { Failed = counts.Failed + 1 },
            };
        }

        return byCategory;
    }

    private static DriveLibrarySyncResponse BuildResponse(
        Guid saveId,
        DriveLibrarySyncRequest request,
        List<DriveLibraryItem> batch,
        bool truncated,
        List<DriveFileOutcome> outcomes,
        DateTimeOffset syncedUtc)
    {
        Dictionary<string, DrivePublishingStore.LibraryCategoryCounts> byCategory = CountByCategory(batch, outcomes);
        List<DriveLibraryCategoryResult> categories = [];
        foreach (string category in CategoryOrder())
        {
            if (!byCategory.TryGetValue(category, out DrivePublishingStore.LibraryCategoryCounts? counts))
            {
                continue;
            }

            categories.Add(new DriveLibraryCategoryResult(
                category,
                counts.Created + counts.Updated + counts.Unchanged + counts.Failed,
                counts.Created,
                counts.Updated,
                counts.Unchanged,
                counts.Failed));
        }

        return new DriveLibrarySyncResponse(
            saveId,
            request.DryRun,
            truncated,
            batch.Count,
            syncedUtc,
            categories,
            outcomes);
    }

    private static IEnumerable<string> CategoryOrder()
    {
        yield return DriveLibraryKind.Almanac;
        yield return DriveLibraryKind.SeasonDossier;
        yield return DriveLibraryKind.ColorEdition;
        yield return DriveLibraryKind.TypeEdition;
        yield return DriveLibraryKind.ColorTeam;
        yield return DriveLibraryKind.TypeTeam;
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
        foreach (string? folderId in new[]
                 {
                     destination.MtshistoryFolderId,
                     destination.SeasonsFolderId,
                     destination.ColorCupFolderId,
                     destination.ColorCupsFolderId,
                     destination.ColorTeamsFolderId,
                     destination.TypeCupFolderId,
                     destination.TypeCupsFolderId,
                     destination.TypeTeamsFolderId,
                 })
        {
            if (folderId is null)
            {
                continue;
            }

            if (string.Equals(other.MtshistoryFolderId, folderId, StringComparison.Ordinal)
                || string.Equals(other.SeasonsFolderId, folderId, StringComparison.Ordinal)
                || string.Equals(other.ColorCupFolderId, folderId, StringComparison.Ordinal)
                || string.Equals(other.ColorCupsFolderId, folderId, StringComparison.Ordinal)
                || string.Equals(other.ColorTeamsFolderId, folderId, StringComparison.Ordinal)
                || string.Equals(other.TypeCupFolderId, folderId, StringComparison.Ordinal)
                || string.Equals(other.TypeCupsFolderId, folderId, StringComparison.Ordinal)
                || string.Equals(other.TypeTeamsFolderId, folderId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        HashSet<string> otherFiles = CollectFileIds(other);
        foreach (string fileId in CollectFileIds(destination))
        {
            if (otherFiles.Contains(fileId))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> CollectFileIds(DrivePublishingStore.DestinationDocument destination)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        if (destination.DossierFileId is not null)
        {
            ids.Add(destination.DossierFileId);
        }

        if (destination.AlmanacFileId is not null)
        {
            ids.Add(destination.AlmanacFileId);
        }

        foreach (IReadOnlyDictionary<int, DrivePublishingStore.DossierSlot>? map in new[] { destination.DossierBySeason })
        {
            if (map is null)
            {
                continue;
            }

            foreach (DrivePublishingStore.DossierSlot slot in map.Values)
            {
                if (slot.FileId is not null)
                {
                    ids.Add(slot.FileId);
                }
            }
        }

        foreach (IReadOnlyDictionary<string, DrivePublishingStore.LibrarySlot>? map in new[]
                 {
                     destination.CupEditionByKey, destination.CupTeamByKey,
                 })
        {
            if (map is null)
            {
                continue;
            }

            foreach (DrivePublishingStore.LibrarySlot slot in map.Values)
            {
                if (slot.FileId is not null)
                {
                    ids.Add(slot.FileId);
                }
            }
        }

        return ids;
    }
}
