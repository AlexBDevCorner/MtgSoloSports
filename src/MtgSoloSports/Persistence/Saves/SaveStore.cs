using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Features.Leagues.InauguralDraw;
using MtgSoloSports.Features.Universe.CreateUniverse;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Filesystem + SQLite orchestration for independent save universes.
/// Each save is exactly one <c>{saveId:N}.db</c> file; there is no shared
/// sporting-history database. Discovery is filesystem enumeration plus per-file
/// metadata reads (a persisted global index is intentionally not used so saves
/// stay independent and copyable).
/// This is save-scoped file management, not a generic repository.
/// </summary>
public sealed class SaveStore
{
    public const string InitialPhase = "SeasonInProgress";
    public const int InitialSeason = 1;
    public const int MaxNameLength = 100;

    private readonly IOptions<SaveStorageOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly SaveDbContextFactory _factory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SaveStore> _logger;

    /// <summary>
    /// Per-save migration-verified fast path for hot read endpoints. After a
    /// save is proven current (no pending EF migrations), repeated
    /// <see cref="EnsureMigratedAsync"/> calls skip the
    /// <c>__EFMigrationsHistory</c> round-trip while the underlying file bytes
    /// are unchanged (length plus last-write). Any file replacement via import,
    /// restore, delete or migration itself invalidates the entry, and a new
    /// app version with additional migrations naturally misses because the
    /// verified entry stores the schema version it was verified against.
    /// Correctness is preserved: a stale entry can only occur if the file is
    /// replaced outside <see cref="SaveStore"/> with identical length and
    /// timestamp, in which case the next detailed read still validates
    /// required rows and schema bookkeeping.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, VerifiedMigration> _migrationVerified = new();

    private sealed record VerifiedMigration(int SchemaVersion, long Length, DateTime LastWriteUtc);

    public SaveStore(
        IOptions<SaveStorageOptions> options,
        IHostEnvironment environment,
        SaveDbContextFactory factory,
        TimeProvider timeProvider,
        ILogger<SaveStore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public sealed record SaveRecord(
        Guid SaveId,
        string Name,
        DateTimeOffset CreatedUtc,
        int SchemaVersion,
        int CurrentSeason,
        string Phase);

    public sealed record SaveDetailRecord(
        Guid SaveId,
        string Name,
        DateTimeOffset CreatedUtc,
        int SchemaVersion,
        int CurrentSeason,
        string Phase,
        string RngAlgorithm,
        int RngVersion,
        ulong RngState,
        ulong RngStream,
        int RulesVersion,
        string RulesJson);

    public sealed record UniverseSummaryRecord(
        int TotalAthletes,
        IReadOnlyDictionary<SportingColor, int> AthletesPerColor,
        string Checksum);

    public sealed record CreationRecord(
        SaveDetailRecord Detail,
        UniverseSummaryRecord Universe);

    /// <summary>
    /// Verified technical recovery checkpoint. Checkpoints are crash/migration
    /// recovery boundaries, never user-facing gameplay undo.
    /// </summary>
    public sealed record CheckpointRecord(
        Guid CheckpointId,
        Guid SaveId,
        DateTimeOffset CreatedUtc,
        string Reason,
        string SaveName,
        int SchemaVersion,
        int CurrentSeason,
        string Phase,
        string DatabaseSha256);

    /// <summary>
    /// Portable export artifact: the SQLite save database plus manifest/version
    /// metadata zipped together. The external artwork cache is intentionally
    /// not embedded; image references/fallbacks remain valid on import.
    /// </summary>
    public sealed record ExportRecord(
        Guid SaveId,
        string FileName,
        string ContentType,
        byte[] ZipBytes,
        SaveBundleManifest Manifest);

    public string GetSavesRoot()
    {
        string configured = _options.Value.SavesRoot;
        string root = string.IsNullOrWhiteSpace(configured) ? "saves" : configured.Trim();
        return Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(_environment.ContentRootPath, root));
    }

    public string GetSaveFilePath(Guid saveId) => SaveFileNaming.GetSaveFilePath(GetSavesRoot(), saveId);

    /// <summary>
    /// Opens a tracked <see cref="SaveDbContext"/> for future simulation mutations.
    /// The caller owns disposal and commits RNG state in the same transaction as results.
    /// </summary>
    public SaveDbContext OpenDbContext(Guid saveId)
    {
        string path = GetSaveFilePath(saveId);
        if (!File.Exists(path))
        {
            throw new SaveNotFoundException(saveId);
        }

        return _factory.Create(path);
    }

    /// <summary>
    /// Applies pending save-schema (EF) migrations to one existing save file.
    /// Database-schema migration is separate from game-rule migration: the
    /// schema runner only ensures tables/indexes exist and stamps schema
    /// bookkeeping, while the persisted rules snapshot and sporting results
    /// are compared before/after and must be byte-identical. When migrations
    /// are pending, a verified recoverable checkpoint is created first; a
    /// failed migration restores that checkpoint and aborts instead of
    /// leaving a half-migrated file behind.
    /// </summary>
    public async Task EnsureMigratedAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        string path = GetSaveFilePath(saveId);
        if (!File.Exists(path))
        {
            throw new SaveNotFoundException(saveId);
        }

        if (IsMigrationVerified(path, saveId))
        {
            return;
        }

