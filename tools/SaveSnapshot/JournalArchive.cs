using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtgSoloSports.SaveSnapshot;

/// <summary>
/// Deterministic local journalist archive for Superleague stage reviews (MSS-135).
/// The agent writes prose in chat; this helper only finds earlier coverage and
/// commits an article with matching evidence metadata. All writes stay under the
/// configured archive root, identity is save-ID/season/stage, and repeats need
/// an explicit revision with the prior text preserved. Reads are bounded; a
/// historical cutoff excludes later articles and later notebook entries.
/// </summary>
public static class JournalArchive
{
    public const int ArchiveFormatVersion = 1;

    public const int SupportedEvidenceSchemaVersion = 1;

    public const string SupportedEvidenceType = "superleague-stage-evidence";

    public const int MaxArticleBytes = 32 * 1024;

    public const int MaxNoteChars = 2000;

    public const int DefaultRecent = 3;

    public const int MaxRecent = 10;

    public const int NotebookExcerptMaxChars = 12000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public sealed record SaveFile(
        int ArchiveFormatVersion,
        Guid SaveId,
        string SaveName,
        DateTimeOffset CreatedUtc);

    public sealed record IndexStage(
        int StageNumber,
        int Revision,
        string ArticlePath,
        string EvidencePath,
        string MetaPath,
        string StageChecksum,
        string DatabaseSha256,
        DateTimeOffset GeneratedUtc,
        DateTimeOffset UpdatedUtc);

    public sealed record IndexSeason(int SeasonNumber, IReadOnlyList<IndexStage> Stages);

    public sealed record IndexFile(
        int ArchiveFormatVersion,
        Guid SaveId,
        string SaveName,
        DateTimeOffset UpdatedUtc,
        IReadOnlyList<IndexSeason> Seasons);

    public sealed record MetaFile(
        int ArchiveFormatVersion,
        Guid SaveId,
        string SaveName,
        int SeasonNumber,
        int StageNumber,
        int Revision,
        string ArticlePath,
        string EvidencePath,
        string ArticleSha256,
        string EvidenceSha256,
        string DatabaseSha256,
        string StageChecksum,
        int EvidenceSchemaVersion,
        string EvidenceType,
        DateTimeOffset EvidenceGeneratedUtc,
        DateTimeOffset SnapshotExportedUtc,
        DateTimeOffset SnapshotAcquiredUtc,
        int SnapshotSchemaVersion,
        int SnapshotRulesVersion,
        int SavedRulesVersion,
        int StagesPerSeason,
        int RoundsPerStage,
        int LeagueSize,
        DateTimeOffset CommittedUtc,
        bool Conflict,
        string? PriorDatabaseSha256,
        string? PriorStageChecksum,
        string? Note,
        SnapshotAtCommitInfo? SnapshotAtCommit);

    public sealed record SnapshotAtCommitInfo(
        string DatabaseSha256,
        DateTimeOffset ExportedUtc,
        DateTimeOffset AcquiredUtc);

    public sealed record ContextArticle(
        int SeasonNumber,
        int StageNumber,
        int Revision,
        string ArticlePath,
        string ArticleText,
        bool Truncated,
        string ArticleSha256,
        string StageChecksum,
        string DatabaseSha256,
        DateTimeOffset GeneratedUtc);

    public sealed record ContextResult(
        Guid SaveId,
        string SaveName,
        int? CutoffSeason,
        int? CutoffStage,
        IReadOnlyList<ContextArticle> Recent,
        string NotebookPath,
        string NotebookExcerpt,
        bool NotebookTruncated,
        bool NotebookMissing,
        int ExcludedLaterArticles,
        int ExcludedLaterNotebookEntries,
        int ExcludedUnscopedNotebookSections,
        IReadOnlyList<int> GapsInCutoffSeason,
        IReadOnlyList<string> Disclosures);

    public static string ResolveArchiveRoot(SnapshotConfig config, string? archiveOverride)
    {
        ArgumentNullException.ThrowIfNull(config);
        string? raw = archiveOverride ?? config.ArchiveDirectory;
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException(
                "No journalist archive is configured. Set 'archiveDirectory' in the snapshot config (for example \"./journalist-archive\") or pass '--archive <dir>', then run 'archive init'.");
        }

