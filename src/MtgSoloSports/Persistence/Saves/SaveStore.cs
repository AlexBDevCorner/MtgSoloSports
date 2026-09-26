using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.ImportCatalog;
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
    /// Creates one independent save universe: metadata, immutable Rules v1
    /// snapshot, the deterministic 2,048-athlete universe selected from
    /// <paramref name="catalogAthletes"/> with the save RNG, and the
    /// post-selection RNG state, all committed in a single transaction.
    /// Selection consumes only the save RNG in sporting-color enum order from
    /// name-sorted pools, so an equivalent seed plus catalog reproduces the
    /// equivalent universe. Any failure deletes the save file so no partially
    /// initialized save is left behind.
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
            RulesV1.RulesVersion,
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
        Pcg32State AdvancedRng,
        string RulesJson);

    private static UniversePreparation PrepareUniverse(
        IReadOnlyList<CatalogAthlete> catalogAthletes,
        ulong seed,
        ulong stream)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        Pcg32V1 rng = new(seed, stream);
        UniverseSelection selection = UniverseSelector.Select(catalogAthletes, rng, rules);
        return new UniversePreparation(rules, selection, rng.Snapshot(), RulesSnapshotDocument.FromRules(rules).ToJson());
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
            RulesVersion = RulesV1.RulesVersion,
            RulesJson = preparation.RulesJson,
        });
        context.RngStates.Add(RngStateEntity.FromState(preparation.AdvancedRng));

        foreach (CatalogAthlete athlete in preparation.Selection.Selected)
        {
            context.SaveAthletes.Add(SaveAthleteEntity.FromCatalog(athlete));
        }
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
    /// Explicitly deletes one save's own database file plus its SQLite
    /// companion files (-wal/-shm/-journal). The resolved path must stay
    /// inside the saves root; nothing else is ever removed.
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
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            string companion = path + suffix;
            if (File.Exists(companion))
            {
                File.Delete(companion);
            }
        }

        return Task.CompletedTask;
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
        RulesSnapshotDocument.FromJson(rulesRow.RulesJson).ToRules();

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