        IReadOnlyList<string> pending = await SaveSchemaMigrator
            .GetPendingMigrationsAsync(_factory, path, cancellationToken)
            .ConfigureAwait(false);
        if (pending.Count == 0)
        {
            MarkMigrationVerified(path, saveId);
            return;
        }

        string rulesBefore = await ReadRulesJsonByPathAsync(path, cancellationToken).ConfigureAwait(false);
        CheckpointRecord checkpoint = await CreateCheckpointAsync(
            saveId, $"pre-schema-migration-to-{SaveSchemaVersion.Current}", cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveSchemaMigrator.ApplyPendingAsync(_factory, path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await OverwriteLiveFromCheckpointAsync(saveId, checkpoint.CheckpointId, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Database-schema migration failed and the save was rolled back to verified checkpoint '{checkpoint.CheckpointId:D}'.",
                ex);
        }

        string rulesAfter = await ReadRulesJsonByPathAsync(path, cancellationToken).ConfigureAwait(false);
        SaveRulesCompatibility.EnsureSnapshotUnchanged(rulesBefore, rulesAfter);
        _ = await ReadDetailAsync(saveId, cancellationToken).ConfigureAwait(false);
        InvalidateMigrationVerified(saveId);
        MarkMigrationVerified(path, saveId);
    }

    public bool IsMigrationVerified(string saveFilePath, Guid saveId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveFilePath);
        if (!_migrationVerified.TryGetValue(saveId, out VerifiedMigration? verified))
        {
            return false;
        }

        if (verified.SchemaVersion != SaveSchemaVersion.Current)
        {
            return false;
        }

        FileInfo info;
        try
        {
            info = new FileInfo(saveFilePath);
            if (!info.Exists)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return info.Length == verified.Length && info.LastWriteTimeUtc == verified.LastWriteUtc;
    }

    public void MarkMigrationVerified(string saveFilePath, Guid saveId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveFilePath);
        FileInfo info = new(saveFilePath);
        if (!info.Exists)
        {
            return;
        }