        return Path.GetFullPath(raw.Trim());
    }

    public static string SaveDirFor(string archiveRoot, Guid saveId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
        return Path.Combine(Path.GetFullPath(archiveRoot), $"save-{saveId:D}");
    }

    public static SaveFile Init(string archiveRoot, Guid saveId, string saveName, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveRoot);
        if (saveId.Equals(Guid.Empty))
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(saveName);
        string root = Path.GetFullPath(archiveRoot);
        string saveDir = SaveDirFor(root, saveId);
        Directory.CreateDirectory(saveDir);

        string savePath = Path.Combine(saveDir, "save.json");
        if (File.Exists(savePath))
        {
            SaveFile existing = ReadJson<SaveFile>(savePath);
            if (!existing.SaveId.Equals(saveId))
            {
                throw new InvalidOperationException(
                    $"Archive save file '{savePath}' belongs to save '{existing.SaveId:D}', not '{saveId:D}'. Refusing to overwrite another save's archive; choose a different '--archive' root.");
            }

            EnsureIndexExists(saveDir, saveId, saveName, now);
            return existing;
        }

        SaveFile fresh = new(ArchiveFormatVersion, saveId, saveName.Trim(), now);
        WriteTextAtomically(savePath, JsonSerializer.Serialize(fresh, JsonOptions));
        EnsureIndexExists(saveDir, saveId, saveName, now);
        return fresh;
    }

    internal static string? ValidateCommitNote(string? note)
    {
        string? trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is not null && trimmed.Length > MaxNoteChars)
        {
            throw new ArgumentException($"Editorial note must be at most {MaxNoteChars} characters.", nameof(note));
        }

        return trimmed;
    }

    internal static byte[] ReadArticleForCommit(string articleSourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(articleSourcePath);
        if (!articleSourcePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Article file must have a '.md' extension.", nameof(articleSourcePath));
        }

        return ReadBoundedFile(articleSourcePath, MaxArticleBytes, "article");
    }

    internal static void ValidateArticleText(string articleText, string articleSourcePath)
    {
        if (string.IsNullOrWhiteSpace(articleText))
        {
            throw new InvalidOperationException($"Article file '{articleSourcePath}' is empty.");
        }
    }

    internal static void ValidateEvidenceForCommit(
        EvidenceProvenance evidence, Guid expectedSaveId, int seasonNumber, int stageNumber, string evidenceSourcePath)
    {
        if (!string.Equals(evidence.EvidenceType, SupportedEvidenceType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Evidence file '{evidenceSourcePath}' has evidenceType '{evidence.EvidenceType}', expected '{SupportedEvidenceType}'.");
        }

        if (evidence.SchemaVersion != SupportedEvidenceSchemaVersion)
        {
            throw new InvalidOperationException($"Evidence file '{evidenceSourcePath}' has schemaVersion {evidence.SchemaVersion}, supported {SupportedEvidenceSchemaVersion}.");
        }

        if (!string.Equals(evidence.Outcome, "completed", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Evidence file '{evidenceSourcePath}' has outcome '{evidence.Outcome}'; only 'completed' stages can be archived.");
        }

        if (!evidence.SaveId.Equals(expectedSaveId))
        {
            throw new InvalidOperationException(
                $"Evidence save '{evidence.SaveId:D}' does not match the configured save '{expectedSaveId:D}'. Refusing to mix saves; check '--config' and '--evidence'.");
        }

        if (evidence.SeasonNumber != seasonNumber || evidence.StageNumber != stageNumber)
        {
            throw new InvalidOperationException(
                $"Evidence target season {evidence.SeasonNumber} stage {evidence.StageNumber} does not match the requested season {seasonNumber} stage {stageNumber}. Refusing to archive mismatched evidence.");
        }
    }

    internal sealed record CommitArchive(string Root, string SaveDir, string SavePath, string IndexPath, SaveFile SaveFile, IndexFile Index);

    internal sealed record CommitPaths(string SeasonDir, string ArticlePath, string MetaPath, string EvidencePath);

    internal static CommitArchive LoadCommitArchive(string archiveRoot, Guid expectedSaveId)
    {
        string root = Path.GetFullPath(archiveRoot);
        string saveDir = SaveDirFor(root, expectedSaveId);
        string savePath = Path.Combine(saveDir, "save.json");
        string indexPath = Path.Combine(saveDir, "index.json");
        if (!File.Exists(savePath) || !File.Exists(indexPath))
        {
            throw new InvalidOperationException(
                $"No journalist archive for save '{expectedSaveId:D}' under '{root}'. Run 'archive init' first.");
        }

        SaveFile saveFile = ReadJson<SaveFile>(savePath);
        if (!saveFile.SaveId.Equals(expectedSaveId))
        {
            throw new InvalidOperationException(
                $"Archive save file '{savePath}' belongs to save '{saveFile.SaveId:D}', not '{expectedSaveId:D}'. Refusing to overwrite another save's article.");
        }

        IndexFile index = ReadJson<IndexFile>(indexPath);
        if (!index.SaveId.Equals(expectedSaveId))
        {
            throw new InvalidOperationException($"Archive index '{indexPath}' belongs to another save. Refusing to mix saves.");
        }

        return new CommitArchive(root, saveDir, savePath, indexPath, saveFile, index);
    }

    internal static CommitPaths ResolveCommitPaths(CommitArchive archive, int seasonNumber, int stageNumber)
    {
        string seasonDir = Path.Combine(archive.SaveDir, $"season-{seasonNumber:D2}");
        string articlePath = Path.Combine(seasonDir, $"stage-{stageNumber:D2}.md");
        string metaPath = Path.Combine(seasonDir, $"stage-{stageNumber:D2}.meta.json");
        string evidencePath = Path.Combine(seasonDir, $"stage-{stageNumber:D2}.evidence.json");
        EnsureUnderRoot(archive.Root, articlePath);
        EnsureUnderRoot(archive.Root, metaPath);
        EnsureUnderRoot(archive.Root, evidencePath);
        Directory.CreateDirectory(seasonDir);
        return new CommitPaths(seasonDir, articlePath, metaPath, evidencePath);
    }

    internal static MetaFile? HandleCommitRevision(CommitPaths paths, int seasonNumber, int stageNumber, bool isRevision)
    {
        MetaFile? existing = File.Exists(paths.MetaPath) ? ReadJson<MetaFile>(paths.MetaPath) : null;
        if (existing is null)
        {
            if (isRevision)
            {
                throw new InvalidOperationException(
                    $"Season {seasonNumber} stage {stageNumber} has no archived article yet; '--revision' needs an existing entry to revise.");
            }

            return null;
        }

        if (!File.Exists(paths.ArticlePath) || !File.Exists(paths.EvidencePath))
        {
            throw new InvalidOperationException(
                $"Archive for season {seasonNumber} stage {stageNumber} is incomplete (article or evidence missing beside '{paths.MetaPath}'). Inspect the files manually; refusing to overwrite a torn entry.");
        }

        if (!isRevision)
        {
            throw new InvalidOperationException(
                $"Season {seasonNumber} stage {stageNumber} is already archived (rev {existing.Revision}). Re-requesting the same stage needs an explicit '--revision'; the prior text is preserved and never overwritten silently.");
        }

        string backupArticle = Path.Combine(paths.SeasonDir, $"stage-{stageNumber:D2}.r{existing.Revision}.md");
        string backupMeta = Path.Combine(paths.SeasonDir, $"stage-{stageNumber:D2}.r{existing.Revision}.meta.json");
        string backupEvidence = Path.Combine(paths.SeasonDir, $"stage-{stageNumber:D2}.r{existing.Revision}.evidence.json");
        if (File.Exists(backupArticle) || File.Exists(backupMeta) || File.Exists(backupEvidence))
        {
            throw new InvalidOperationException(
                $"Revision backup for season {seasonNumber} stage {stageNumber} rev {existing.Revision} already exists. Inspect the 'stage-*.r{existing.Revision}.*' files manually; refusing to overwrite recoverable prior text.");
        }

        File.Copy(paths.ArticlePath, backupArticle);
        File.Copy(paths.MetaPath, backupMeta);
        File.Copy(paths.EvidencePath, backupEvidence);
        return existing;
    }

    internal static MetaFile BuildCommitMeta(
        CommitArchive archive, CommitPaths paths, EvidenceProvenance evidence,
        Guid expectedSaveId, int seasonNumber, int stageNumber,
        byte[] articleBytes, string evidenceSourcePath, string? trimmedNote,
        MetaFile? existingMeta, DateTimeOffset committedUtc, SnapshotInfo? snapshotAtCommit)
    {
        bool conflict = existingMeta is not null &&
            (!string.Equals(existingMeta.DatabaseSha256, evidence.DatabaseSha256, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(existingMeta.StageChecksum, evidence.StageChecksum, StringComparison.Ordinal));
        int revision = existingMeta is null ? 1 : existingMeta.Revision + 1;
        string evidenceHash = ComputeFileSha256Hex(evidenceSourcePath);
        string articleHash = ToHexLower(SHA256.HashData(articleBytes));
        SnapshotAtCommitInfo? commitSnapshot = snapshotAtCommit is null
            ? null
            : new SnapshotAtCommitInfo(snapshotAtCommit.DatabaseSha256, snapshotAtCommit.ExportedUtc, snapshotAtCommit.AcquiredUtc);
        return new MetaFile(
            ArchiveFormatVersion, expectedSaveId, evidence.SaveName, seasonNumber, stageNumber, revision,
            ToArchiveRelative(paths.ArticlePath, archive.SaveDir), ToArchiveRelative(paths.EvidencePath, archive.SaveDir),
            articleHash, evidenceHash, evidence.DatabaseSha256, evidence.StageChecksum,
            evidence.SchemaVersion, evidence.EvidenceType, evidence.GeneratedUtc,
            evidence.ExportedUtc, evidence.AcquiredUtc, evidence.SchemaVersion_Snapshot,
            evidence.RulesVersion, evidence.SavedRulesVersion, evidence.StagesPerSeason,
            evidence.RoundsPerStage, evidence.LeagueSize, committedUtc, conflict,
            existingMeta?.DatabaseSha256, existingMeta?.StageChecksum, trimmedNote, commitSnapshot);
    }

    internal static void PersistCommit(
        CommitArchive archive, CommitPaths paths, MetaFile meta,
        byte[] articleBytes, string evidenceSourcePath, string? trimmedNote)
    {
        WriteBytesAtomically(paths.ArticlePath, articleBytes);
        File.Copy(evidenceSourcePath, paths.EvidencePath + ".new", overwrite: true);
        File.Move(paths.EvidencePath + ".new", paths.EvidencePath, overwrite: true);
        WriteTextAtomically(paths.MetaPath, JsonSerializer.Serialize(meta, JsonOptions));
        IndexFile updated = UpsertIndex(archive.Index, meta.SaveName, meta.CommittedUtc, meta);
        WriteTextAtomically(archive.IndexPath, JsonSerializer.Serialize(updated, JsonOptions));
        if (!string.Equals(archive.SaveFile.SaveName, meta.SaveName, StringComparison.Ordinal))
        {
            WriteTextAtomically(archive.SavePath, JsonSerializer.Serialize(archive.SaveFile with { SaveName = meta.SaveName }, JsonOptions));
        }

        AppendNotebookEntry(paths.SeasonDir, meta.SeasonNumber, meta.StageNumber, meta.Revision, meta, trimmedNote);
    }

    public static MetaFile Commit(
        string archiveRoot,
        Guid expectedSaveId,
        int seasonNumber,
        int stageNumber,
        string articleSourcePath,
        string evidenceSourcePath,
        string? note,
        bool isRevision,
        DateTimeOffset committedUtc,
        SnapshotInfo? snapshotAtCommit)
    {
        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season must be positive.", nameof(seasonNumber));
        }

        if (stageNumber < 1)
        {
            throw new ArgumentException("Stage must be positive.", nameof(stageNumber));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceSourcePath);
        if (expectedSaveId.Equals(Guid.Empty))
        {
            throw new ArgumentException("Save id must not be empty.", nameof(expectedSaveId));
        }

        string? trimmedNote = ValidateCommitNote(note);
        byte[] articleBytes = ReadArticleForCommit(articleSourcePath);
        ValidateArticleText(Encoding.UTF8.GetString(articleBytes), articleSourcePath);
        EvidenceProvenance evidence = ReadEvidenceProvenance(evidenceSourcePath);
        ValidateEvidenceForCommit(evidence, expectedSaveId, seasonNumber, stageNumber, evidenceSourcePath);
        CommitArchive archive = LoadCommitArchive(archiveRoot, expectedSaveId);
        CommitPaths paths = ResolveCommitPaths(archive, seasonNumber, stageNumber);
        MetaFile? existingMeta = HandleCommitRevision(paths, seasonNumber, stageNumber, isRevision);
        MetaFile meta = BuildCommitMeta(
            archive, paths, evidence, expectedSaveId, seasonNumber, stageNumber,
            articleBytes, evidenceSourcePath, trimmedNote, existingMeta, committedUtc, snapshotAtCommit);
        PersistCommit(archive, paths, meta, articleBytes, evidenceSourcePath, trimmedNote);
        return meta;
    }

    internal sealed record ContextArchive(string Root, string SaveDir, SaveFile SaveFile, IndexFile Index);

    internal static void ValidateContextArgs(Guid expectedSaveId, int? cutoffSeason, int? cutoffStage, int recentCount)
    {
        if ((cutoffSeason.HasValue) != (cutoffStage.HasValue))
        {
            throw new ArgumentException("Options '--season' and '--stage' must be supplied together for a historical cutoff.", nameof(cutoffSeason));
        }

        if (cutoffSeason.HasValue && cutoffSeason.Value < 1)
        {
            throw new ArgumentException("Option '--season' must be positive.", nameof(cutoffSeason));
        }

        if (cutoffStage.HasValue && cutoffStage.Value < 1)
        {
            throw new ArgumentException("Option '--stage' must be positive.", nameof(cutoffStage));
        }

        if (recentCount < 1 || recentCount > MaxRecent)
        {
            throw new ArgumentException($"Option '--recent' must be between 1 and {MaxRecent}.", nameof(recentCount));
        }

        if (expectedSaveId.Equals(Guid.Empty))
        {
            throw new ArgumentException("Save id must not be empty.", nameof(expectedSaveId));
        }
    }

    internal static ContextArchive LoadContextArchive(string archiveRoot, Guid expectedSaveId)
    {
        string root = Path.GetFullPath(archiveRoot);
        string saveDir = SaveDirFor(root, expectedSaveId);
        string savePath = Path.Combine(saveDir, "save.json");
        string indexPath = Path.Combine(saveDir, "index.json");
        if (!File.Exists(savePath) || !File.Exists(indexPath))
        {
            throw new InvalidOperationException(
                $"No journalist archive for save '{expectedSaveId:D}' under '{root}'. Run 'archive init' first.");
        }

        SaveFile saveFile = ReadJson<SaveFile>(savePath);
        if (!saveFile.SaveId.Equals(expectedSaveId))
        {
            throw new InvalidOperationException($"Archive save file '{savePath}' belongs to another save. Refusing to mix saves.");
        }

        IndexFile index = ReadJson<IndexFile>(indexPath);
        if (!index.SaveId.Equals(expectedSaveId))
        {
            throw new InvalidOperationException($"Archive index '{indexPath}' belongs to another save. Refusing to mix saves.");
        }

        return new ContextArchive(root, saveDir, saveFile, index);
    }

    internal static List<(int Season, int Stage, IndexStage Entry)> CollectStages(IndexFile index)
    {
        List<(int Season, int Stage, IndexStage Entry)> all = [];
        foreach (IndexSeason season in index.Seasons)
        {
            foreach (IndexStage stage in season.Stages)
            {
                all.Add((season.SeasonNumber, stage.StageNumber, stage));
            }
        }

        all.Sort(static (a, b) => a.Season != b.Season ? a.Season.CompareTo(b.Season) : a.Stage.CompareTo(b.Stage));
        return all;
    }

    internal static List<ContextArticle> ReadRecentArticles(
        ContextArchive archive, List<(int Season, int Stage, IndexStage Entry)> picked)
    {
        List<ContextArticle> recent = [];
        foreach ((int Season, int Stage, IndexStage Entry) in picked)
        {
            string fullArticle = Path.Combine(archive.SaveDir, Entry.ArticlePath.Replace('/', Path.DirectorySeparatorChar));
            EnsureUnderRoot(archive.Root, fullArticle);
            string text = ReadArticleText(fullArticle, Entry.ArticlePath);
            bool truncated = text.Length > MaxArticleBytes;
            string excerpt = truncated ? text[..MaxArticleBytes] : text;
            string hash = ToHexLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            recent.Add(new ContextArticle(Season, Stage, Entry.Revision, Entry.ArticlePath, excerpt, truncated, hash, Entry.StageChecksum, Entry.DatabaseSha256, Entry.GeneratedUtc));
        }

        return recent;
    }

    internal static string ReadArticleText(string fullArticle, string relative)
    {
        try
        {
            return File.ReadAllText(fullArticle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Archived article '{relative}' could not be read.", ex);
        }
    }

    internal sealed record EligibleSelection(List<(int Season, int Stage, IndexStage Entry)> Eligible, int ExcludedLater);

    internal static EligibleSelection SelectEligible(
        List<(int Season, int Stage, IndexStage Entry)> all, int? cutoffSeason, int? cutoffStage)
    {
        List<(int Season, int Stage, IndexStage Entry)> eligible = cutoffSeason.HasValue
            ? all.Where(e => e.Season < cutoffSeason.Value || (e.Season == cutoffSeason.Value && e.Stage <= cutoffStage!.Value)).ToList()
            : [.. all];
        int excludedLater = cutoffSeason.HasValue ? all.Count - eligible.Count : 0;
        return new EligibleSelection(eligible, excludedLater);
    }

    internal sealed record NotebookSection(
        string Relative, string Excerpt, bool Truncated, bool Missing,
        int ExcludedLater, int ExcludedUnscoped, int Season);

    internal static int ResolveNotebookSeason(
        IndexFile index, List<(int Season, int Stage, IndexStage Entry)> eligible, int? cutoffSeason)
    {
        if (cutoffSeason.HasValue)
        {
            return cutoffSeason.Value;
        }

        if (eligible.Count == 0 && index.Seasons.Count > 0)
        {
            return index.Seasons.Max(s => s.SeasonNumber);
        }

        if (eligible.Count == 0)
        {
            return 1;
        }

        return eligible.Max(e => e.Season);
    }

    internal static NotebookSection ReadNotebookSection(
        ContextArchive archive, int notebookSeason, int? cutoffSeason, int? cutoffStage)
    {
        string relative = $"season-{notebookSeason:D2}/notebook.md";
        string full = Path.Combine(archive.SaveDir, $"season-{notebookSeason:D2}", "notebook.md");
        EnsureUnderRoot(archive.Root, full);
        bool missing = !File.Exists(full);
        if (missing)
        {
            return new NotebookSection(relative, string.Empty, false, true, 0, 0, notebookSeason);
        }

        string text = File.ReadAllText(full);
        (string excerpt, bool truncated, int later, int unscoped) = FilterNotebook(text, cutoffSeason, cutoffStage);
        return new NotebookSection(relative, excerpt, truncated, false, later, unscoped, notebookSeason);
    }

    internal static (List<int> Gaps, List<string> Disclosures) BuildGapsAndDisclosures(
        List<(int Season, int Stage, IndexStage Entry)> eligible,
        int? cutoffSeason, int? cutoffStage, int excludedLater, int excludedUnscoped,
        bool notebookMissing, int notebookSeason)
    {
        List<int> gaps = [];
        List<string> disclosures = [];
        if (eligible.Count == 0)
        {
            disclosures.Add(cutoffSeason.HasValue
                ? $"No archived reviews at or before season {cutoffSeason.Value} stage {cutoffStage!.Value}; this historical request has no earlier coverage in the archive."
                : "No archived reviews yet; this is the first review of the save in the archive.");
        }
        else if (cutoffSeason.HasValue)
        {
            AddHistoricalGaps(eligible, cutoffSeason.Value, cutoffStage!.Value, gaps, disclosures);
            if (excludedLater > 0)
            {
                disclosures.Add($"{excludedLater} later review(s) after season {cutoffSeason.Value} stage {cutoffStage.Value} were excluded so the historical request cannot reveal later outcomes.");
            }

            if (excludedUnscoped > 0)
            {
                disclosures.Add($"{excludedUnscoped} unscoped notebook section(s) were excluded from the historical context; only dated Season/Stage entries at or before the cutoff are shown.");
            }
        }
        else
        {
            AddLatestGaps(eligible, gaps, disclosures);
        }

        if (notebookMissing)
        {
            disclosures.Add($"Season {notebookSeason} notebook is absent; no continuity notes exist yet for that season.");
        }

        return (gaps, disclosures);
    }

    internal static void AddHistoricalGaps(
        List<(int Season, int Stage, IndexStage Entry)> eligible,
        int cutoffSeason, int cutoffStage, List<int> gaps, List<string> disclosures)
    {
        List<int> committed = eligible.Where(e => e.Season == cutoffSeason).Select(e => e.Stage).OrderBy(e => e).ToList();
        if (committed.Count == 0)
        {
            disclosures.Add($"No archived reviews for season {cutoffSeason} at or before stage {cutoffStage}; coverage below comes from earlier seasons only.");
            return;
        }

        int upper = Math.Max(committed.Max(), cutoffStage);
        for (int s = 1; s <= upper; s++)
        {
            if (s <= cutoffStage && !committed.Contains(s))
            {
                gaps.Add(s);
            }
        }

        if (gaps.Count > 0)
        {
            disclosures.Add($"Missed stages in season {cutoffSeason} at or before the cutoff are not auto-created: {string.Join(", ", gaps)}. The review covers season {cutoffSeason} stage {cutoffStage}; gaps stay disclosed, not filled.");
        }
    }

    internal static void AddLatestGaps(
        List<(int Season, int Stage, IndexStage Entry)> eligible, List<int> gaps, List<string> disclosures)
    {
        int latestSeason = eligible.Max(e => e.Season);
        List<int> committed = eligible.Where(e => e.Season == latestSeason).Select(e => e.Stage).OrderBy(e => e).ToList();
        int maxLatest = committed.Max();
        for (int s = 1; s <= maxLatest; s++)
        {
            if (!committed.Contains(s))
            {
                gaps.Add(s);
            }
        }

        if (gaps.Count > 0)
        {
            disclosures.Add($"Season {latestSeason} has gaps that stay disclosed, not auto-filled: missing {string.Join(", ", gaps)}.");
        }
    }

    public static ContextResult BuildContext(
        string archiveRoot,
        Guid expectedSaveId,
        int? cutoffSeason,
        int? cutoffStage,
        int recentCount)
    {
        ValidateContextArgs(expectedSaveId, cutoffSeason, cutoffStage, recentCount);
        ContextArchive archive = LoadContextArchive(archiveRoot, expectedSaveId);
        List<(int Season, int Stage, IndexStage Entry)> all = CollectStages(archive.Index);
        EligibleSelection selection = SelectEligible(all, cutoffSeason, cutoffStage);
        List<(int Season, int Stage, IndexStage Entry)> eligible = selection.Eligible;
        int excludedLater = selection.ExcludedLater;
        List<(int Season, int Stage, IndexStage Entry)> picked = eligible.TakeLast(recentCount).ToList();

        List<ContextArticle> recent = ReadRecentArticles(archive, picked);
        int notebookSeason = ResolveNotebookSeason(archive.Index, eligible, cutoffSeason);
        NotebookSection notebook = ReadNotebookSection(archive, notebookSeason, cutoffSeason, cutoffStage);
        (List<int> gaps, List<string> disclosures) = BuildGapsAndDisclosures(
            eligible, cutoffSeason, cutoffStage, excludedLater, notebook.ExcludedUnscoped,
            notebook.Missing, notebook.Season);

        return new ContextResult(
            expectedSaveId, archive.SaveFile.SaveName, cutoffSeason, cutoffStage, recent,
            notebook.Relative, notebook.Excerpt, notebook.Truncated, notebook.Missing,
            excludedLater, notebook.ExcludedLater, notebook.ExcludedUnscoped, gaps, disclosures);
    }

    private static string await_ReadAllText(string path) => File.ReadAllText(path);

    internal sealed record NotebookParsed(
        List<(string Header, List<string> Body, int? Season, int? Stage)> Entries,
        List<string>? Preamble);

    internal static NotebookParsed ParseNotebookEntries(List<string> lines)
    {
        List<(string Header, List<string> Body, int? Season, int? Stage)> entries = [];
        List<string>? preamble = null;
        (string Header, List<string> Body, int? Season, int? Stage)? current = null;
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (current.HasValue)
                {
                    entries.Add(current.Value);
                }

                current = ParseNotebookHeader(line);
            }
            else if (current.HasValue)
            {
                current.Value.Body.Add(line);
            }
            else
            {
                preamble ??= [];
                preamble.Add(line);
            }
        }

        if (current.HasValue)
        {
            entries.Add(current.Value);
        }

        return new NotebookParsed(entries, preamble);
    }

    internal static (string Header, List<string> Body, int? Season, int? Stage) ParseNotebookHeader(string line)
    {
        string header = line[3..].Trim();
        bool hasSeason = TryParseSeasonStage(header, out int seasonParsed, out int stageParsed);
        return hasSeason ? (header, [], seasonParsed, stageParsed) : (header, [], null, null);
    }

    internal static (List<string> Kept, int ExcludedLater, int ExcludedUnscoped) SelectNotebookEntries(
        NotebookParsed parsed, int? cutoffSeason, int? cutoffStage)
    {
        List<string> kept = [];
        if (parsed.Preamble is not null)
        {
            kept.AddRange(parsed.Preamble);
        }

        int excludedLater = 0;
        int excludedUnscoped = 0;
        foreach ((string Header, List<string> Body, int? Season, int? Stage) in parsed.Entries)
        {
            if (Season is null || Stage is null)
            {
                excludedUnscoped++;
                continue;
            }

            bool atOrBefore = Season.Value < cutoffSeason!.Value || (Season.Value == cutoffSeason.Value && Stage.Value <= cutoffStage!.Value);
            if (!atOrBefore)
            {
                excludedLater++;
                continue;
            }

            kept.Add("## " + Header);
            kept.AddRange(Body);
        }

        return (kept, excludedLater, excludedUnscoped);
    }

    private static (string Excerpt, bool Truncated, int ExcludedLater, int ExcludedUnscoped) FilterNotebook(
        string notebookText, int? cutoffSeason, int? cutoffStage)
    {
        if (!cutoffSeason.HasValue)
        {
            bool allTruncated = notebookText.Length > NotebookExcerptMaxChars;
            return (allTruncated ? notebookText[..NotebookExcerptMaxChars] : notebookText, allTruncated, 0, 0);
        }

        List<string> lines = notebookText.Split('\n').ToList();
        NotebookParsed parsed = ParseNotebookEntries(lines);
        (List<string> kept, int later, int unscoped) = SelectNotebookEntries(parsed, cutoffSeason, cutoffStage);
        string result = string.Join("\n", kept);
        bool isTruncated = result.Length > NotebookExcerptMaxChars;
        return (isTruncated ? result[..NotebookExcerptMaxChars] : result, isTruncated, later, unscoped);
    }

    internal static bool TryParseSeasonStage(string header, out int season, out int stage)
    {
        season = 0;
        stage = 0;
        int seasonIndex = header.IndexOf("Season", StringComparison.OrdinalIgnoreCase);
        if (seasonIndex < 0)
        {
            return false;
        }

        int stageIndex = header.IndexOf("Stage", StringComparison.OrdinalIgnoreCase);
        if (stageIndex < 0 || stageIndex < seasonIndex)
        {
            return false;
        }

        if (!TryParseFirstInt(header[(seasonIndex + "Season".Length)..stageIndex], out season) || season < 1)
        {
            return false;
        }

        if (!TryParseFirstInt(header[(stageIndex + "Stage".Length)..], out stage) || stage < 1)
        {
            return false;
        }

        return true;
    }

    private static bool TryParseFirstInt(string text, out int value)
    {
        value = 0;
        int i = 0;
        while (i < text.Length && !char.IsDigit(text[i]))
        {
            i++;
        }

        int start = i;
        while (i < text.Length && char.IsDigit(text[i]))
        {
            i++;
        }

        if (start >= text.Length || i == start)
        {
            return false;
        }

        return int.TryParse(text[start..i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    internal sealed record EvidenceProvenance(
        int SchemaVersion,
        string EvidenceType,
        string Outcome,
        DateTimeOffset GeneratedUtc,
        Guid SaveId,
        string SaveName,
        DateTimeOffset ExportedUtc,
        DateTimeOffset AcquiredUtc,
        string DatabaseSha256,
        int SchemaVersion_Snapshot,
        int RulesVersion,
        string RngAlgorithm,
        int RngVersion,
        int SavedRulesVersion,
        int StagesPerSeason,
        int RoundsPerStage,
        int LeagueSize,
        int SeasonNumber,
        int StageNumber,
        string StageChecksum);

    private static EvidenceProvenance ReadEvidenceProvenance(string evidencePath)
    {
        string json;
        try
        {
            json = File.ReadAllText(evidencePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Evidence file could not be read: '{evidencePath}'.", ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Evidence file '{evidencePath}' is not valid JSON; save the 'evidence' command stdout verbatim.", ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            int schemaVersion = GetInt(root, "schemaVersion");
            string evidenceType = GetString(root, "evidenceType");
            string outcome = GetString(root, "outcome");
            DateTimeOffset generatedUtc = GetDate(root, "generatedUtc");
            JsonElement source = GetObject(root, "source");
            JsonElement target = GetObject(root, "target");
            Guid saveId = GetGuid(source, "saveId");
            string saveName = GetString(source, "saveName");
            DateTimeOffset exportedUtc = GetDate(source, "exportedUtc");
            DateTimeOffset acquiredUtc = GetDate(source, "acquiredUtc");
            string databaseSha256 = GetString(source, "databaseSha256");
            int snapshotSchema = GetInt(source, "schemaVersion");
            int rulesVersion = GetInt(source, "rulesVersion");
            string rngAlgorithm = GetString(source, "rngAlgorithm");
            int rngVersion = GetInt(source, "rngVersion");
            int savedRulesVersion = GetInt(source, "savedRulesVersion");
            int stagesPerSeason = GetInt(source, "stagesPerSeason");
            int roundsPerStage = GetInt(source, "roundsPerStage");
            int leagueSize = GetInt(source, "leagueSize");
            int seasonNumber = GetInt(target, "seasonNumber");
            int stageNumber = GetInt(target, "stageNumber");
            string stageChecksum = GetString(target, "stageChecksum");
            if (string.IsNullOrWhiteSpace(databaseSha256) || string.IsNullOrWhiteSpace(stageChecksum))
            {
                throw new InvalidOperationException($"Evidence file '{evidencePath}' is missing provenance fingerprints.");
            }

            return new EvidenceProvenance(
                schemaVersion, evidenceType, outcome, generatedUtc, saveId, saveName,
                exportedUtc, acquiredUtc, databaseSha256, snapshotSchema, rulesVersion,
                rngAlgorithm, rngVersion, savedRulesVersion, stagesPerSeason, roundsPerStage,
                leagueSize, seasonNumber, stageNumber, stageChecksum);
        }
    }

    private static JsonElement GetObject(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object)
        {
            return value;
        }

        throw new InvalidOperationException($"Evidence is missing the '{name}' object; save the 'evidence' command stdout verbatim.");
    }

    private static string GetString(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        throw new InvalidOperationException($"Evidence is missing text field '{name}'.");
    }

    private static int GetInt(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Evidence is missing integer field '{name}'.");
    }

    private static Guid GetGuid(JsonElement parent, string name)
    {
        string text = GetString(parent, name);
        if (Guid.TryParse(text, out Guid parsed) && !parsed.Equals(Guid.Empty))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Evidence field '{name}' is not a valid save id.");
    }

    private static DateTimeOffset GetDate(JsonElement parent, string name)
    {
        string text = GetString(parent, name);
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException($"Evidence field '{name}' is not a valid timestamp.");
    }

    private static byte[] ReadBoundedFile(string path, int maxBytes, string kind)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{kind} file could not be read: '{path}'.", ex);
        }

        if (!info.Exists)
        {
            throw new InvalidOperationException($"{kind} file was not found: '{path}'.");
        }

        if (info.Length == 0)
        {
            throw new InvalidOperationException($"{kind} file is empty: '{path}'.");
        }

        if (info.Length > maxBytes)
        {
            throw new InvalidOperationException($"{kind} file exceeds the {maxBytes} byte limit: '{path}'. Keep articles compact; evidence stays a single stage.");
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{kind} file could not be read: '{path}'.", ex);
        }
    }

    private static void EnsureIndexExists(string saveDir, Guid saveId, string saveName, DateTimeOffset now)
    {
        string indexPath = Path.Combine(saveDir, "index.json");
        if (File.Exists(indexPath))
        {
            IndexFile existing = ReadJson<IndexFile>(indexPath);
            if (!existing.SaveId.Equals(saveId))
            {
                throw new InvalidOperationException($"Archive index '{indexPath}' belongs to another save. Refusing to mix saves.");
            }

            return;
        }

        IndexFile fresh = new(ArchiveFormatVersion, saveId, saveName.Trim(), now, []);
        WriteTextAtomically(indexPath, JsonSerializer.Serialize(fresh, JsonOptions));
    }

    private static IndexFile UpsertIndex(IndexFile index, string saveName, DateTimeOffset now, MetaFile meta)
    {
        List<IndexSeason> seasons = index.Seasons.ToList();
        int seasonIndex = seasons.FindIndex(s => s.SeasonNumber == meta.SeasonNumber);
        IndexStage entry = new(
            meta.StageNumber, meta.Revision, meta.ArticlePath, meta.EvidencePath,
            ToMetaRelative(meta, index),
            meta.StageChecksum, meta.DatabaseSha256, meta.EvidenceGeneratedUtc, meta.CommittedUtc);
        if (seasonIndex < 0)
        {
            seasons.Add(new IndexSeason(meta.SeasonNumber, [entry]));
        }
        else
        {
            List<IndexStage> stages = seasons[seasonIndex].Stages.ToList();
            int stageIndex = stages.FindIndex(s => s.StageNumber == meta.StageNumber);
            if (stageIndex < 0)
            {
                stages.Add(entry);
            }
            else
            {
                stages[stageIndex] = entry;
            }

            stages.Sort(static (a, b) => a.StageNumber.CompareTo(b.StageNumber));
            seasons[seasonIndex] = new IndexSeason(meta.SeasonNumber, stages);
        }

        seasons.Sort(static (a, b) => a.SeasonNumber.CompareTo(b.SeasonNumber));
        return index with { SaveName = saveName, UpdatedUtc = now, Seasons = seasons };
    }

    private static string ToMetaRelative(MetaFile meta, IndexFile index)
    {
        _ = index;
        string article = meta.ArticlePath.Replace('/', Path.DirectorySeparatorChar);
        string directory = Path.GetDirectoryName(article) ?? string.Empty;
        string file = Path.GetFileNameWithoutExtension(article);
        return (string.IsNullOrEmpty(directory) ? file : directory + "/" + file).Replace(Path.DirectorySeparatorChar, '/') + ".meta.json";
    }

    private static void AppendNotebookEntry(
        string seasonDir, int seasonNumber, int stageNumber, int revision, MetaFile meta, string? note)
    {
        string notebookPath = Path.Combine(seasonDir, "notebook.md");
        if (!File.Exists(notebookPath))
        {
            string header =
                $"# Season {seasonNumber} notebook — Superleague stage reviews\n\n" +
                $"Save `{meta.SaveId:D}` ({meta.SaveName}). Helper appends one dated Season/Stage entry per archived review; " +
                $"human notes anywhere are preserved because the helper only appends. Unscoped sections are excluded from historical context.\n\n";
            WriteTextAtomically(notebookPath, header);
        }

        StringBuilder builder = new();
        builder.Append("## Season ").Append(seasonNumber).Append(" Stage ").Append(stageNumber)
            .Append(" — ").Append(meta.CommittedUtc.ToString("O")).Append(" (rev ").Append(revision).AppendLine(")");
        builder.AppendLine();
        builder.Append("- Cutoff: season ").Append(seasonNumber).Append(" stage ").Append(stageNumber)
            .Append("; evidence `").Append(meta.EvidenceType).Append("` v").Append(meta.EvidenceSchemaVersion)
            .Append("; stageChecksum `").Append(meta.StageChecksum).Append("`; databaseSha256 `").Append(meta.DatabaseSha256)
            .Append("`; generated `").Append(meta.EvidenceGeneratedUtc.ToString("O")).Append("`; snapshot exported `")
            .Append(meta.SnapshotExportedUtc.ToString("O")).Append("` acquired `").Append(meta.SnapshotAcquiredUtc.ToString("O")).AppendLine("`.");
        builder.Append("- Article: `").Append(meta.ArticlePath).Append("` (rev ").Append(revision)
            .Append(", sha `").Append(meta.ArticleSha256).Append("`); evidence: `").Append(meta.EvidencePath)
            .Append("` (sha `").Append(meta.EvidenceSha256).AppendLine("`).");
        if (meta.Conflict)
        {
            builder.Append("- Provenance conflict: this revision supersedes a different timeline (prior databaseSha256 `")
                .Append(meta.PriorDatabaseSha256).Append("`, prior stageChecksum `").Append(meta.PriorStageChecksum)
                .AppendLine("`); earlier text stays in the `stage-*.r<N>.*` backups.");
        }

        if (meta.SnapshotAtCommit is not null)
        {
            bool fresh = string.Equals(meta.SnapshotAtCommit.DatabaseSha256, meta.DatabaseSha256, StringComparison.OrdinalIgnoreCase);
            builder.Append("- Freshness at commit: working snapshot `").Append(meta.SnapshotAtCommit.DatabaseSha256)
                .Append("` (exported `").Append(meta.SnapshotAtCommit.ExportedUtc.ToString("O")).Append("`) ")
                .Append(fresh ? "matches" : "DIFFERS FROM").AppendLine(" the evidence snapshot; the article keeps the evidence provenance above.");
        }
        else
        {
            builder.AppendLine("- Freshness at commit: no working snapshot was present; the article keeps the evidence provenance above.");
        }

        builder.AppendLine("- Note:");
        if (trimmedOrNull(note) is string noteText)
        {
            foreach (string line in noteText.Split('\n'))
            {
                string cleaned = line.TrimEnd('\r');
                builder.Append("> ").AppendLine(string.IsNullOrWhiteSpace(cleaned) ? string.Empty : cleaned.TrimEnd());
            }
        }
        else
        {
            builder.AppendLine("> _(provenance only — no editorial note supplied)_");
        }

        builder.AppendLine();
        File.AppendAllText(notebookPath, builder.ToString());
    }

    private static string? trimmedOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ToArchiveRelative(string fullPath, string saveDir)
    {
        string relative = Path.GetRelativePath(saveDir, fullPath);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static void EnsureUnderRoot(string root, string candidate)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && !string.Equals(fullCandidate, fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Archive path '{candidate}' escapes the archive root '{root}'.");
        }
    }

    private static T ReadJson<T>(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Archive file could not be read: '{path}'.", ex);
        }

        try
        {
            T? value = JsonSerializer.Deserialize<T>(json, JsonReadOptions);
            return value ?? throw new InvalidOperationException($"Archive file is empty: '{path}'.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Archive file is not valid JSON: '{path}'.", ex);
        }
    }

    private static void WriteTextAtomically(string finalPath, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        string? directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = finalPath + ".new";
        File.WriteAllText(tempPath, text);
        File.Move(tempPath, finalPath, overwrite: true);
    }

    private static void WriteBytesAtomically(string finalPath, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string? directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = finalPath + ".new";
        File.WriteAllBytes(tempPath, bytes);
        File.Move(tempPath, finalPath, overwrite: true);
    }

    private static string ComputeFileSha256Hex(string path)
    {
        return Persistence.Saves.SaveBundle.ComputeFileSha256Hex(path);
    }

    private static string ToHexLower(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
