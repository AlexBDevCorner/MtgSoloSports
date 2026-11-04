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
using MtgSoloSports.Features.Simulation.AdvanceRound;
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

    /// <summary>
    /// Per-save migration gates serializing <see cref="EnsureMigratedAsync"/>.
    /// Mutation handlers already hold the per-save simulation lock, but
    /// read-only status/Cup/history queries call <see cref="EnsureMigratedAsync"/>
    /// without that lock so hot reads stay concurrent. The gate keeps exactly
    /// one checkpoint/migrate/rollback sequence per save at a time: parallel
    /// first reads of a pre-tournament-schema save, or a read overlapping a
    /// locked mutation's own upgrade, serialize here instead of racing on
    /// pending-migration checks, checkpoint files, and SQLite schema writes.
    /// Static so every <see cref="SaveStore"/> instance (including a fresh
    /// store after an app restart in tests) shares the same per-save
    /// serialization, matching <c>AdvanceRoundSaveLock</c> lifetime.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> MigrationGates = new();

    private static SemaphoreSlim AcquireMigrationGate(Guid saveId) =>
        MigrationGates.GetOrAdd(saveId, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Per-save snapshot gates serializing consistent-snapshot capture
    /// (export/checkpoint via <c>VACUUM INTO</c>) with other save-file
    /// replacements (import/restore/delete/overwrite). Simulation writes
    /// coordinate via SQLite snapshot isolation: <c>VACUUM INTO</c> sees only
    /// committed data even with a pinned WAL reader, so no simulation lock is
    /// taken here. Ordering is always migration gate then snapshot gate, never
    /// the reverse, because checkpoint creation is invoked from already-locked
    /// bulk/migration paths. Per-save, never global.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> SnapshotGates = new();

    private static SemaphoreSlim AcquireSnapshotGate(Guid saveId) =>
        SnapshotGates.GetOrAdd(saveId, static _ => new SemaphoreSlim(1, 1));

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
    /// Concurrency-safe: a per-save migration gate serializes the
    /// verified-check, pending-migration check, checkpoint, apply, and
    /// rollback sequence, so parallel status reads of an old save and reads
    /// overlapping a mutating upgrade cannot interleave checkpoint/restore or
    /// SQLite schema writes. Read-only callers must not take the per-save
    /// simulation lock; this gate is the established synchronization point.
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

        SemaphoreSlim gate = AcquireMigrationGate(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsMigrationVerified(path, saveId))
            {
                return;
            }

            IReadOnlyList<string> pending = await SaveSchemaMigrator
                .GetPendingMigrationsAsync(_factory, path, cancellationToken)
                .ConfigureAwait(false);
            if (pending.Count == 0)
            {
                await EnsureChaosActivationAsync(saveId, cancellationToken).ConfigureAwait(false);
                await EnsureSelectionFormulaActivationAsync(saveId, cancellationToken).ConfigureAwait(false);
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
            await EnsureChaosActivationAsync(saveId, cancellationToken).ConfigureAwait(false);
            await EnsureSelectionFormulaActivationAsync(saveId, cancellationToken).ConfigureAwait(false);
            InvalidateMigrationVerified(saveId);
            MarkMigrationVerified(path, saveId);
        }
        finally
        {
            gate.Release();
        }
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

    internal async Task EnsureChaosActivationAsync(Guid saveId, CancellationToken cancellationToken)
    {
        // Backfills the single-row Chaos activation for pre-MSS-106 saves: first source
        // season at SaveMetadata.CurrentSeason + 1 so current/historic seasons never gain an
        // injected tournament while a pending not-yet-started successor stays eligible.
        // New saves already carry EffectiveFrom=1 from creation.
        // Idempotent; consumes no sporting RNG.
        using SaveDbContext context = OpenDbContext(saveId);
        try
        {
            _ = await Features.Cups.ChaosCup.ChaosCupLifecycle.EnsureActivationAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("ChaosCupActivations", StringComparison.OrdinalIgnoreCase))
        {
            // Table predates the Chaos activation migration; the pending-migration path
            // applies it before this backfill runs again.
            return;
        }

        InvalidateMigrationVerified(saveId);
    }

    internal async Task EnsureSelectionFormulaActivationAsync(Guid saveId, CancellationToken cancellationToken)
    {
        // Backfills the single-row selection-formula activation for pre-MSS-132
        // saves: first newly started season at max(CurrentSeason + 1, Chaos
        // activation) so the currently started source season keeps its legacy
        // formula. New saves already carry EffectiveFrom=1 from creation.
        // Idempotent; consumes no sporting RNG; never moves the boundary on retry.
        using SaveDbContext context = OpenDbContext(saveId);
        try
        {
            _ = await Features.Cups.SelectionFormula.CupSelectionFormulaLifecycle.EnsureActivationAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("CupSelectionFormulaActivations", StringComparison.OrdinalIgnoreCase))
        {
            // Table predates the selection-formula migration; the
            // pending-migration path applies it before this backfill runs again.
            return;
        }

        InvalidateMigrationVerified(saveId);
    }

    /// <summary>
    /// Creates one independent save universe: metadata, immutable Rules v2
    /// snapshot, the deterministic 2,048-athlete universe selected from
    /// <paramref name="catalogAthletes"/> with the save RNG, Season 1 with 24
    /// 32-athlete feeder leagues (F1/F2/F3 per color, no Superleague) drawn from
    /// the save population with the same RNG, and the post-draw RNG state, all
    /// committed in a single transaction. Selection and the inaugural draw
    /// consume only the save RNG in sporting-color enum order from name-sorted
    /// pools (positions 0..31 F1, 32..63 F2, 64..95 F3, 96..255 pool), so an
    /// equivalent seed plus catalog reproduces the equivalent universe and
    /// leagues. Any failure deletes the save file so no partially initialized
    /// save is left behind. League membership lives in Season/Membership rows,
    /// never in <see cref="SaveAthleteEntity"/> card metadata.
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
        RulesV1 rules = RulesV3.CreateDefault();
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
        context.ChaosCupActivations.Add(new ChaosCupActivationEntity
        {
            Id = 1,
            EffectiveFromSeasonNumber = InitialSeason,
            ChaosFormatVersion = SimulationKernel.Cups.ChaosCupFormat.ChaosFormatVersion,
        });
        context.CupSelectionFormulaActivations.Add(new CupSelectionFormulaActivationEntity
        {
            Id = Features.Cups.SelectionFormula.CupSelectionFormulaLifecycle.ActivationId,
            EffectiveFromSeasonNumber = InitialSeason,
            FormulaVersion = Features.Cups.SelectionFormula.CupSelectionFormulaLifecycle.FormulaVersion,
        });

        foreach (CatalogAthlete athlete in preparation.Selection.Selected)
        {
            context.SaveAthletes.Add(SaveAthleteEntity.FromCatalog(athlete));
        }
    }

    /// <summary>
    /// Inserts Season 1 (no Superleague), its versioned feeder leagues (8 for v1,
    /// 24 F1/F2/F3 for v2 tiered) and all 2,048 Season 1 memberships from the
    /// already-validated inaugural draw. Runs inside the save-creation
    /// transaction so RNG state and memberships commit together. Membership is
    /// the sporting source of truth; <see cref="SaveAthleteEntity"/> card rows
    /// are left untouched. F1 keeps the canonical "{Color} League" name so
    /// existing feeder identity is preserved; F2/F3 carry explicit suffixes and
    /// divisions so tiers never depend on name parsing.
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
        List<LeagueEntity> leagues = await CreateFeederLeaguesAsync(context, season.Id, rules, cancellationToken).ConfigureAwait(false);
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
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);
        List<LeagueEntity> leagues = [];
        bool tiered = rules.FeederDivisionsPerColor == 3;
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

            if (!tiered)
            {
                continue;
            }

            leagues.Add(new LeagueEntity
            {
                SeasonId = seasonId,
                SportingColor = (int)color,
                Kind = (int)LeagueKind.Feeder,
                FeederDivision = (int)SimulationKernel.Leagues.FeederDivision.Second,
                Name = $"{color} League F2",
            });
            leagues.Add(new LeagueEntity
            {
                SeasonId = seasonId,
                SportingColor = (int)color,
                Kind = (int)LeagueKind.Feeder,
                FeederDivision = (int)SimulationKernel.Leagues.FeederDivision.Third,
                Name = $"{color} League F3",
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
        Dictionary<(SportingColor Color, int Division), int> leagueIds = leagues.ToDictionary(
            l => ((SportingColor)l.SportingColor, l.FeederDivision), l => l.Id);
        bool tiered = rules.FeederDivisionsPerColor == 3;
        List<SeasonMembershipEntity> memberships = new(rules.TotalAthletesInSave);
        foreach (InauguralDrawEntry entry in draw.Entries)
        {
            if (!athleteIds.TryGetValue(entry.Name, out int athleteId))
            {
                throw new InvalidOperationException($"Season 1 draw references unknown athlete '{entry.Name}'.");
            }

            int? leagueId = ResolveInauguralLeague(entry, leagueIds, tiered, rules);
            memberships.Add(new SeasonMembershipEntity
            {
                SeasonId = seasonId,
                LeagueId = leagueId,
                SaveAthleteId = athleteId,
                SportingColor = (int)entry.SportingColor,
                DrawIndex = entry.DrawIndex,
            });
        }

        return memberships;
    }

    internal static int? ResolveInauguralLeague(
        InauguralDrawEntry entry,
        Dictionary<(SportingColor Color, int Division), int> leagueIds,
        bool tiered,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(leagueIds);
        ArgumentNullException.ThrowIfNull(rules);
        if (!entry.IsLeagueMember)
        {
            return null;
        }

        int division = !tiered
            ? (int)SimulationKernel.Leagues.FeederDivision.First
            : entry.DrawIndex < rules.LeagueSize
                ? (int)SimulationKernel.Leagues.FeederDivision.First
                : entry.DrawIndex < 2 * rules.LeagueSize
                    ? (int)SimulationKernel.Leagues.FeederDivision.Second
                    : (int)SimulationKernel.Leagues.FeederDivision.Third;
        if (!leagueIds.TryGetValue((entry.SportingColor, division), out int leagueId))
        {
            throw new InvalidOperationException(
                $"Season 1 is missing the {entry.SportingColor} division {division} league for draw index {entry.DrawIndex}.");
        }

        return leagueId;
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
    /// Destructive file removal holds the per-save mutation, migration and
    /// snapshot gates (in that order) so an active simulation write cannot
    /// retain an invalid handle or write into a removed database. Ordinary
    /// queries never take the mutation gate and stay concurrent.
    /// </summary>
    public async Task DeleteAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        string root = GetSavesRoot();
        string path = SaveFileNaming.GetSaveFilePath(root, saveId);
        if (!File.Exists(path))
        {
            throw new SaveNotFoundException(saveId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        SemaphoreSlim mutationGate = AdvanceRoundSaveLock.Acquire(saveId);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SemaphoreSlim migrationGate = AcquireMigrationGate(saveId);
            await migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SemaphoreSlim snapshotGate = AcquireSnapshotGate(saveId);
                await snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    DeleteLiveSaveFile(path, saveId);
                }
                finally
                {
                    snapshotGate.Release();
                }
            }
            finally
            {
                migrationGate.Release();
            }
        }
        finally
        {
            mutationGate.Release();
        }

        string checkpointDirectory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, saveId);
        if (Directory.Exists(checkpointDirectory))
        {
            // Drop pooled handles to checkpoint databases before removing the
            // directory; otherwise Windows directory deletion races open
            // mappings. Only this save's checkpoint files are touched.
            ClearCheckpointPools(checkpointDirectory);
            try
            {
                Directory.Delete(checkpointDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove checkpoint directory for deleted save {SaveId}.", saveId);
            }
        }
    }

    private void DeleteLiveSaveFile(string path, Guid saveId)
    {
        // Re-check under the gates: a concurrent restore/import could have
        // replaced the file after the pre-gate existence check.
        if (!File.Exists(path))
        {
            throw new SaveNotFoundException(saveId);
        }

        // Drop pooled handles before removing the file so the delete and
        // companion cleanup never race open SQLite mappings (Windows share
        // violations, Linux fd resurrection).
        ClearSaveConnections(path);
        File.Delete(path);
        DeleteCompanionFiles(path);
        ClearSaveConnections(path);
        InvalidateMigrationVerified(saveId);
    }

    private static void ClearCheckpointPools(string checkpointDirectory)
    {
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(checkpointDirectory, "*.db");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string candidate in candidates)
        {
            try
            {
                ClearSaveConnections(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Exports a complete save as a portable artifact: the SQLite save
    /// database plus manifest/version metadata zipped together. The artifact
    /// is built from one consistent committed snapshot captured with
    /// <c>VACUUM INTO</c>: embedded database, checksum and manifest all
    /// describe that same snapshot file, never separate live reads. A busy or
    /// incomplete WAL checkpoint can no longer silently omit committed frames;
    /// capture reads through the WAL and fails explicitly without emitting a
    /// success artifact. Never mutates sporting state or the live file.
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
        string root = GetSavesRoot();
        string snapshotPath = Path.Combine(root, $".export-{saveId:N}-{Guid.NewGuid():N}.tmp");
        SemaphoreSlim snapshotGate = AcquireSnapshotGate(saveId);
        await snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                throw new SaveNotFoundException(saveId);
            }

            await CaptureDatabaseSnapshotAsync(path, snapshotPath, cancellationToken).ConfigureAwait(false);
            try
            {
                SaveDetailRecord detail = await ReadDetailFromSnapshotAsync(snapshotPath, saveId, cancellationToken).ConfigureAwait(false);
                return BuildExportFromSnapshot(snapshotPath, detail, _timeProvider.GetUtcNow());
            }
            finally
            {
                // Pool first: deleting a file with pooled SQLite handles open
                // throws a Windows sharing violation; clearing after is too late.
                ClearSaveConnections(snapshotPath);
                DeleteFileBestEffort(snapshotPath);
                DeleteCompanionFiles(snapshotPath);
            }
        }
        finally
        {
            snapshotGate.Release();
        }
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
        StagedImport? staged = null;
        try
        {
            staged = await StageValidatedImportAsync(bundleBytes, stagingDirectory, cancellationToken).ConfigureAwait(false);
            return await PlaceStagedImportAsync(root, staged, overwrite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (staged is not null)
            {
                ClearSaveConnections(staged.StagedPath);
                ClearSaveConnections(SaveFileNaming.GetSaveFilePath(root, staged.Manifest.SaveId));
            }

            DeleteDirectoryBestEffort(stagingDirectory);
        }
    }

    private sealed record StagedImport(string StagedPath, SaveBundleManifest Manifest);

    private async Task<StagedImport> StageValidatedImportAsync(
        byte[] bundleBytes,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        SaveBundle.Extraction extraction = await SaveBundle
            .ExtractToStagingAsync(bundleBytes, stagingDirectory, cancellationToken)
            .ConfigureAwait(false);
        SaveBundleManifest manifest = extraction.Manifest;
        SaveRulesCompatibility.EnsureImportableRules(manifest);
        await VerifyStagedDatabaseAsync(extraction.StagedDatabasePath, manifest, cancellationToken).ConfigureAwait(false);
        await MigrateStagedDatabaseIfNeededAsync(extraction.StagedDatabasePath, manifest, cancellationToken).ConfigureAwait(false);
        // Staged verification opens pooled handles; drop them before any
        // file placement so Windows moves/copies never race open mappings.
        ClearSaveConnections(extraction.StagedDatabasePath);
        return new StagedImport(extraction.StagedDatabasePath, manifest);
    }

    private async Task<SaveDetailRecord> PlaceStagedImportAsync(
        string root,
        StagedImport staged,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staged);
        string target = SaveFileNaming.GetSaveFilePath(root, staged.Manifest.SaveId);
        // Placement replaces or creates the live file: hold the per-save
        // mutation and migration gates so an active simulation write cannot
        // retain an invalid handle. Snapshot captures nest inside (gates are
        // always acquired Advance -> Migration -> Snapshot, never reverse).
        SemaphoreSlim mutationGate = AdvanceRoundSaveLock.Acquire(staged.Manifest.SaveId);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SemaphoreSlim migrationGate = AcquireMigrationGate(staged.Manifest.SaveId);
            await migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(target))
                {
                    if (!overwrite)
                    {
                        throw new SaveAlreadyExistsException(staged.Manifest.SaveId);
                    }

                    await CreateCheckpointAsync(staged.Manifest.SaveId, "pre-import-overwrite", cancellationToken).ConfigureAwait(false);
                    await ReplaceLiveDatabaseAsync(staged.Manifest.SaveId, target, staged.StagedPath, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ClearSaveConnections(staged.StagedPath);
                    ClearSaveConnections(target);
                    File.Move(staged.StagedPath, target);
                }

                // File replacement bypasses pooled SQLite handles, which would
                // otherwise keep serving pages from the previous file content.
                ClearSaveConnections(target);
                InvalidateMigrationVerified(staged.Manifest.SaveId);
                return await ReadDetailAsync(staged.Manifest.SaveId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                migrationGate.Release();
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    /// <summary>
    /// Creates a verified technical recovery checkpoint of the last known
    /// valid save boundary: one consistent committed snapshot (via
    /// <c>VACUUM INTO</c>), identity verification of the snapshot, and sidecar
    /// persistence. Embedded database, checksum and sidecar all describe that
    /// same snapshot; a busy WAL checkpoint can no longer silently omit
    /// committed frames. Used before migrations, import overwrites, and large
    /// bulk operations, and for restoring after a software bug/failure. This
    /// is technical recovery, never normal gameplay rewind.
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
        string root = GetSavesRoot();
        CheckpointPaths paths = PrepareCheckpointPaths(root, saveId);
        string normalizedReason = SaveCheckpointFiles.NormalizeReason(reason);

        SemaphoreSlim snapshotGate = AcquireSnapshotGate(saveId);
        await snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(livePath))
            {
                throw new SaveNotFoundException(saveId);
            }

            await CaptureDatabaseSnapshotAsync(livePath, paths.TempCopy, cancellationToken).ConfigureAwait(false);
            try
            {
                SaveDetailRecord detail = await ReadDetailFromSnapshotAsync(paths.TempCopy, saveId, cancellationToken).ConfigureAwait(false);
                DateTimeOffset now = _timeProvider.GetUtcNow();
                return await PersistSnapshotCheckpointAsync(paths, detail, normalizedReason, now, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ClearSaveConnections(paths.TempCopy);
                DeleteFileBestEffort(paths.TempCopy);
                DeleteCompanionFiles(paths.TempCopy);
                throw;
            }
        }
        finally
        {
            snapshotGate.Release();
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
    /// Verifies the captured snapshot, moves it into place, persists a
    /// verified sidecar, prunes old checkpoints and reports the new checkpoint
    /// record. The checksum, embedded database and sidecar all describe the
    /// same snapshot file. A failure leaves prior verified checkpoints intact.
    /// </summary>
    private async Task<CheckpointRecord> PersistSnapshotCheckpointAsync(
        CheckpointPaths paths,
        SaveDetailRecord detail,
        string normalizedReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedReason);

        await VerifyCheckpointCopyAsync(paths.TempCopy, detail.SaveId, cancellationToken).ConfigureAwait(false);
        string copySha = SaveBundle.ComputeFileSha256Hex(paths.TempCopy);
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
    /// Captures one consistent committed database snapshot with
    /// <c>VACUUM INTO</c>, which reads through the WAL and includes the latest
    /// committed data even when a pinned reader blocks a WAL checkpoint. The
    /// destination is rebuilt with all committed frames, so a later file copy
    /// cannot silently omit them. The snapshot is set to WAL journal mode to
    /// match live save files. Any failure deletes the partial destination and
    /// throws without emitting a success artifact; live data and prior
    /// verified checkpoints are unchanged.
    /// </summary>
    private static async Task CaptureDatabaseSnapshotAsync(
        string livePath,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(livePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        DeleteFileBestEffort(snapshotPath);
        DeleteCompanionFiles(snapshotPath);

        await VacuumLiveIntoSnapshotAsync(livePath, snapshotPath, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(snapshotPath))
        {
            throw new InvalidOperationException(
                "Save snapshot capture failed; live data and prior verified checkpoints are unchanged.");
        }

        // VACUUM rebuilds in delete journal mode; restore WAL so exported and
        // checkpoint databases match the live save format (sporting data is unchanged).
        await ApplySnapshotWalModeAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VacuumLiveIntoSnapshotAsync(
        string livePath,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        string escaped = snapshotPath.Replace("'", "''", StringComparison.Ordinal);
        SqliteConnectionStringBuilder sourceBuilder = new()
        {
            DataSource = livePath,
            Pooling = false,
        };
        try
        {
            using SqliteConnection connection = new(sourceBuilder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using (DbCommand busy = connection.CreateCommand())
            {
                busy.CommandText = "PRAGMA busy_timeout=5000;";
                _ = await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (DbCommand vacuum = connection.CreateCommand())
            {
                vacuum.CommandText = $"VACUUM INTO '{escaped}';";
                _ = await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteFileBestEffort(snapshotPath);
            DeleteCompanionFiles(snapshotPath);
            throw new InvalidOperationException(
                "Save snapshot capture failed; live data and prior verified checkpoints are unchanged.", ex);
        }
    }

    private static async Task ApplySnapshotWalModeAsync(
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        try
        {
            SqliteConnectionStringBuilder destinationBuilder = new()
            {
                DataSource = snapshotPath,
                Pooling = false,
            };
            using SqliteConnection destination = new(destinationBuilder.ToString());
            await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
            using DbCommand wal = destination.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            _ = await wal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteFileBestEffort(snapshotPath);
            DeleteCompanionFiles(snapshotPath);
            throw new InvalidOperationException(
                "Save snapshot capture failed; live data and prior verified checkpoints are unchanged.", ex);
        }
    }

    /// <summary>
    /// Reads the save detail from a captured snapshot file (not the live
    /// file) so manifest/sidecar and checksum describe the same snapshot.
    /// </summary>
    private async Task<SaveDetailRecord> ReadDetailFromSnapshotAsync(
        string snapshotPath,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        try
        {
            using SaveDbContext context = _factory.Create(snapshotPath);
            SaveRows rows = await LoadRowsAsync(context, cancellationToken).ConfigureAwait(false);
            return MapDetail(saveId, rows);
        }
        finally
        {
            ClearSaveConnections(snapshotPath);
        }
    }

    private static ExportRecord BuildExportFromSnapshot(
        string snapshotPath,
        SaveDetailRecord detail,
        DateTimeOffset exportedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentNullException.ThrowIfNull(detail);
        string sha256 = SaveBundle.ComputeFileSha256Hex(snapshotPath);
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
        byte[] zipBytes = SaveBundle.Create(snapshotPath, manifest);
        return new ExportRecord(
            detail.SaveId,
            $"{detail.SaveId:N}.mtgsave.zip",
            "application/zip",
            zipBytes,
            manifest);
    }

    /// <summary>
    /// Replaces the live save file with a staged database under the per-save
    /// snapshot gate so a concurrent snapshot capture cannot read a torn file.
    /// Pooled handles are dropped before the copy so old WAL frames cannot
    /// resurrect after the replacement when pools close. Callers hold the
    /// per-save mutation and migration gates; this helper only takes the
    /// snapshot gate to preserve the Advance -&gt; Migration -&gt; Snapshot order.
    /// </summary>
    private static async Task ReplaceLiveDatabaseAsync(
        Guid saveId,
        string livePath,
        string stagedPath,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim snapshotGate = AcquireSnapshotGate(saveId);
        await snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearSaveConnections(livePath);
            ClearSaveConnections(stagedPath);
            File.Copy(stagedPath, livePath, overwrite: true);
            DeleteCompanionFiles(livePath);
            ClearSaveConnections(livePath);
        }
        finally
        {
            snapshotGate.Release();
        }
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
    /// recovery checkpoint. A corrupt backup never touches live data; a
    /// healthy live save gains a safety checkpoint first while a damaged or
    /// missing live file is recovered from the checkpoint with surviving
    /// damaged bytes preserved for diagnosis. The replacement holds the
    /// per-save mutation and migration gates so active writers cannot retain
    /// invalid handles; snapshot captures nest inside those gates.
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

        await VerifyCheckpointForRestoreAsync(saveId, checkpointId, databasePath, sidecarPath, cancellationToken).ConfigureAwait(false);

        string livePath = GetSaveFilePath(saveId);
        SemaphoreSlim mutationGate = AdvanceRoundSaveLock.Acquire(saveId);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SemaphoreSlim migrationGate = AcquireMigrationGate(saveId);
            await migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await IsLiveHealthyAsync(saveId, cancellationToken).ConfigureAwait(false))
                {
                    await CreateCheckpointAsync(saveId, "pre-restore-safety", cancellationToken).ConfigureAwait(false);
                }
                else if (File.Exists(livePath))
                {
                    await PreserveDamagedLiveCopyAsync(saveId, checkpointId, cancellationToken).ConfigureAwait(false);
                }

                await OverwriteLiveFromCheckpointAsync(saveId, checkpointId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                migrationGate.Release();
            }
        }
        finally
        {
            mutationGate.Release();
        }

        _logger.LogWarning(
            "Restored save {SaveId} from technical recovery checkpoint {CheckpointId}.",
            saveId,
            checkpointId);
        return await ReadDetailAsync(saveId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checksum- and identity-verifies one checkpoint before a restore may
    /// touch live data. Corrupt backups are rejected here, never after.
    /// </summary>
    private async Task VerifyCheckpointForRestoreAsync(
        Guid saveId,
        Guid checkpointId,
        string databasePath,
        string sidecarPath,
        CancellationToken cancellationToken)
    {
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
    }

    /// <summary>
    /// Probes whether the live save file opens with all required rows and
    /// parsable rules/RNG snapshots. Any validation, readability or missing
    /// file failure reports unhealthy (never throws) so the caller can take
    /// the damaged/missing recovery path instead of the healthy safety
    /// checkpoint path.
    /// </summary>
    private async Task<bool> IsLiveHealthyAsync(Guid saveId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await ReadDetailAsync(saveId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SaveNotFoundException
            or InvalidOperationException
            or DbException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Preserves surviving damaged live bytes beside the checkpoints for
    /// diagnosis before a recovery restore overwrites them. The copy has no
    /// sidecar and a distinct <c>damaged-pre-restore-</c> name so it is never
    /// mistaken for a verified checkpoint. Best effort: preservation failures
    /// are logged and the restore still proceeds.
    /// </summary>
    private async Task PreserveDamagedLiveCopyAsync(
        Guid saveId,
        Guid checkpointId,
        CancellationToken cancellationToken)
    {
        string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(GetSavesRoot(), saveId);
        string livePath = GetSaveFilePath(saveId);
        if (!File.Exists(livePath))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        string damagedPath = SaveCheckpointFiles.GetDamagedLiveBackupPath(directory, checkpointId);

        SemaphoreSlim snapshotGate = AcquireSnapshotGate(saveId);
        await snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(livePath))
            {
                return;
            }

            await WriteDamagedLiveCopyAsync(saveId, checkpointId, livePath, damagedPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            snapshotGate.Release();
        }
    }

    /// <summary>
    /// Writes one damaged-live preservation copy: a WAL-aware snapshot when
    /// the live file is still a readable database, else a raw byte copy.
    /// </summary>
    private async Task WriteDamagedLiveCopyAsync(
        Guid saveId,
        Guid checkpointId,
        string livePath,
        string damagedPath,
        CancellationToken cancellationToken)
    {
        ClearSaveConnections(livePath);
        try
        {
            await CaptureDatabaseSnapshotAsync(livePath, damagedPath, cancellationToken).ConfigureAwait(false);
            ClearSaveConnections(damagedPath);
            DeleteCompanionFiles(damagedPath);
            _logger.LogWarning(
                "Preserved damaged live save {SaveId} bytes at {DamagedPath} before restoring checkpoint {CheckpointId}.",
                saveId,
                damagedPath,
                checkpointId);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException or IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(
                ex,
                "Snapshot preservation failed for damaged save {SaveId}; falling back to a raw byte copy.",
                saveId);
            ClearSaveConnections(damagedPath);
            DeleteFileBestEffort(damagedPath);
            DeleteCompanionFiles(damagedPath);
        }

        try
        {
            ClearSaveConnections(livePath);
            File.Copy(livePath, damagedPath, overwrite: true);
            ClearSaveConnections(damagedPath);
            DeleteCompanionFiles(damagedPath);
            _logger.LogWarning(
                "Preserved damaged live save {SaveId} raw bytes at {DamagedPath} before restoring checkpoint {CheckpointId}.",
                saveId,
                damagedPath,
                checkpointId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Could not preserve damaged live save {SaveId} bytes; continuing with checkpoint restore.",
                saveId);
        }
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
    /// Verifies a checkpoint database copy opens with required rows, the
    /// expected save identity, and parsable rules/RNG snapshots (the same
    /// bar as a live open). Cleans up SQLite companions opened by the check.
    /// A checkpoint that fails here is rejected before anything live is
    /// touched. Throws <see cref="InvalidOperationException"/> (or the
    /// underlying <see cref="DbException"/>/IO failure) without modifying
    /// live data or other checkpoints.
    /// </summary>
    private async Task VerifyCheckpointCopyAsync(
        string checkpointDatabasePath,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        try
        {
            using SaveDbContext context = _factory.Create(checkpointDatabasePath);
            SaveRows rows = await LoadRowsAsync(context, cancellationToken).ConfigureAwait(false);
            _ = MapDetail(saveId, rows);
        }
        finally
        {
            // Pool first: companion deletes with pooled handles open throw on
            // Windows; clearing after is too late.
            ClearSaveConnections(checkpointDatabasePath);
            DeleteCompanionFiles(checkpointDatabasePath);
        }
    }

    /// <summary>
    /// Overwrites the live save file with a verified checkpoint copy and
    /// verifies the result opens. The checkpoint stages through a temp file
    /// so a failed replacement leaves prior live bytes in place; the
    /// checkpoint itself is only read. A missing live file is recreated.
    /// Callers hold the per-save mutation and migration gates (Restore) or the
    /// migration gate inside <see cref="EnsureMigratedAsync"/>; this helper
    /// only takes the snapshot gate to preserve Advance -&gt; Migration -&gt;
    /// Snapshot ordering and avoid reentrant deadlocks.
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

        cancellationToken.ThrowIfCancellationRequested();
        SemaphoreSlim snapshotGate = AcquireSnapshotGate(saveId);
        await snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingPath = null;
        try
        {
            if (!File.Exists(checkpointPath))
            {
                throw new SaveCheckpointNotFoundException(saveId, checkpointId);
            }

            stagingPath = PrepareRestoreStagingPath(livePath, saveId);
            ReplaceLiveWithStagedCopy(checkpointPath, livePath, stagingPath);
            stagingPath = null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            if (!File.Exists(checkpointPath))
            {
                throw new SaveCheckpointNotFoundException(saveId, checkpointId);
            }

            throw;
        }
        finally
        {
            if (stagingPath is not null)
            {
                ClearSaveConnections(stagingPath);
                DeleteFileBestEffort(stagingPath);
                DeleteCompanionFiles(stagingPath);
            }

            snapshotGate.Release();
        }

        ClearSaveConnections(livePath);
        InvalidateMigrationVerified(saveId);
        _ = await ReadDetailAsync(saveId, cancellationToken).ConfigureAwait(false);
    }

    private string PrepareRestoreStagingPath(string livePath, Guid saveId)
    {
        string? liveDirectory = Path.GetDirectoryName(livePath);
        if (!string.IsNullOrEmpty(liveDirectory))
        {
            Directory.CreateDirectory(liveDirectory);
        }

        string stagingPath = Path.Combine(
            liveDirectory ?? GetSavesRoot(),
            $".restore-{saveId:N}-{Guid.NewGuid():N}.tmp");
        ClearSaveConnections(stagingPath);
        DeleteFileBestEffort(stagingPath);
        DeleteCompanionFiles(stagingPath);
        return stagingPath;
    }

    private static void ReplaceLiveWithStagedCopy(string checkpointPath, string livePath, string stagingPath)
    {
        ClearSaveConnections(livePath);
        ClearSaveConnections(checkpointPath);
        File.Copy(checkpointPath, stagingPath);
        ClearSaveConnections(stagingPath);
        File.Move(stagingPath, livePath, overwrite: true);
        DeleteCompanionFiles(livePath);
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
        ClearSaveConnections(path);
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

        ClearSaveConnections(path);
    }
}