        _migrationVerified[saveId] = new VerifiedMigration(
            SaveSchemaVersion.Current, info.Length, info.LastWriteTimeUtc);
    }

    public void InvalidateMigrationVerified(Guid saveId)
    {
        _migrationVerified.TryRemove(saveId, out _);
    }

    /// <summary>
    /// Creates one independent save universe: metadata, immutable Rules v1
    /// snapshot, the deterministic 2,048-athlete universe selected from
    /// <paramref name="catalogAthletes"/> with the save RNG, Season 1 with eight
    /// 32-athlete feeder leagues (no Superleague) drawn from the save population
    /// with the same RNG, and the post-draw RNG state, all committed in a single
    /// transaction. Selection and the inaugural draw consume only the save RNG in
    /// sporting-color enum order from name-sorted pools, so an equivalent seed
    /// plus catalog reproduces the equivalent universe and leagues. Any failure
    /// deletes the save file so no partially initialized save is left behind.
    /// League membership lives in Season/Membership rows, never in
    /// <see cref="SaveAthleteEntity"/> card metadata.
    /// </summary>
    public async Task<CreationRecord> CreateAsync(
        string name,
        ulong? seed,
        ulong? stream,
        IReadOnlyList<CatalogAthlete> catalogAthletes,
        CancellationToken cancellationToken = default)
    {
        string cleanName = ValidateName(name);
        ArgumentNullException.ThrowIfNull(catalogAthletes);
        Guid saveId = Guid.NewGuid();
        (ulong resolvedSeed, ulong resolvedStream) = ResolveSeed(seed, stream);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        UniversePreparation preparation = PrepareUniverse(catalogAthletes, resolvedSeed, resolvedStream);

        string root = GetSavesRoot();
        Directory.CreateDirectory(root);
        string path = SaveFileNaming.GetSaveFilePath(root, saveId);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Save file '{path}' already exists.");
        }

        try
        {
            await PersistNewSaveAsync(saveId, cleanName, now, preparation, path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            DeleteSaveFilesBestEffort(path);
            throw;
        }

        SaveDetailRecord detail = new(
            saveId,
            cleanName,
            now,
            SaveSchemaVersion.Current,
            InitialSeason,
            InitialPhase,
            Pcg32V1.AlgorithmName,
            Pcg32V1.AlgorithmVersion,
            preparation.AdvancedRng.State,
            preparation.AdvancedRng.Stream,
            preparation.Rules.Version,
            preparation.RulesJson);
        UniverseSummaryRecord universe = new(
            preparation.Selection.Summary.TotalAthletes,
            preparation.Selection.Summary.AthletesPerColor,
            preparation.Selection.Summary.Checksum);
        return new CreationRecord(detail, universe);
    }

    private sealed record UniversePreparation(
        RulesV1 Rules,
        UniverseSelection Selection,
        InauguralDrawResult Draw,
        Pcg32State AdvancedRng,
        string RulesJson);

    private static UniversePreparation PrepareUniverse(
        IReadOnlyList<CatalogAthlete> catalogAthletes,
        ulong seed,
        ulong stream)
    {
        RulesV1 rules = RulesV2.CreateDefault();
        Pcg32V1 rng = new(seed, stream);
        UniverseSelection selection = UniverseSelector.Select(catalogAthletes, rng, rules);
        InauguralDrawResult draw = InauguralDrawSelector.Select(selection.Selected, rng, rules);
        return new UniversePreparation(rules, selection, draw, rng.Snapshot(), RulesSnapshotCodec.Encode(rules));
    }

    private async Task PersistNewSaveAsync(
        Guid saveId,
        string cleanName,
        DateTimeOffset now,
        UniversePreparation preparation,
        string path,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = _factory.Create(path);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);

        using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        InsertUniverseRows(context, saveId, cleanName, now, preparation);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        int persisted = await context.SaveAthletes.CountAsync(cancellationToken).ConfigureAwait(false);
        if (persisted != preparation.Rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Save universe must contain exactly {preparation.Rules.TotalAthletesInSave} athletes, was {persisted}.");
        }

        await InsertSeason1RowsAsync(context, preparation, cancellationToken).ConfigureAwait(false);
        await Features.Athletes.Projections.AthleteProjectionUpdater.SeedForNewSaveAsync(
            context, await context.Seasons.SingleAsync(e => e.SeasonNumber == InitialSeason, cancellationToken).ConfigureAwait(false),
            preparation.Rules, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void InsertUniverseRows(
        SaveDbContext context,
        Guid saveId,
        string cleanName,
        DateTimeOffset now,
        UniversePreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.SaveMetadata.Add(new SaveMetadataEntity
        {
            Id = 1,
            SaveId = saveId,
            Name = cleanName,
            CreatedUtc = now,
            SchemaVersion = SaveSchemaVersion.Current,
            CurrentSeason = InitialSeason,
            Phase = InitialPhase,
        });
        context.RulesSnapshots.Add(new RulesSnapshotEntity
        {
            Id = 1,
            RulesVersion = preparation.Rules.Version,
            RulesJson = preparation.RulesJson,
        });
        context.RngStates.Add(RngStateEntity.FromState(preparation.AdvancedRng));

        foreach (CatalogAthlete athlete in preparation.Selection.Selected)
        {
            context.SaveAthletes.Add(SaveAthleteEntity.FromCatalog(athlete));
        }
    }

    /// <summary>
    /// Inserts Season 1 (no Superleague), its eight feeder leagues and all 2,048
    /// Season 1 memberships from the already-validated inaugural draw. Runs inside
    /// the save-creation transaction so RNG state and memberships commit together.
    /// Membership is the sporting source of truth; <see cref="SaveAthleteEntity"/>
    /// card rows are left untouched.
    /// </summary>
    private static async Task InsertSeason1RowsAsync(
        SaveDbContext context,
        UniversePreparation preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        RulesV1 rules = preparation.Rules;

        Dictionary<string, int> athleteIds = CollectAthleteIds(context, rules);
        SeasonEntity season = await CreateSeason1Async(context, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> leagues = await CreateFeederLeaguesAsync(context, season.Id, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> memberships = BuildMemberships(preparation.Draw, athleteIds, season.Id, leagues, rules);
        foreach (SeasonMembershipEntity membership in memberships)
        {
            context.SeasonMemberships.Add(membership);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        ValidateSeason1Rows(season, leagues, memberships, rules);
    }

    private static Dictionary<string, int> CollectAthleteIds(SaveDbContext context, RulesV1 rules)
    {
        Dictionary<string, int> ids = new(StringComparer.Ordinal);
        foreach (SaveAthleteEntity entity in context.SaveAthletes.Local)
        {
            ids[entity.Name] = entity.Id;
        }

        if (ids.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Save universe must contain exactly {rules.TotalAthletesInSave} athletes before Season 1 leagues, was {ids.Count}.");
        }

        return ids;
    }

    private static async Task<SeasonEntity> CreateSeason1Async(SaveDbContext context, CancellationToken cancellationToken)
    {
        SeasonEntity season = new() { SeasonNumber = 1, HasSuperleague = false };
        context.Seasons.Add(season);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return season;
    }

    private static async Task<List<LeagueEntity>> CreateFeederLeaguesAsync(
        SaveDbContext context,
        int seasonId,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            leagues.Add(new LeagueEntity
            {
                SeasonId = seasonId,
                SportingColor = (int)color,
                Kind = (int)LeagueKind.Feeder,
                FeederDivision = (int)SimulationKernel.Leagues.FeederDivision.First,
                Name = $"{color} League",
            });
        }

        foreach (LeagueEntity league in leagues)
        {
            context.Leagues.Add(league);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return leagues;
    }

    private static List<SeasonMembershipEntity> BuildMemberships(
        InauguralDrawResult draw,
        Dictionary<string, int> athleteIds,
        int seasonId,
        IReadOnlyList<LeagueEntity> leagues,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(draw);
        Dictionary<SportingColor, int> leagueIds = leagues.ToDictionary(l => (SportingColor)l.SportingColor, l => l.Id);
        List<SeasonMembershipEntity> memberships = new(rules.TotalAthletesInSave);
        foreach (InauguralDrawEntry entry in draw.Entries)
        {
            if (!athleteIds.TryGetValue(entry.Name, out int athleteId))
            {
                throw new InvalidOperationException($"Season 1 draw references unknown athlete '{entry.Name}'.");
            }

            memberships.Add(new SeasonMembershipEntity
            {
                SeasonId = seasonId,
                LeagueId = entry.IsLeagueMember ? leagueIds[entry.SportingColor] : null,
                SaveAthleteId = athleteId,
                SportingColor = (int)entry.SportingColor,
                DrawIndex = entry.DrawIndex,
            });
        }

        return memberships;
    }

    private static void ValidateSeason1Rows(
        SeasonEntity season,
        IReadOnlyList<LeagueEntity> leagues,
        IReadOnlyList<SeasonMembershipEntity> memberships,
        RulesV1 rules)
    {
        Season1PersistedInvariants.ValidatePersistedSeason1(
            season.SeasonNumber,
            season.HasSuperleague,
            leagues.Select(l => new PersistedLeague(l.Id, (SportingColor)l.SportingColor, l.Kind, l.FeederDivision, l.Name)).ToList(),
            memberships.Select(m => new PersistedMembership(m.SaveAthleteId, (SportingColor)m.SportingColor, m.LeagueId, m.DrawIndex)).ToList(),
            rules.TotalAthletesInSave,
            rules);
    }

    public async Task<IReadOnlyList<SaveRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        string root = GetSavesRoot();
        Directory.CreateDirectory(root);

        string[] files;
        try
        {
            files = Directory.GetFiles(root, "*.db");
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        Array.Sort(files, StringComparer.Ordinal);
        List<SaveRecord> records = [];
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(file);
            if (!SaveFileNaming.IsValidSaveFileName(fileName, out Guid saveId))
            {
                continue;
            }

            try
            {
                SaveDetailRecord detail = await ReadDetailAsync(saveId, cancellationToken).ConfigureAwait(false);
                records.Add(new SaveRecord(detail.SaveId, detail.Name, detail.CreatedUtc, detail.SchemaVersion, detail.CurrentSeason, detail.Phase));
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Skipping unreadable save file {FileName}.", fileName);
            }
        }

        records.Sort(static (left, right) =>
        {
            int created = left.CreatedUtc.CompareTo(right.CreatedUtc);
            return created != 0 ? created : left.SaveId.CompareTo(right.SaveId);
        });
        return records;
    }

    public Task<SaveDetailRecord> OpenAsync(Guid saveId, CancellationToken cancellationToken = default)
        => ReadDetailAsync(saveId, cancellationToken);

    /// <summary>
    /// Explicitly deletes one save's own database file, its SQLite companion
    /// files (-wal/-shm/-journal) and its technical recovery checkpoints.
    /// The resolved path must stay inside the saves root; nothing else is ever removed.
    /// </summary>
    public Task DeleteAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        string root = GetSavesRoot();
        string path = SaveFileNaming.GetSaveFilePath(root, saveId);
        if (!File.Exists(path))
        {
            throw new SaveNotFoundException(saveId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        DeleteCompanionFiles(path);
        ClearSaveConnections(path);
        InvalidateMigrationVerified(saveId);

        string checkpointDirectory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, saveId);
        if (Directory.Exists(checkpointDirectory))
        {
            try
            {
                Directory.Delete(checkpointDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove checkpoint directory for deleted save {SaveId}.", saveId);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Exports a complete save as a portable artifact: the SQLite save
    /// database plus manifest/version metadata zipped together. The live save
    /// is WAL-checkpointed first so the embedded copy is a consistent
    /// committed snapshot. Never mutates sporting state.
    /// </summary>
    public async Task<ExportRecord> ExportAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        string path = GetSaveFilePath(saveId);
        if (!File.Exists(path))
        {
            throw new SaveNotFoundException(saveId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await CheckpointWalAsync(path, cancellationToken).ConfigureAwait(false);
        SaveDetailRecord detail = await ReadDetailAsync(saveId, cancellationToken).ConfigureAwait(false);
        string sha256 = SaveBundle.ComputeFileSha256Hex(path);
        DateTimeOffset exportedUtc = _timeProvider.GetUtcNow();

        SaveBundleManifest manifest = new(
            SaveBundleManifest.ExpectedFormat,
            SaveBundleManifest.CurrentFormatVersion,
            detail.SaveId,
            detail.Name,
            detail.CreatedUtc,
            detail.SchemaVersion,
            detail.RulesVersion,
            detail.RngAlgorithm,
            detail.RngVersion,
            detail.CurrentSeason,
            detail.Phase,
            SaveBundleManifest.DatabaseEntryName,
            sha256,
            exportedUtc,
            "MtgSoloSports");
        byte[] zipBytes = SaveBundle.Create(path, manifest);
        return new ExportRecord(
            detail.SaveId,
            $"{saveId:N}.mtgsave.zip",
            "application/zip",
            zipBytes,
            manifest);
    }

    /// <summary>
    /// Imports a portable save artifact. Validates archive shape, manifest
    /// checksum, identity, and schema/rules compatibility before placing the
    /// file, migrates older database schemas forward on the staged copy, and
    /// never silently overwrites another save: an existing save id is
    /// rejected unless <paramref name="overwrite"/> is explicitly true, in
    /// which case a verified checkpoint of the current file is created first.
    /// Any validation failure leaves existing saves untouched and removes the
    /// staged copy.
    /// </summary>
    public async Task<SaveDetailRecord> ImportAsync(
        byte[] bundleBytes,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundleBytes);
        if (bundleBytes.Length == 0)
        {
            throw new ArgumentException("Save bundle is empty.", nameof(bundleBytes));
        }

        if (bundleBytes.LongLength > SaveBundle.MaxBundleBytes)
        {
            throw new InvalidOperationException(
                $"Save bundle exceeds the {SaveBundle.MaxBundleBytes} byte import limit.");
        }

        string root = GetSavesRoot();
        Directory.CreateDirectory(root);
        string stagingDirectory = Path.Combine(root, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            SaveBundle.Extraction extraction = await SaveBundle
                .ExtractToStagingAsync(bundleBytes, stagingDirectory, cancellationToken)
                .ConfigureAwait(false);
            SaveBundleManifest manifest = extraction.Manifest;
            SaveRulesCompatibility.EnsureImportableRules(manifest);
            await VerifyStagedDatabaseAsync(extraction.StagedDatabasePath, manifest, cancellationToken).ConfigureAwait(false);
            await MigrateStagedDatabaseIfNeededAsync(extraction.StagedDatabasePath, manifest, cancellationToken).ConfigureAwait(false);

            string target = SaveFileNaming.GetSaveFilePath(root, manifest.SaveId);
            if (File.Exists(target))
            {
                if (!overwrite)
                {
                    throw new SaveAlreadyExistsException(manifest.SaveId);
                }

                await CreateCheckpointAsync(manifest.SaveId, "pre-import-overwrite", cancellationToken).ConfigureAwait(false);
                File.Copy(extraction.StagedDatabasePath, target, overwrite: true);
                DeleteCompanionFiles(target);
            }
            else
            {
                File.Move(extraction.StagedDatabasePath, target);
            }

            // File replacement bypasses pooled SQLite handles, which would
            // otherwise keep serving pages from the previous file content.
            ClearSaveConnections(target);
            InvalidateMigrationVerified(manifest.SaveId);
            return await ReadDetailAsync(manifest.SaveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteDirectoryBestEffort(stagingDirectory);
        }
    }

    /// <summary>
    /// Creates a verified technical recovery checkpoint of the last known
    /// valid save boundary: WAL-checkpoint, file copy, checksum comparison,
    /// identity verification of the copy, and sidecar persistence. Used before
    /// migrations, import overwrites, and large bulk operations, and for
    /// restoring after a software bug/failure. This is technical recovery,
    /// never normal gameplay rewind.
    /// </summary>
    public async Task<CheckpointRecord> CreateCheckpointAsync(
        Guid saveId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        string livePath = GetSaveFilePath(saveId);
        if (!File.Exists(livePath))
        {
            throw new SaveNotFoundException(saveId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        SaveDetailRecord detail = await ReadDetailAsync(saveId, cancellationToken).ConfigureAwait(false);
        await CheckpointWalAsync(livePath, cancellationToken).ConfigureAwait(false);

        string root = GetSavesRoot();
        CheckpointPaths paths = PrepareCheckpointPaths(root, saveId);
        string normalizedReason = SaveCheckpointFiles.NormalizeReason(reason);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        File.Copy(livePath, paths.TempCopy, overwrite: false);
        try
        {
            return await PersistVerifiedCheckpointAsync(
                livePath, paths, detail, normalizedReason, now, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DeleteFileBestEffort(paths.TempCopy);
            throw;
        }
    }

    private sealed record CheckpointPaths(
        string Directory,
        Guid CheckpointId,
        string DatabasePath,
        string SidecarPath,
        string TempCopy);

    private static CheckpointPaths PrepareCheckpointPaths(string savesRoot, Guid saveId)
    {
        string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(savesRoot, saveId);
        Directory.CreateDirectory(directory);
        Guid checkpointId = Guid.NewGuid();
        return new CheckpointPaths(
            directory,
            checkpointId,
            SaveCheckpointFiles.GetCheckpointDatabasePath(directory, checkpointId),
            SaveCheckpointFiles.GetCheckpointSidecarPath(directory, checkpointId),
            Path.Combine(directory, $".tmp-{checkpointId:N}.db"));
    }

    /// <summary>
    /// Verifies the staged copy, moves it into place, persists a verified
    /// sidecar, prunes old checkpoints and reports the new checkpoint record.
    /// </summary>
    private async Task<CheckpointRecord> PersistVerifiedCheckpointAsync(
        string livePath,
        CheckpointPaths paths,
        SaveDetailRecord detail,
        string normalizedReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(livePath);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedReason);

        string copySha = await CopyAndVerifyDatabaseAsync(livePath, paths.TempCopy, detail.SaveId, cancellationToken).ConfigureAwait(false);
        File.Move(paths.TempCopy, paths.DatabasePath);

        SaveCheckpointSidecar sidecar = new(
            paths.CheckpointId,
            detail.SaveId,
            now,
            normalizedReason,
            detail.Name,
            detail.SchemaVersion,
            detail.CurrentSeason,
            detail.Phase,
            copySha);
        await WriteVerifiedSidecarAsync(paths.SidecarPath, sidecar, copySha, cancellationToken).ConfigureAwait(false);

        SaveCheckpointFiles.PruneOldest(paths.Directory);
        _logger.LogInformation(
            "Created technical recovery checkpoint {CheckpointId} for save {SaveId} (reason: {Reason}).",
            paths.CheckpointId,
            detail.SaveId,
            normalizedReason);
        return new CheckpointRecord(
            paths.CheckpointId,
            detail.SaveId,
            now,
            normalizedReason,
            detail.Name,
            detail.SchemaVersion,
            detail.CurrentSeason,
            detail.Phase,
            copySha);
    }

    /// <summary>
    /// Verifies a checkpoint file copy byte-for-byte against its source and
    /// proves the copy opens with the expected save identity.
    /// Returns the verified checksum.
    /// </summary>
    private async Task<string> CopyAndVerifyDatabaseAsync(
        string sourcePath,
        string copyPath,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        string sourceSha = SaveBundle.ComputeFileSha256Hex(sourcePath);
        string copySha = SaveBundle.ComputeFileSha256Hex(copyPath);
        if (!string.Equals(sourceSha, copySha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Checkpoint copy verification failed for save '{saveId:D}'.");
        }

        await VerifyCheckpointCopyAsync(copyPath, saveId, cancellationToken).ConfigureAwait(false);
        return copySha;
    }

    /// <summary>
    /// Persists a checkpoint sidecar and verifies it round-trips with the
    /// expected checksum before the checkpoint is trusted.
    /// </summary>
    private static async Task WriteVerifiedSidecarAsync(
        string sidecarPath,
        SaveCheckpointSidecar sidecar,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        await File.WriteAllTextAsync(sidecarPath, sidecar.ToJson(), cancellationToken).ConfigureAwait(false);
        SaveCheckpointSidecar reloaded = SaveCheckpointSidecar.FromJson(
            await File.ReadAllTextAsync(sidecarPath, cancellationToken).ConfigureAwait(false));
        if (reloaded.CheckpointId != sidecar.CheckpointId
            || !string.Equals(reloaded.DatabaseSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Checkpoint sidecar verification failed for save '{sidecar.SaveId:D}'.");
        }
    }

    /// <summary>
    /// Lists verified technical recovery checkpoints for a save, newest last.
    /// Entries whose sidecar or database file is unreadable are skipped.
    /// </summary>
    public Task<IReadOnlyList<CheckpointRecord>> ListCheckpointsAsync(
        Guid saveId,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(GetSavesRoot(), saveId);
        List<CheckpointRecord> records = [];
        if (!Directory.Exists(directory))
        {
            return Task.FromResult<IReadOnlyList<CheckpointRecord>>(records);
        }

        foreach (string sidecarPath in Directory.GetFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                SaveCheckpointSidecar sidecar = SaveCheckpointSidecar.FromJson(File.ReadAllText(sidecarPath));
                if (sidecar.SaveId != saveId || sidecar.CheckpointId == Guid.Empty)
                {
                    continue;
                }

                if (!File.Exists(SaveCheckpointFiles.GetCheckpointDatabasePath(directory, sidecar.CheckpointId)))
                {
                    continue;
                }

                records.Add(new CheckpointRecord(
                    sidecar.CheckpointId,
                    sidecar.SaveId,
                    sidecar.CreatedUtc,
                    sidecar.Reason,
                    sidecar.SaveName,
                    sidecar.SchemaVersion,
                    sidecar.CurrentSeason,
                    sidecar.Phase,
                    sidecar.DatabaseSha256));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "Skipping unreadable checkpoint sidecar {SidecarPath}.", sidecarPath);
            }
        }

        records.Sort(static (left, right) =>
        {
            int created = left.CreatedUtc.CompareTo(right.CreatedUtc);
            return created != 0 ? created : left.CheckpointId.CompareTo(right.CheckpointId);
        });
        return Task.FromResult<IReadOnlyList<CheckpointRecord>>(records);
    }

    /// <summary>
    /// Restores the last known valid boundary from a verified technical
    /// recovery checkpoint after a software bug/failure. The checkpoint is
    /// checksum- and identity-verified before use, a safety checkpoint of the
    /// current file is created first, and the restored file is verified
    /// readable afterwards. Technical recovery only; normal gameplay never
    /// rewinds or resimulates sporting results.
    /// </summary>
    public async Task<SaveDetailRecord> RestoreCheckpointAsync(
        Guid saveId,
        Guid checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (checkpointId == Guid.Empty)
        {
            throw new ArgumentException("Checkpoint id must not be empty.", nameof(checkpointId));
        }

        string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(GetSavesRoot(), saveId);
        string databasePath = SaveCheckpointFiles.GetCheckpointDatabasePath(directory, checkpointId);
        string sidecarPath = SaveCheckpointFiles.GetCheckpointSidecarPath(directory, checkpointId);
        if (!File.Exists(databasePath) || !File.Exists(sidecarPath))
        {
            throw new SaveCheckpointNotFoundException(saveId, checkpointId);
        }

        SaveCheckpointSidecar sidecar;
        try
        {
            sidecar = SaveCheckpointSidecar.FromJson(
                await File.ReadAllTextAsync(sidecarPath, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Checkpoint '{checkpointId:D}' sidecar is unreadable; restore was rejected.", ex);
        }

        if (sidecar.SaveId != saveId || sidecar.CheckpointId != checkpointId)
        {
            throw new InvalidOperationException(
                $"Checkpoint '{checkpointId:D}' sidecar identity mismatch; restore was rejected.");
        }

        string actualSha = SaveBundle.ComputeFileSha256Hex(databasePath);
        if (!string.Equals(actualSha, sidecar.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Checkpoint '{checkpointId:D}' checksum mismatch; the backup is corrupt and restore was rejected.");
        }

        await VerifyCheckpointCopyAsync(databasePath, saveId, cancellationToken).ConfigureAwait(false);

        string livePath = GetSaveFilePath(saveId);
        if (!File.Exists(livePath))
        {
            throw new SaveNotFoundException(saveId);
        }

        await CreateCheckpointAsync(saveId, "pre-restore-safety", cancellationToken).ConfigureAwait(false);
        await OverwriteLiveFromCheckpointAsync(saveId, checkpointId, cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Restored save {SaveId} from technical recovery checkpoint {CheckpointId}.",
            saveId,
            checkpointId);
        return await ReadDetailAsync(saveId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SaveDetailRecord> ReadDetailAsync(Guid saveId, CancellationToken cancellationToken)
    {
        string path = GetSaveFilePath(saveId);
        if (!File.Exists(path))
        {
            throw new SaveNotFoundException(saveId);
        }

        using SaveDbContext context = _factory.Create(path);
        SaveRows rows = await LoadRowsAsync(context, cancellationToken).ConfigureAwait(false);
        return MapDetail(saveId, rows);
    }

    private sealed record SaveRows(SaveMetadataEntity? Metadata, RulesSnapshotEntity? Rules, RngStateEntity? Rng);

    private static async Task<SaveRows> LoadRowsAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        SaveMetadataEntity? metadata = await context.SaveMetadata
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        RulesSnapshotEntity? rulesRow = await context.RulesSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        RngStateEntity? rngRow = await context.RngStates
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        return new SaveRows(metadata, rulesRow, rngRow);
    }

    private static SaveDetailRecord MapDetail(Guid saveId, SaveRows rows)
    {
        SaveMetadataEntity? metadata = rows.Metadata;
        RulesSnapshotEntity? rulesRow = rows.Rules;
        RngStateEntity? rngRow = rows.Rng;
        if (metadata is null || rulesRow is null || rngRow is null)
        {
            throw new InvalidOperationException($"Save '{saveId:D}' is missing required rows.");
        }

        if (metadata.SaveId != saveId)
        {
            throw new InvalidOperationException($"Save '{saveId:D}' has mismatched identity.");
        }

        if (metadata.SchemaVersion != SaveSchemaVersion.Current)
        {
            throw new InvalidOperationException($"Save '{saveId:D}' has unsupported schema version {metadata.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(metadata.Phase))
        {
            throw new InvalidOperationException($"Save '{saveId:D}' has an empty phase.");
        }

        if (string.IsNullOrWhiteSpace(rulesRow.RulesJson))
        {
            throw new InvalidOperationException($"Save '{saveId:D}' has an empty rules snapshot.");
        }

        Pcg32State rngState = rngRow.ToState();
        _ = RulesSnapshotCodec.Decode(rulesRow.RulesJson);

        return new SaveDetailRecord(
            metadata.SaveId,
            metadata.Name,
            metadata.CreatedUtc,
            metadata.SchemaVersion,
            metadata.CurrentSeason,
            metadata.Phase,
            rngRow.Algorithm,
            rngRow.AlgorithmVersion,
            rngState.State,
            rngState.Stream,
            rulesRow.RulesVersion,
            rulesRow.RulesJson);
    }

    internal static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Save name must not be empty.", nameof(name));
        }

        string clean = name.Trim();
        if (clean.Length > MaxNameLength)
        {
            throw new ArgumentException($"Save name must be at most {MaxNameLength} characters.", nameof(name));
        }

        return clean;
    }

    private static (ulong Seed, ulong Stream) ResolveSeed(ulong? seed, ulong? stream)
    {
        if (seed.HasValue && stream.HasValue)
        {
            return (seed.Value, stream.Value);
        }

        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        ulong freshSeed = BitConverter.ToUInt64(bytes[..8]);
        ulong freshStream = BitConverter.ToUInt64(bytes[8..]);
        return (seed ?? freshSeed, stream ?? freshStream);
    }

    /// <summary>
    /// Forces pending WAL frames into the main database file so a subsequent
    /// file copy is a consistent committed snapshot.
    /// </summary>
    private async Task CheckpointWalAsync(string saveFilePath, CancellationToken cancellationToken)
    {
        using SaveDbContext context = _factory.Create(saveFilePath);
        await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a staged import database: required rows exist, identity and
    /// schema version match the manifest, and RNG/rules snapshots parse.
    /// Throws before anything live is touched.
    /// </summary>
    private async Task VerifyStagedDatabaseAsync(
        string stagedDatabasePath,
        SaveBundleManifest manifest,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = _factory.Create(stagedDatabasePath);
        SaveRows rows = await LoadRowsAsync(context, cancellationToken).ConfigureAwait(false);
        if (rows.Metadata is null || rows.Rules is null || rows.Rng is null)
        {
            throw new InvalidOperationException("Imported save is missing required metadata, rules, or RNG rows.");
        }

        if (rows.Metadata.SaveId != manifest.SaveId)
        {
            throw new InvalidOperationException(
                $"Imported save identity '{rows.Metadata.SaveId:D}' does not match manifest save '{manifest.SaveId:D}'.");
        }

        if (rows.Metadata.SchemaVersion != manifest.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Imported save schema version {rows.Metadata.SchemaVersion} does not match manifest version {manifest.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(rows.Metadata.Phase))
        {
            throw new InvalidOperationException("Imported save has an empty phase.");
        }

        if (string.IsNullOrWhiteSpace(rows.Rules.RulesJson))
        {
            throw new InvalidOperationException("Imported save has an empty rules snapshot.");
        }

        _ = rows.Rng.ToState();
        _ = SaveRulesCompatibility.ReadSnapshotRules(rows.Rules.RulesJson);
        if (rows.Rules.RulesVersion != manifest.RulesVersion)
        {
            throw new InvalidOperationException(
                $"Imported save rules version {rows.Rules.RulesVersion} does not match manifest version {manifest.RulesVersion}.");
        }
    }

    /// <summary>
    /// Migrates an older staged import database forward to the current
    /// schema. Runs on the staging copy only, proves the rules snapshot is
    /// unchanged, and verifies the result maps cleanly before placement.
    /// </summary>
    private async Task MigrateStagedDatabaseIfNeededAsync(
        string stagedDatabasePath,
        SaveBundleManifest manifest,
        CancellationToken cancellationToken)
    {
        string rulesBefore = await ReadRulesJsonByPathAsync(stagedDatabasePath, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> pending = await SaveSchemaMigrator
            .GetPendingMigrationsAsync(_factory, stagedDatabasePath, cancellationToken)
            .ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return;
        }

        await SaveSchemaMigrator.ApplyPendingAsync(_factory, stagedDatabasePath, cancellationToken).ConfigureAwait(false);
        string rulesAfter = await ReadRulesJsonByPathAsync(stagedDatabasePath, cancellationToken).ConfigureAwait(false);
        SaveRulesCompatibility.EnsureSnapshotUnchanged(rulesBefore, rulesAfter);

        using SaveDbContext context = _factory.Create(stagedDatabasePath);
        SaveRows rows = await LoadRowsAsync(context, cancellationToken).ConfigureAwait(false);
        _ = MapDetail(manifest.SaveId, rows);
    }

    private async Task<string> ReadRulesJsonByPathAsync(string saveFilePath, CancellationToken cancellationToken)
    {
        using SaveDbContext context = _factory.Create(saveFilePath);
        RulesSnapshotEntity? rules = await context.RulesSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        if (rules is null || string.IsNullOrWhiteSpace(rules.RulesJson))
        {
            throw new InvalidOperationException("Save has an empty rules snapshot.");
        }

        return rules.RulesJson;
    }

    /// <summary>
    /// Verifies a checkpoint database copy opens with required rows and the
    /// expected save identity. Cleans up SQLite companions opened by the check.
    /// </summary>
    private async Task VerifyCheckpointCopyAsync(
        string checkpointDatabasePath,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        try
        {
            using SaveDbContext context = _factory.Create(checkpointDatabasePath);
            SaveMetadataEntity? metadata = await context.SaveMetadata
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.Id == 1, cancellationToken)
                .ConfigureAwait(false);
            bool hasRules = await context.RulesSnapshots
                .AsNoTracking()
                .AnyAsync(e => e.Id == 1, cancellationToken)
                .ConfigureAwait(false);
            bool hasRng = await context.RngStates
                .AsNoTracking()
                .AnyAsync(e => e.Id == 1, cancellationToken)
                .ConfigureAwait(false);
            if (metadata is null || !hasRules || !hasRng)
            {
                throw new InvalidOperationException(
                    $"Checkpoint for save '{saveId:D}' is missing required metadata, rules, or RNG rows.");
            }

            if (metadata.SaveId != saveId)
            {
                throw new InvalidOperationException(
                    $"Checkpoint save identity '{metadata.SaveId:D}' does not match save '{saveId:D}'.");
            }
        }
        finally
        {
            DeleteCompanionFiles(checkpointDatabasePath);
        }
    }

    /// <summary>
    /// Overwrites the live save file with a verified checkpoint copy and
    /// verifies the result opens. Used by restore and migration rollback.
    /// </summary>
    private async Task OverwriteLiveFromCheckpointAsync(
        Guid saveId,
        Guid checkpointId,
        CancellationToken cancellationToken)
    {
        string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(GetSavesRoot(), saveId);
        string checkpointPath = SaveCheckpointFiles.GetCheckpointDatabasePath(directory, checkpointId);
        if (!File.Exists(checkpointPath))
        {
            throw new SaveCheckpointNotFoundException(saveId, checkpointId);
        }

        string livePath = GetSaveFilePath(saveId);
        if (!File.Exists(livePath))
        {
            throw new SaveNotFoundException(saveId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Copy(checkpointPath, livePath, overwrite: true);
        DeleteCompanionFiles(livePath);
        ClearSaveConnections(livePath);
        InvalidateMigrationVerified(saveId);
        _ = await ReadDetailAsync(saveId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops pooled SQLite handles for a save file after the file bytes were
    /// replaced or removed outside any connection. Without this, pooled
    /// handles keep serving pages from the previous file content.
    /// </summary>
    private static void ClearSaveConnections(string saveFilePath)
    {
        using SqliteConnection connection = new($"Data Source={saveFilePath}");
        SqliteConnection.ClearPool(connection);
    }

    private static void DeleteCompanionFiles(string saveFilePath)
    {
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            DeleteFileBestEffort(saveFilePath + suffix);
        }
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes a failed save's database file plus SQLite companions so a
    /// failed universe creation leaves no partially initialized save behind.
    /// Best effort: cleanup failures never mask the original error.
    /// </summary>
    private static void DeleteSaveFilesBestEffort(string path)
    {
        foreach (string candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
