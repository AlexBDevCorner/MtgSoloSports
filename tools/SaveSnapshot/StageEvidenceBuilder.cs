using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Reports;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Seasons;
using MtgSoloSports.SimulationKernel.TieBreaking;

namespace MtgSoloSports.SaveSnapshot;

/// <summary>
/// Offline Superleague stage evidence over one verified working snapshot (MSS-134).
/// Reads only the validated <c>save.db</c> with <c>Mode=ReadOnly</c> plus
/// <c>query_only</c>; never touches bootstrap, migration, seeding, simulation or
/// RNG state. All sporting mathematics come from the save-owned rules snapshot;
/// stage counts are read from those rules, never hard-coded. Championship
/// before/after standings are accumulated from persisted <c>StageStandings</c>
/// with the pure <see cref="SeasonCalculator"/> accumulation plus deterministic
/// tie-break ordering; exact deterministic ties are flagged rather than broken
/// with a fresh RNG draw, and the final stage of a completed season reuses the
/// persisted official <c>SeasonStandings</c>. Later seasons, later stages, later
/// Cups and current mutable bonuses are excluded by construction: every query
/// filters to seasons/stages at or before the target cutoff.
/// </summary>
public static class StageEvidenceBuilder
{
    public const int EvidenceSchemaVersion = 1;

    public const string EvidenceType = "superleague-stage-evidence";

    private const int RecentFormDepth = 5;

    private const int RecentWinRefs = 5;

    public static async Task<StageEvidenceDocument> BuildAsync(
        string databasePath,
        SnapshotInfo snapshot,
        int? seasonFilter,
        int? stageFilter,
        string? athleteName,
        int? athleteId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.IsNullOrWhiteSpace(athleteName) && athleteId.HasValue)
        {
            throw new ArgumentException("Specify only one of '--athlete' or '--athlete-id'.", nameof(athleteId));
        }

        if (stageFilter.HasValue && !seasonFilter.HasValue)
        {
            throw new ArgumentException("Option '--stage' requires '--season'.", nameof(stageFilter));
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        SnapshotReader.DatabaseView view = SnapshotReader.ReadDatabase(databasePath);
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules = SaveRulesCompatibility.ReadSnapshotRules(view.RulesJson);

        ResolvedTarget target = await ResolveTargetAsync(databasePath, rules, seasonFilter, stageFilter, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> names = await LoadAthleteNamesAsync(databasePath, cancellationToken).ConfigureAwait(false);
        List<StageStandingRow> stageRows = await LoadStageStandingsAsync(databasePath, target, cancellationToken).ConfigureAwait(false);
        ValidateStageRows(stageRows, target, rules);
        List<RoundRow> roundRows = await LoadRoundRowsAsync(databasePath, target, cancellationToken).ConfigureAwait(false);
        List<DecodedRound> rounds = DecodeRounds(roundRows, target, rules);
        string stageChecksum = ComputeStageChecksum(stageRows, names);

        ChampionshipView before = await BuildBeforeAsync(databasePath, target, rules, names, cancellationToken).ConfigureAwait(false);
        ChampionshipView after = await BuildAfterAsync(databasePath, target, rules, names, stageRows, cancellationToken).ConfigureAwait(false);
        HistoryContext history = await BuildHistoryAsync(databasePath, target, rules, names, cancellationToken).ConfigureAwait(false);
        AthleteFocusView? focus = await BuildAthleteFocusAsync(databasePath, target, rules, names, before, after, stageRows, rounds, history, athleteName, athleteId, cancellationToken).ConfigureAwait(false);

        elapsed.Stop();
        return Assemble(
            snapshot, view, rules, target, names, stageRows, rounds, stageChecksum,
            before, after, history, focus, elapsed.ElapsedMilliseconds);
    }

    internal sealed record ResolvedTarget(
        int SeasonId,
        int SeasonNumber,
        bool SeasonIsComplete,
        int LeagueId,
        string LeagueName,
        int StageId,
        int StageNumber,
        int CompletedRounds,
        bool IsFirstStage,
        bool IsFinalStage);

    internal sealed record StageStandingRow(
        int SeasonId,
        int LeagueId,
        int StageId,
        int StageNumber,
        int SaveAthleteId,
        int StageRank,
        int StageScoreThousandths,
        int BaseScoreThousandths,
        int ChampionshipPointsThousandths,
        int RoundWins,
        List<int> RoundPlaceCounts,
        int EarnedBonusThousandths);

    internal sealed record RoundRow(
        int SeasonId,
        int LeagueId,
        int StageId,
        int StageNumber,
        int RoundNumber,
        int RulesVersion,
        string PayloadJson,
        string PayloadChecksum);

    internal sealed record DecodedRound(
        int RoundNumber,
        string PayloadChecksum,
        string Encoding,
        RoundPayloadDocument Document);

    internal sealed record ChampionshipView(
        bool Available,
        string? UnavailableReason,
        int CompletedStages,
        bool IsFinal,
        string Checksum,
        List<ChampionshipEntry> Standings,
        string EvidenceRef);

    internal sealed record ChampionshipEntry(
        int AthleteId,
        string Name,
        int Rank,
        int TotalChampionshipPointsThousandths,
        int TotalStageScoreThousandths,
        int TotalBaseScoreThousandths,
        int StageWins,
        int RoundWins,
        int GapToLeaderThousandths,
        bool DeterministicTie,
        int TieGroupSize,
        List<int> StagePlaceCounts,
        List<int> RoundPlaceCounts);

    private static async Task<ResolvedTarget> ResolveTargetAsync(
        string databasePath,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        int? seasonFilter,
        int? stageFilter,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SnapshotReader.OpenReadOnly(databasePath);
        Dictionary<int, (int SeasonNumber, bool IsComplete)> seasons = await LoadSeasonsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (seasons.Count == 0)
        {
            throw new InvalidOperationException("Snapshot database has no seasons; no completed Superleague stage exists.");
        }

        if (seasonFilter.HasValue)
        {
            return await ResolveExplicitAsync(connection, rules, seasons, seasonFilter.Value, stageFilter, cancellationToken).ConfigureAwait(false);
        }

        return await ResolveLatestAsync(connection, rules, seasons, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ResolvedTarget> ResolveLatestAsync(
        SqliteConnection connection,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        Dictionary<int, (int SeasonNumber, bool IsComplete)> seasons,
        CancellationToken cancellationToken)
    {
        Dictionary<int, int> seasonById = seasons.ToDictionary(e => e.Key, e => e.Value.SeasonNumber);
        List<(int SeasonId, int SeasonNumber, int LeagueId, string LeagueName, int StageId, int StageNumber)> completed = await ListCompletedSuperleagueStagesAsync(connection, cancellationToken).ConfigureAwait(false);
        if (completed.Count == 0)
        {
            throw new InvalidOperationException("No completed Superleague stage exists in this snapshot (Season 1 has no Superleague and no later Superleague stage has completed).");
        }

        var latest = completed
            .OrderByDescending(e => e.SeasonNumber)
            .ThenByDescending(e => e.StageNumber)
            .First();
        ResolvedTarget target = await LoadTargetDetailsAsync(connection, rules, latest.SeasonId, latest.SeasonNumber, latest.LeagueId, latest.StageNumber, cancellationToken).ConfigureAwait(false);
        await EnsureDurableAsync(connection, rules, target, allowPartial: false, cancellationToken).ConfigureAwait(false);
        return target;
    }

    private static async Task<ResolvedTarget> ResolveExplicitAsync(
        SqliteConnection connection,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        Dictionary<int, (int SeasonNumber, bool IsComplete)> seasons,
        int seasonNumber,
        int? stageNumber,
        CancellationToken cancellationToken)
    {
        if (seasonNumber < 1)
        {
            throw new ArgumentException("Option '--season' must be positive.", nameof(seasonNumber));
        }

        if (stageNumber.HasValue && stageNumber.Value < 1)
        {
            throw new ArgumentException("Option '--stage' must be positive.", nameof(stageNumber));
        }

        int seasonId = seasons.SingleOrDefault(e => e.Value.SeasonNumber == seasonNumber).Key;
        if (seasonId == 0)
        {
            throw new InvalidOperationException($"Requested season {seasonNumber} was not found in this snapshot (not-found).");
        }

        int leagueId = await LoadSuperleagueIdAsync(connection, seasonId, cancellationToken).ConfigureAwait(false);
        if (leagueId == 0)
        {
            throw new InvalidOperationException($"Requested season {seasonNumber} has no Superleague league (not-found); Season 1 never has a Superleague.");
        }

        if (!stageNumber.HasValue)
        {
            List<(int SeasonId, int SeasonNumber, int LeagueId, string LeagueName, int StageId, int StageNumber)> completed =
                await ListCompletedSuperleagueStagesAsync(connection, cancellationToken).ConfigureAwait(false);
            var latestInSeason = completed
                .Where(e => e.SeasonNumber == seasonNumber)
                .OrderByDescending(e => e.StageNumber)
                .FirstOrDefault();
            if (latestInSeason == default)
            {
                throw new InvalidOperationException($"Season {seasonNumber} Superleague has no completed stage in this snapshot (not-found).");
            }

            ResolvedTarget seasonLatest = await LoadTargetDetailsAsync(connection, rules, latestInSeason.SeasonId, latestInSeason.SeasonNumber, latestInSeason.LeagueId, latestInSeason.StageNumber, cancellationToken).ConfigureAwait(false);
            await EnsureDurableAsync(connection, rules, seasonLatest, allowPartial: false, cancellationToken).ConfigureAwait(false);
            return seasonLatest;
        }

        if (stageNumber.Value > rules.StagesPerSeason)
        {
            throw new InvalidOperationException($"Requested stage {stageNumber.Value} is outside the saved rules (1..{rules.StagesPerSeason}) for season {seasonNumber} (not-found).");
        }

        ResolvedTarget exact = await LoadTargetDetailsAsync(connection, rules, seasonId, seasonNumber, leagueId, stageNumber.Value, cancellationToken).ConfigureAwait(false);
        await EnsureDurableAsync(connection, rules, exact, allowPartial: false, cancellationToken).ConfigureAwait(false);
        return exact;
    }

    private static async Task<Dictionary<int, (int SeasonNumber, bool IsComplete)>> LoadSeasonsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        Dictionary<int, (int SeasonNumber, bool IsComplete)> seasons = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id, SeasonNumber, IsComplete FROM Seasons ORDER BY SeasonNumber;";
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            seasons[reader.GetInt32(0)] = (reader.GetInt32(1), reader.GetInt64(2) != 0);
        }

        return seasons;
    }

    private static async Task<int> LoadSuperleagueIdAsync(SqliteConnection connection, int seasonId, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Leagues WHERE SeasonId = $season AND Kind = 1 ORDER BY Id LIMIT 1;";
        command.Parameters.AddWithValue("$season", seasonId);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            return 0;
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<List<(int SeasonId, int SeasonNumber, int LeagueId, string LeagueName, int StageId, int StageNumber)>> ListCompletedSuperleagueStagesAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        List<(int SeasonId, int SeasonNumber, int LeagueId, string LeagueName, int StageId, int StageNumber)> rows = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT st.SeasonId, s.SeasonNumber, st.LeagueId, l.Name, st.Id, st.StageNumber " +
            "FROM Stages st JOIN Seasons s ON s.Id = st.SeasonId JOIN Leagues l ON l.Id = st.LeagueId " +
            "WHERE l.Kind = 1 AND st.IsComplete != 0 ORDER BY s.SeasonNumber, st.StageNumber;";
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5)));
        }

        return rows;
    }

    private static async Task<ResolvedTarget> LoadTargetDetailsAsync(
        SqliteConnection connection,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        int seasonId,
        int seasonNumber,
        int leagueId,
        int stageNumber,
        CancellationToken cancellationToken)
    {
        string leagueName;
        using (SqliteCommand leagueCommand = connection.CreateCommand())
        {
            leagueCommand.CommandText = "SELECT Name, Kind FROM Leagues WHERE Id = $league AND SeasonId = $season;";
            leagueCommand.Parameters.AddWithValue("$league", leagueId);
            leagueCommand.Parameters.AddWithValue("$season", seasonId);
            using SqliteDataReader leagueReader = await leagueCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await leagueReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Requested Superleague target season {seasonNumber} stage {stageNumber} was not found (not-found).");
            }

            leagueName = leagueReader.GetString(0);
            if (leagueReader.GetInt32(1) != 1)
            {
                throw new InvalidOperationException($"Requested league '{leagueName}' is not the Superleague (Kind must be 1, resolved from persisted identity, not display names).");
            }
        }

        int stageId = 0;
        int completedRounds = 0;
        bool isComplete = false;
        using (SqliteCommand stageCommand = connection.CreateCommand())
        {
            stageCommand.CommandText = "SELECT Id, CompletedRounds, IsComplete FROM Stages WHERE SeasonId = $season AND LeagueId = $league AND StageNumber = $stage;";
            stageCommand.Parameters.AddWithValue("$season", seasonId);
            stageCommand.Parameters.AddWithValue("$league", leagueId);
            stageCommand.Parameters.AddWithValue("$stage", stageNumber);
            using SqliteDataReader stageReader = await stageCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await stageReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                stageId = stageReader.GetInt32(0);
                completedRounds = stageReader.GetInt32(1);
                isComplete = stageReader.GetInt64(2) != 0;
            }
        }

        if (stageId == 0)
        {
            throw new InvalidOperationException($"Requested Superleague season {seasonNumber} stage {stageNumber} has not been simulated (not-found).");
        }

        if (!isComplete)
        {
            throw new InvalidOperationException($"Requested Superleague season {seasonNumber} stage {stageNumber} is only partially complete ({completedRounds}/{rules.RoundsPerStage} rounds) and cannot be presented as completed (partial-stage).");
        }

        bool seasonComplete = false;
        using (SqliteCommand seasonCommand = connection.CreateCommand())
        {
            seasonCommand.CommandText = "SELECT IsComplete FROM Seasons WHERE Id = $season;";
            seasonCommand.Parameters.AddWithValue("$season", seasonId);
            object? seasonValue = await seasonCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            seasonComplete = seasonValue is not null && !(seasonValue is DBNull) && Convert.ToInt64(seasonValue, CultureInfo.InvariantCulture) != 0;
        }

        return new ResolvedTarget(
            seasonId, seasonNumber, seasonComplete, leagueId, leagueName, stageId, stageNumber,
            completedRounds, stageNumber == 1, stageNumber == rules.StagesPerSeason);
    }

    private static async Task EnsureDurableAsync(
        SqliteConnection connection,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        ResolvedTarget target,
        bool allowPartial,
        CancellationToken cancellationToken)
    {
        _ = allowPartial;
        using (SqliteCommand standingsCommand = connection.CreateCommand())
        {
            standingsCommand.CommandText = "SELECT COUNT(*) FROM StageStandings WHERE SeasonId = $season AND LeagueId = $league AND StageNumber = $stage;";
            standingsCommand.Parameters.AddWithValue("$season", target.SeasonId);
            standingsCommand.Parameters.AddWithValue("$league", target.LeagueId);
            standingsCommand.Parameters.AddWithValue("$stage", target.StageNumber);
            long standings = Convert.ToInt64(await standingsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (standings != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"Superleague season {target.SeasonNumber} stage {target.StageNumber} has {standings} stage standings, expected {rules.LeagueSize}; a partial stage cannot be presented as completed (partial-stage).");
            }
        }

        using (SqliteCommand roundsCommand = connection.CreateCommand())
        {
            roundsCommand.CommandText = "SELECT COUNT(*) FROM Rounds WHERE SeasonId = $season AND LeagueId = $league AND StageNumber = $stage;";
            roundsCommand.Parameters.AddWithValue("$season", target.SeasonId);
            roundsCommand.Parameters.AddWithValue("$league", target.LeagueId);
            roundsCommand.Parameters.AddWithValue("$stage", target.StageNumber);
            long rounds = Convert.ToInt64(await roundsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (rounds != rules.RoundsPerStage)
            {
                throw new InvalidOperationException(
                    $"Superleague season {target.SeasonNumber} stage {target.StageNumber} has {rounds} persisted rounds, expected {rules.RoundsPerStage}; a partial stage cannot be presented as completed (partial-stage).");
            }
        }
    }

    private static async Task<Dictionary<int, string>> LoadAthleteNamesAsync(string databasePath, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SnapshotReader.OpenReadOnly(databasePath);
        Dictionary<int, string> names = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name FROM SaveAthletes;";
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names[reader.GetInt32(0)] = reader.GetString(1);
        }

        if (names.Count == 0)
        {
            throw new InvalidOperationException("Snapshot database has no athletes; sporting state is corrupt.");
        }

        return names;
    }

    private static async Task<List<StageStandingRow>> LoadStageStandingsAsync(string databasePath, ResolvedTarget target, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SnapshotReader.OpenReadOnly(databasePath);
        List<StageStandingRow> rows = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT SeasonId, LeagueId, StageId, StageNumber, SaveAthleteId, StageRank, StageScoreThousandths, BaseScoreThousandths, " +
            "ChampionshipPointsThousandths, RoundWins, RoundPlaceCountsJson, EarnedBonusThousandths " +
            "FROM StageStandings WHERE SeasonId = $season AND LeagueId = $league AND StageNumber = $stage ORDER BY StageRank;";
        command.Parameters.AddWithValue("$season", target.SeasonId);
        command.Parameters.AddWithValue("$league", target.LeagueId);
        command.Parameters.AddWithValue("$stage", target.StageNumber);
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            List<int>? counts = JsonSerializer.Deserialize<List<int>>(reader.GetString(10));
            rows.Add(new StageStandingRow(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.GetInt32(8), reader.GetInt32(9), counts ?? [], reader.GetInt32(11)));
        }

        return rows;
    }

    private static void ValidateStageRows(
        List<StageStandingRow> rows,
        ResolvedTarget target,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules)
    {
        if (rows.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException($"Superleague season {target.SeasonNumber} stage {target.StageNumber} must hold exactly {rules.LeagueSize} standings, was {rows.Count}.");
        }

        HashSet<int> ranks = rows.Select(r => r.StageRank).ToHashSet();
        if (!ranks.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
        {
            throw new InvalidOperationException($"Superleague season {target.SeasonNumber} stage {target.StageNumber} must cover ranks 1..{rules.LeagueSize} exactly once.");
        }

        foreach (StageStandingRow row in rows)
        {
            int expected = rules.ScoringTable[row.StageRank - 1] * MtgSoloSports.SimulationKernel.Rules.RulesV1.FixedScale;
            if (row.ChampionshipPointsThousandths != expected)
            {
                throw new InvalidOperationException($"Stage standing for athlete {row.SaveAthleteId} rank {row.StageRank} must award {expected} championship points, was {row.ChampionshipPointsThousandths}.");
            }

            if (row.RoundPlaceCounts.Count != rules.LeagueSize)
            {
                throw new InvalidOperationException($"Stage standing for athlete {row.SaveAthleteId} must have {rules.LeagueSize} round-place counts, was {row.RoundPlaceCounts.Count}.");
            }

            if (row.RoundPlaceCounts.Sum() != rules.RoundsPerStage)
            {
                throw new InvalidOperationException($"Stage standing for athlete {row.SaveAthleteId} must sum to {rules.RoundsPerStage} round appearances.");
            }
        }
    }

    private static async Task<List<RoundRow>> LoadRoundRowsAsync(string databasePath, ResolvedTarget target, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SnapshotReader.OpenReadOnly(databasePath);
        List<RoundRow> rows = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT SeasonId, LeagueId, StageId, StageNumber, RoundNumber, RulesVersion, PayloadJson, PayloadChecksum " +
            "FROM Rounds WHERE SeasonId = $season AND LeagueId = $league AND StageNumber = $stage ORDER BY RoundNumber;";
        command.Parameters.AddWithValue("$season", target.SeasonId);
        command.Parameters.AddWithValue("$league", target.LeagueId);
        command.Parameters.AddWithValue("$stage", target.StageNumber);
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new RoundRow(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7)));
        }

        return rows;
    }

    private static List<DecodedRound> DecodeRounds(
        List<RoundRow> rows,
        ResolvedTarget target,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules)
    {
        if (rows.Count != rules.RoundsPerStage)
        {
            throw new InvalidOperationException($"Superleague season {target.SeasonNumber} stage {target.StageNumber} must hold exactly {rules.RoundsPerStage} rounds, was {rows.Count}.");
        }

        List<DecodedRound> decoded = new(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            RoundRow row = rows[i];
            if (row.RoundNumber != i + 1)
            {
                throw new InvalidOperationException($"Superleague season {target.SeasonNumber} stage {target.StageNumber} has non-sequential round rows; rounds must advance in order.");
            }

            RoundPayloadDocument document;
            try
            {
                document = RoundPayloadCodec.DecodeRound(row.PayloadJson);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Superleague season {target.SeasonNumber} stage {target.StageNumber} round {row.RoundNumber} has corrupt payload and cannot be replayed: {ex.Message}", ex);
            }

            if (document.StageNumber != target.StageNumber || document.RoundNumber != row.RoundNumber)
            {
                throw new InvalidOperationException($"Persisted round season {target.SeasonNumber} stage {target.StageNumber} round {row.RoundNumber} has corrupt stage/round identity.");
            }

            if (!string.Equals(document.Checksum, row.PayloadChecksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Persisted round {row.RoundNumber} payload checksum mismatch; evidence cannot be presented as verified.");
            }

            if (document.Placements.Count != rules.LeagueSize)
            {
                throw new InvalidOperationException($"Superleague season {target.SeasonNumber} stage {target.StageNumber} round {row.RoundNumber} must contain exactly {rules.LeagueSize} placements, was {document.Placements.Count}.");
            }

            HashSet<int> positions = document.Placements.Select(p => p.Position).ToHashSet();
            if (!positions.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
            {
                throw new InvalidOperationException($"Superleague season {target.SeasonNumber} stage {target.StageNumber} round {row.RoundNumber} has corrupt finishing positions.");
            }

            foreach (RoundPayloadEntry placement in document.Placements)
            {
                int expectedBase = rules.ScoringTable[placement.Position - 1] * MtgSoloSports.SimulationKernel.Rules.RulesV1.FixedScale;
                if (placement.BaseThousandths != expectedBase)
                {
                    throw new InvalidOperationException($"Round {row.RoundNumber} base points for position {placement.Position} must be {expectedBase}, was {placement.BaseThousandths}.");
                }
            }

            string encoding = row.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal) ? "br1" : "plain";
            decoded.Add(new DecodedRound(row.RoundNumber, row.PayloadChecksum, encoding, document));
        }

        return decoded;
    }

    private static string ComputeStageChecksum(List<StageStandingRow> rows, Dictionary<int, string> names)
    {
        List<MtgSoloSports.SimulationKernel.Stages.StageRankedAthlete> ranked = new(rows.Count);
        foreach (StageStandingRow row in rows.OrderBy(r => r.StageRank))
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            ranked.Add(new MtgSoloSports.SimulationKernel.Stages.StageRankedAthlete(
                row.SaveAthleteId, name ?? $"Athlete {row.SaveAthleteId}", row.StageRank,
                row.StageScoreThousandths, row.BaseScoreThousandths, row.ChampionshipPointsThousandths,
                row.RoundWins, row.RoundPlaceCounts, row.EarnedBonusThousandths));
        }

        return StageInvariants.ComputeChecksum(ranked);
    }

    private static async Task<ChampionshipView> BuildBeforeAsync(
        string databasePath,
        ResolvedTarget target,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        if (target.StageNumber == 1)
        {
            return new ChampionshipView(
                false, "First stage of the season has no prior championship standings; all totals start at zero.",
                0, false, string.Empty, [], $"SeasonStandings:season={target.SeasonNumber}:stages=0");
        }

        List<List<SeasonStageEntry>> groups = await LoadSeasonGroupsAsync(databasePath, target, 1, target.StageNumber - 1, rules, names, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SeasonAthleteTotals> totals = SeasonCalculator.Accumulate(groups, rules);
        List<ChampionshipEntry> standings = RankProvisional(totals, rules.LeagueSize);
        string checksum = SeasonCalculator.ComputeChecksum(MapToRanked(standings));
        return new ChampionshipView(true, null, target.StageNumber - 1, false, checksum, standings, $"SeasonStandings:season={target.SeasonNumber}:stages=1-{target.StageNumber - 1}:provisional");
    }

    private static async Task<ChampionshipView> BuildAfterAsync(
        string databasePath,
        ResolvedTarget target,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        Dictionary<int, string> names,
        List<StageStandingRow> stageRows,
        CancellationToken cancellationToken)
    {
        if (target.SeasonIsComplete && target.IsFinalStage)
        {
            List<ChampionshipEntry> persisted = await LoadPersistedFinalAsync(databasePath, target, names, cancellationToken).ConfigureAwait(false);
            string finalChecksum = SeasonCalculator.ComputeChecksum(MapToRanked(persisted));
            return new ChampionshipView(true, null, target.StageNumber, true, finalChecksum, persisted, $"SeasonStandings:season={target.SeasonNumber}:final");
        }

        List<List<SeasonStageEntry>> groups = await LoadSeasonGroupsAsync(databasePath, target, 1, target.StageNumber, rules, names, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SeasonAthleteTotals> totals = SeasonCalculator.Accumulate(groups, rules);
        List<ChampionshipEntry> standings = RankProvisional(totals, rules.LeagueSize);
        string checksum = SeasonCalculator.ComputeChecksum(MapToRanked(standings));
        return new ChampionshipView(true, null, target.StageNumber, false, checksum, standings, $"SeasonStandings:season={target.SeasonNumber}:stages=1-{target.StageNumber}:provisional");
    }

    private static async Task<List<List<SeasonStageEntry>>> LoadSeasonGroupsAsync(
        string databasePath,
        ResolvedTarget target,
        int fromStage,
        int toStage,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SnapshotReader.OpenReadOnly(databasePath);
        Dictionary<int, List<StageStandingRow>> byStage = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT StageNumber, SaveAthleteId, StageRank, ChampionshipPointsThousandths, StageScoreThousandths, BaseScoreThousandths, RoundPlaceCountsJson " +
                "FROM StageStandings WHERE SeasonId = $season AND LeagueId = $league AND StageNumber >= $from AND StageNumber <= $to ORDER BY StageNumber, StageRank;";
            command.Parameters.AddWithValue("$season", target.SeasonId);
            command.Parameters.AddWithValue("$league", target.LeagueId);
            command.Parameters.AddWithValue("$from", fromStage);
            command.Parameters.AddWithValue("$to", toStage);
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int stageNumber = reader.GetInt32(0);
                List<int>? counts = JsonSerializer.Deserialize<List<int>>(reader.GetString(6));
                StageStandingRow row = new(target.SeasonId, target.LeagueId, target.StageId, stageNumber,
                    reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(4), reader.GetInt32(5),
                    reader.GetInt32(3), 0, counts ?? [], 0);
                if (!byStage.TryGetValue(stageNumber, out List<StageStandingRow>? list))
                {
                    list = [];
                    byStage[stageNumber] = list;
                }

                list.Add(row);
            }
        }

        if (byStage.Count != toStage - fromStage + 1)
        {
            throw new InvalidOperationException($"Superleague season {target.SeasonNumber} is missing persisted stage standings for stages {fromStage}..{toStage}; championship context cannot be reconstructed.");
        }

        List<List<SeasonStageEntry>> groups = new(byStage.Count);
        foreach (int stageNumber in Enumerable.Range(fromStage, toStage - fromStage + 1))
        {
            if (!byStage.TryGetValue(stageNumber, out List<StageStandingRow>? stageRows) || stageRows.Count != rules.LeagueSize)
            {
                throw new InvalidOperationException($"Superleague season {target.SeasonNumber} stage {stageNumber} must hold exactly {rules.LeagueSize} standings for championship context.");
            }

            List<SeasonStageEntry> entries = new(stageRows.Count);
            foreach (StageStandingRow row in stageRows)
            {
                if (!names.TryGetValue(row.SaveAthleteId, out string? name))
                {
                    throw new InvalidOperationException($"Superleague season {target.SeasonNumber} stage {stageNumber} references unknown athlete {row.SaveAthleteId}.");
                }

                entries.Add(new SeasonStageEntry(
                    row.SaveAthleteId, name, row.StageRank, row.ChampionshipPointsThousandths,
                    row.StageScoreThousandths, row.BaseScoreThousandths, row.RoundPlaceCounts));
            }

            groups.Add(entries);
        }

        return groups;
    }

    private static async Task<List<ChampionshipEntry>> LoadPersistedFinalAsync(
        string databasePath,
        ResolvedTarget target,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SnapshotReader.OpenReadOnly(databasePath);
        List<(int AthleteId, int Rank, int Championship, int StageScore, int Base, int StageWins, int RoundWins, List<int> StageCounts, List<int> RoundCounts)> rows = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT SaveAthleteId, SeasonRank, TotalChampionshipPointsThousandths, TotalStageScoreThousandths, TotalBaseScoreThousandths, " +
                "StageWins, RoundWins, StagePlaceCountsJson, RoundPlaceCountsJson " +
                "FROM SeasonStandings WHERE SeasonId = $season AND LeagueId = $league ORDER BY SeasonRank;";
            command.Parameters.AddWithValue("$season", target.SeasonId);
            command.Parameters.AddWithValue("$league", target.LeagueId);
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                List<int>? stageCounts = JsonSerializer.Deserialize<List<int>>(reader.GetString(7));
                List<int>? roundCounts = JsonSerializer.Deserialize<List<int>>(reader.GetString(8));
                rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                    reader.GetInt32(5), reader.GetInt32(6), stageCounts ?? [], roundCounts ?? []));
            }
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"Superleague season {target.SeasonNumber} is marked complete but has no persisted final standings.");
        }

        List<ChampionshipEntry> standings = new(rows.Count);
        int leader = rows.OrderBy(r => r.Rank).First().Championship;
        Dictionary<string, int> tieSizes = ComputeSeasonTieSizes(rows.Select(r => (r.Championship, (IReadOnlyList<int>)r.StageCounts, (IReadOnlyList<int>)r.RoundCounts, r.StageScore, r.Base)).ToList());
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows.OrderBy(r => r.Rank).ElementAt(i);
            names.TryGetValue(row.AthleteId, out string? name);
            string key = SeasonTieKey(row.Championship, row.StageCounts, row.RoundCounts, row.StageScore, row.Base);
            standings.Add(new ChampionshipEntry(
                row.AthleteId, name ?? $"Athlete {row.AthleteId}", row.Rank,
                row.Championship, row.StageScore, row.Base, row.StageWins, row.RoundWins,
                checked(leader - row.Championship), tieSizes[key] > 1, tieSizes[key],
                row.StageCounts, row.RoundCounts));
        }

        return standings;
    }

    internal static List<ChampionshipEntry> RankProvisional(IReadOnlyList<SeasonAthleteTotals> totals, int leagueSize)
    {
        ArgumentNullException.ThrowIfNull(totals);
        if (totals.Count != leagueSize)
        {
            throw new InvalidOperationException($"Season accumulation requires exactly {leagueSize} athletes, was {totals.Count}.");
        }

        List<SeasonAthleteTotals> byChampionship = [.. totals];
        byChampionship.Sort(static (left, right) =>
        {
            int championship = right.TotalChampionshipPointsThousandths.CompareTo(left.TotalChampionshipPointsThousandths);
            return championship != 0 ? championship : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });

        Dictionary<string, int> tieSizes = ComputeSeasonTieSizes(byChampionship
            .Select(t => (t.TotalChampionshipPointsThousandths, t.StagePlaceCounts, t.RoundPlaceCounts, t.TotalStageScoreThousandths, t.TotalBaseScoreThousandths))
            .ToList());

        List<SeasonAthleteTotals> ordered = new(byChampionship.Count);
        int index = 0;
        while (index < byChampionship.Count)
        {
            int runEnd = index + 1;
            while (runEnd < byChampionship.Count &&
                byChampionship[runEnd].TotalChampionshipPointsThousandths == byChampionship[index].TotalChampionshipPointsThousandths)
            {
                runEnd++;
            }

            List<SeasonAthleteTotals> group = byChampionship.GetRange(index, runEnd - index);
            group.Sort(CompareSeasonDeterministic);
            ordered.AddRange(group);
            index = runEnd;
        }

        int leader = ordered[0].TotalChampionshipPointsThousandths;
        List<ChampionshipEntry> standings = new(ordered.Count);
        for (int rank = 0; rank < ordered.Count; rank++)
        {
            SeasonAthleteTotals entry = ordered[rank];
            string key = SeasonTieKey(entry.TotalChampionshipPointsThousandths, entry.StagePlaceCounts, entry.RoundPlaceCounts, entry.TotalStageScoreThousandths, entry.TotalBaseScoreThousandths);
            standings.Add(new ChampionshipEntry(
                entry.AthleteId, entry.Name, rank + 1,
                entry.TotalChampionshipPointsThousandths, entry.TotalStageScoreThousandths, entry.TotalBaseScoreThousandths,
                entry.StageWins, entry.RoundWins, checked(leader - entry.TotalChampionshipPointsThousandths),
                tieSizes[key] > 1, tieSizes[key],
                [.. entry.StagePlaceCounts], [.. entry.RoundPlaceCounts]));
        }

        return standings;
    }

    private static int CompareSeasonDeterministic(SeasonAthleteTotals left, SeasonAthleteTotals right)
    {
        DeterministicTieBreakKey leftKey = new(left.StagePlaceCounts, left.RoundPlaceCounts, EncodeRawTotals(left.TotalStageScoreThousandths, left.TotalBaseScoreThousandths));
        DeterministicTieBreakKey rightKey = new(right.StagePlaceCounts, right.RoundPlaceCounts, EncodeRawTotals(right.TotalStageScoreThousandths, right.TotalBaseScoreThousandths));
        int deterministic = TieBreaker.CompareDeterministic(leftKey, rightKey);
        return deterministic != 0 ? deterministic : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
    }

    internal static long EncodeRawTotals(int stageScoreThousandths, int baseScoreThousandths)
    {
        // Same packing as SeasonCalculator.EncodeRawTotals: stage score decides
        // before base score in one ordered 64-bit key.
        checked
        {
            return (((long)stageScoreThousandths) << 32) | (uint)baseScoreThousandths;
        }
    }

    private static Dictionary<string, int> ComputeSeasonTieSizes(List<(int Championship, IReadOnlyList<int> StageCounts, IReadOnlyList<int> RoundCounts, int StageScore, int Base)> keys)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            string flat = SeasonTieKey(key.Championship, key.StageCounts, key.RoundCounts, key.StageScore, key.Base);
            counts[flat] = counts.TryGetValue(flat, out int existing) ? existing + 1 : 1;
        }

        return counts;
    }

    private static string SeasonTieKey(int championship, IReadOnlyList<int> stageCounts, IReadOnlyList<int> roundCounts, int stageScore, int @base)
    {
        return championship.ToString(CultureInfo.InvariantCulture) + "|" +
            string.Join(",", stageCounts) + "|" + string.Join(",", roundCounts) + "|" +
            stageScore.ToString(CultureInfo.InvariantCulture) + ":" + @base.ToString(CultureInfo.InvariantCulture);
    }

    private static List<SeasonRankedAthlete> MapToRanked(List<ChampionshipEntry> standings)
    {
        return standings
            .OrderBy(s => s.Rank)
            .Select(s => new SeasonRankedAthlete(
                s.AthleteId, s.Name, s.Rank, s.TotalChampionshipPointsThousandths,
                s.TotalStageScoreThousandths, s.TotalBaseScoreThousandths,
                s.StageWins, s.RoundWins, s.StagePlaceCounts, s.RoundPlaceCounts, s.Rank == 1))
            .ToList();
    }

    private static async Task<HistoryContext> BuildHistoryAsync(
        string databasePath,
        ResolvedTarget target,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SnapshotReader.OpenReadOnly(databasePath);
        List<StageWinnerRef> stageWinners = [];
        using (SqliteCommand winnersCommand = connection.CreateCommand())
        {
            winnersCommand.CommandText =
                "SELECT StageNumber, SaveAthleteId, ChampionshipPointsThousandths FROM StageStandings " +
                "WHERE SeasonId = $season AND LeagueId = $league AND StageRank = 1 AND StageNumber <= $stage ORDER BY StageNumber;";
            winnersCommand.Parameters.AddWithValue("$season", target.SeasonId);
            winnersCommand.Parameters.AddWithValue("$league", target.LeagueId);
            winnersCommand.Parameters.AddWithValue("$stage", target.StageNumber);
            using SqliteDataReader reader = await winnersCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int stageNumber = reader.GetInt32(0);
                int athleteId = reader.GetInt32(1);
                names.TryGetValue(athleteId, out string? name);
                stageWinners.Add(new StageWinnerRef(stageNumber, athleteId, name ?? $"Athlete {athleteId}", reader.GetInt32(2)));
            }
        }

        if (stageWinners.Count != target.StageNumber)
        {
            throw new InvalidOperationException($"Superleague season {target.SeasonNumber} should have {target.StageNumber} stage winners at or before the cutoff, was {stageWinners.Count}.");
        }

        List<SeasonChampionRef> priorChampions = [];
        using (SqliteCommand championsCommand = connection.CreateCommand())
        {
            championsCommand.CommandText =
                "SELECT s.SeasonNumber, ss.SaveAthleteId, ss.TotalChampionshipPointsThousandths " +
                "FROM SeasonStandings ss JOIN Seasons s ON s.Id = ss.SeasonId JOIN Leagues l ON l.Id = ss.LeagueId " +
                "WHERE l.Kind = 1 AND ss.IsChampion != 0 AND s.SeasonNumber < $season ORDER BY s.SeasonNumber;";
            championsCommand.Parameters.AddWithValue("$season", target.SeasonNumber);
            using SqliteDataReader reader = await championsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int seasonNumber = reader.GetInt32(0);
                int athleteId = reader.GetInt32(1);
                names.TryGetValue(athleteId, out string? name);
                priorChampions.Add(new SeasonChampionRef(seasonNumber, athleteId, name ?? $"Athlete {athleteId}", reader.GetInt32(2)));
            }
        }

        _ = rules;
        return new HistoryContext(stageWinners, priorChampions);
    }

    private static int? ResolveAthleteId(Dictionary<int, string> names, string? athleteName, int? athleteId)
    {
        if (!string.IsNullOrWhiteSpace(athleteName) && athleteId.HasValue)
        {
            throw new ArgumentException("Specify only one of '--athlete' or '--athlete-id'.", nameof(athleteId));
        }

        if (!string.IsNullOrWhiteSpace(athleteName))
        {
            string trimmed = athleteName.Trim();
            foreach (var candidate in names)
            {
                if (string.Equals(candidate.Value, trimmed, StringComparison.Ordinal))
                {
                    return candidate.Key;
                }
            }

            throw new InvalidOperationException($"Athlete '{trimmed}' was not found in this snapshot (not-found).");
        }

        return athleteId;
    }

    private static async Task<AthleteFocusView?> BuildAthleteFocusAsync(
        string databasePath,
        ResolvedTarget target,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        Dictionary<int, string> names,
        ChampionshipView before,
        ChampionshipView after,
        List<StageStandingRow> stageRows,
        List<DecodedRound> rounds,
        HistoryContext history,
        string? athleteName,
        int? athleteId,
        CancellationToken cancellationToken)
    {
        int? resolvedId = ResolveAthleteId(names, athleteName, athleteId);
        if (!resolvedId.HasValue)
        {
            return null;
        }

        if (!names.TryGetValue(resolvedId.Value, out string? name))
        {
            throw new InvalidOperationException($"Athlete id {resolvedId.Value} was not found in this snapshot (not-found).");
        }

        StageStandingRow? stageRow = stageRows.SingleOrDefault(r => r.SaveAthleteId == resolvedId.Value);
        if (stageRow is null)
        {
            throw new InvalidOperationException($"Athlete '{name}' is not part of Superleague season {target.SeasonNumber} stage {target.StageNumber} (not-found).");
        }

        ChampionshipEntry? beforeEntry = before.Available ? before.Standings.SingleOrDefault(s => s.AthleteId == resolvedId.Value) : null;
        ChampionshipEntry afterEntry = after.Standings.Single(s => s.AthleteId == resolvedId.Value);
        List<RoundPlacementRef> roundPlacements = [];
        foreach (DecodedRound round in rounds)
        {
            RoundPayloadEntry placement = round.Document.Placements.Single(p => p.AthleteId == resolvedId.Value);
            roundPlacements.Add(new RoundPlacementRef(round.RoundNumber, placement.Position, placement.BaseThousandths, ReportMarkdown.FormatPoints(placement.BaseThousandths), placement.FinalThousandths, ReportMarkdown.FormatPoints(placement.FinalThousandths)));
        }

        List<RecentFinish> recent = await LoadRecentFormAsync(databasePath, target, resolvedId.Value, names, cancellationToken).ConfigureAwait(false);
        (int winCount, List<StageWinnerRef> winRefs) = await LoadPriorWinsAsync(databasePath, target, resolvedId.Value, names, cancellationToken).ConfigureAwait(false);
        (int titleCount, List<int> titleSeasons) = await LoadPriorTitlesAsync(databasePath, target, resolvedId.Value, cancellationToken).ConfigureAwait(false);

        _ = rules;
        _ = history;
        return new AthleteFocusView(
            resolvedId.Value, name, stageRow.StageRank,
            stageRow.StageScoreThousandths, ReportMarkdown.FormatPoints(stageRow.StageScoreThousandths),
            stageRow.BaseScoreThousandths, ReportMarkdown.FormatPoints(stageRow.BaseScoreThousandths),
            stageRow.ChampionshipPointsThousandths, ReportMarkdown.FormatPoints(stageRow.ChampionshipPointsThousandths),
            stageRow.EarnedBonusThousandths, ReportMarkdown.FormatBonus(stageRow.EarnedBonusThousandths),
            beforeEntry?.Rank, afterEntry.Rank,
            beforeEntry is null ? null : beforeEntry.Rank - afterEntry.Rank,
            beforeEntry?.TotalChampionshipPointsThousandths, afterEntry.TotalChampionshipPointsThousandths,
            checked(afterEntry.TotalChampionshipPointsThousandths - (beforeEntry?.TotalChampionshipPointsThousandths ?? 0)),
            beforeEntry?.GapToLeaderThousandths, afterEntry.GapToLeaderThousandths,
            roundPlacements, recent, winCount, winRefs, titleCount, titleSeasons);
    }

    private static async Task<List<RecentFinish>> LoadRecentFormAsync(
        string databasePath, ResolvedTarget target, int athleteId, Dictionary<int, string> names, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SnapshotReader.OpenReadOnly(databasePath);
        List<RecentFinish> finishes = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.SeasonNumber, st.StageNumber, st.StageRank, st.ChampionshipPointsThousandths " +
            "FROM StageStandings st JOIN Seasons s ON s.Id = st.SeasonId JOIN Leagues l ON l.Id = st.LeagueId " +
            "WHERE l.Kind = 1 AND st.SaveAthleteId = $athlete AND (s.SeasonNumber < $season OR (s.SeasonNumber = $season AND st.StageNumber <= $stage)) " +
            "ORDER BY s.SeasonNumber DESC, st.StageNumber DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$athlete", athleteId);
        command.Parameters.AddWithValue("$season", target.SeasonNumber);
        command.Parameters.AddWithValue("$stage", target.StageNumber);
        command.Parameters.AddWithValue("$limit", RecentFormDepth);
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            finishes.Add(new RecentFinish(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), ReportMarkdown.FormatPoints(reader.GetInt32(3))));
        }

        finishes.Reverse();
        _ = names;
        return finishes;
    }

    private static async Task<(int Count, List<StageWinnerRef> Refs)> LoadPriorWinsAsync(
        string databasePath, ResolvedTarget target, int athleteId, Dictionary<int, string> names, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SnapshotReader.OpenReadOnly(databasePath);
        List<StageWinnerRef> wins = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.SeasonNumber, st.StageNumber, st.ChampionshipPointsThousandths FROM StageStandings st " +
            "JOIN Seasons s ON s.Id = st.SeasonId JOIN Leagues l ON l.Id = st.LeagueId " +
            "WHERE l.Kind = 1 AND st.SaveAthleteId = $athlete AND st.StageRank = 1 " +
            "AND (s.SeasonNumber < $season OR (s.SeasonNumber = $season AND st.StageNumber < $stage)) ORDER BY s.SeasonNumber DESC, st.StageNumber DESC;";
        command.Parameters.AddWithValue("$athlete", athleteId);
        command.Parameters.AddWithValue("$season", target.SeasonNumber);
        command.Parameters.AddWithValue("$stage", target.StageNumber);
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int seasonNumber = reader.GetInt32(0);
            int stageNumber = reader.GetInt32(1);
            names.TryGetValue(athleteId, out string? name);
            wins.Add(new StageWinnerRef(stageNumber, athleteId, name ?? $"Athlete {athleteId}", reader.GetInt32(2), seasonNumber));
        }

        int count = wins.Count;
        List<StageWinnerRef> refs = wins.Take(RecentWinRefs).Reverse().ToList();
        return (count, refs);
    }

    private static async Task<(int Count, List<int> Seasons)> LoadPriorTitlesAsync(
        string databasePath, ResolvedTarget target, int athleteId, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = SnapshotReader.OpenReadOnly(databasePath);
        List<int> seasons = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.SeasonNumber FROM SeasonStandings ss JOIN Seasons s ON s.Id = ss.SeasonId JOIN Leagues l ON l.Id = ss.LeagueId " +
            "WHERE l.Kind = 1 AND ss.SaveAthleteId = $athlete AND ss.IsChampion != 0 AND s.SeasonNumber < $season ORDER BY s.SeasonNumber;";
        command.Parameters.AddWithValue("$athlete", athleteId);
        command.Parameters.AddWithValue("$season", target.SeasonNumber);
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            seasons.Add(reader.GetInt32(0));
        }

        return (seasons.Count, seasons);
    }

    private static StageEvidenceDocument Assemble(
        SnapshotInfo snapshot,
        SnapshotReader.DatabaseView view,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        ResolvedTarget target,
        Dictionary<int, string> names,
        List<StageStandingRow> stageRows,
        List<DecodedRound> rounds,
        string stageChecksum,
        ChampionshipView before,
        ChampionshipView after,
        HistoryContext history,
        AthleteFocusView? focus,
        long elapsedMs)
    {
        Dictionary<int, ChampionshipEntry> beforeById = before.Standings.ToDictionary(s => s.AthleteId);
        List<StageTableRow> table = BuildStageTable(target, names, stageRows, beforeById, after);

        (List<EvidenceRound> evidenceRounds, List<RoundWinner> winners, HashSet<string> encodings) = BuildRoundEvidence(target, rounds);

        (List<ChampionshipRow> beforeRows, List<ChampionshipRow> afterRows) = BuildChampionshipRows(before, after, beforeById);

        List<string> limitations = BuildLimitations(before, history, target);

        string summary = BuildSummary(target, rules, table, after);

        return new StageEvidenceDocument(
            EvidenceSchemaVersion, EvidenceType, "completed", DateTimeOffset.UtcNow, elapsedMs,
            new EvidenceSource(
                snapshot.SaveId, snapshot.SaveName, snapshot.SnapshotPath,
                snapshot.ExportedUtc, snapshot.AcquiredUtc, snapshot.DatabaseSha256,
                snapshot.SchemaVersion, snapshot.RulesVersion, view.RngAlgorithm, view.RngVersion,
                snapshot.CurrentSeason, snapshot.Phase,
                rules.Version, rules.StagesPerSeason, rules.RoundsPerStage, rules.LeagueSize),
            new EvidenceTarget(
                target.SeasonNumber, target.SeasonId, target.LeagueId, target.LeagueName, "Superleague",
                target.StageNumber, target.IsFirstStage, target.IsFinalStage,
                target.CompletedRounds, table.Count, rounds.Count, stageChecksum,
                target.SeasonIsComplete, $"Stages:season={target.SeasonNumber}:stage={target.StageNumber}",
                $"Leagues:id={target.LeagueId}:kind=Superleague"),
            new EvidenceUnits(
                "Fixed-point thousandths integers; display = value / 1000 (for example 77000 = 77.000).",
                "Fixed-point thousandths of a percent; display = value / 1000 with percent sign (for example 7552 = +7.552%).",
                "Official season championship points from the saved scoring table with no bonus multiplier; fixed-point thousandths.",
                "Signed rank change = rankBefore - rankAfter (positive means improved); signed gap change = gapBefore - gapAfter (positive means closed the gap); gaps in thousandths.",
                "Stage score = sum of final round points; base score = sum of base points; earned bonus = round-plus-stage bonus pending until the next boundary; championship points = scoring-table award."),
            table, evidenceRounds, winners,
            new ChampionshipSnapshot(before.Available, before.UnavailableReason, before.CompletedStages, before.IsFinal, before.Checksum, beforeRows, before.EvidenceRef),
            new ChampionshipSnapshot(after.Available, null, after.CompletedStages, after.IsFinal, after.Checksum, afterRows, after.EvidenceRef),
            focus, history, limitations, summary,
            new EvidenceCoverage(
                target.StageNumber, rules.StagesPerSeason, rules.RoundsPerStage, rules.LeagueSize,
                [.. encodings.OrderBy(e => e, StringComparer.Ordinal)],
                view.Seasons.Count, history.StageWinners.Count));
    }

    private static List<StageTableRow> BuildStageTable(
        ResolvedTarget target,
        Dictionary<int, string> names,
        List<StageStandingRow> stageRows,
        Dictionary<int, ChampionshipEntry> beforeById,
        ChampionshipView after)
    {
        List<StageTableRow> table = new(stageRows.Count);
        foreach (StageStandingRow row in stageRows.OrderBy(r => r.StageRank))
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            beforeById.TryGetValue(row.SaveAthleteId, out ChampionshipEntry? beforeEntry);
            ChampionshipEntry afterEntry = after.Standings.Single(s => s.AthleteId == row.SaveAthleteId);
            table.Add(new StageTableRow(
                row.SaveAthleteId, name ?? $"Athlete {row.SaveAthleteId}", row.StageRank,
                row.StageScoreThousandths, ReportMarkdown.FormatPoints(row.StageScoreThousandths),
                row.BaseScoreThousandths, ReportMarkdown.FormatPoints(row.BaseScoreThousandths),
                row.ChampionshipPointsThousandths, ReportMarkdown.FormatPoints(row.ChampionshipPointsThousandths),
                row.RoundWins, row.EarnedBonusThousandths, ReportMarkdown.FormatBonus(row.EarnedBonusThousandths),
                row.RoundPlaceCounts,
                beforeEntry?.Rank, afterEntry.Rank,
                beforeEntry is null ? null : beforeEntry.Rank - afterEntry.Rank,
                beforeEntry?.TotalChampionshipPointsThousandths ?? 0, afterEntry.TotalChampionshipPointsThousandths,
                beforeEntry?.GapToLeaderThousandths, afterEntry.GapToLeaderThousandths,
                $"StageStandings:season={target.SeasonNumber}:stage={target.StageNumber}:rank={row.StageRank}"));
        }

        return table;
    }

    private static (List<EvidenceRound> Rounds, List<RoundWinner> Winners, HashSet<string> Encodings) BuildRoundEvidence(
        ResolvedTarget target,
        List<DecodedRound> rounds)
    {
        List<EvidenceRound> evidenceRounds = new(rounds.Count);
        List<RoundWinner> winners = new(rounds.Count);
        HashSet<string> encodings = new(StringComparer.Ordinal);
        foreach (DecodedRound round in rounds.OrderBy(r => r.RoundNumber))
        {
            encodings.Add(round.Encoding);
            RoundPayloadEntry winner = round.Document.Placements.Single(p => p.Position == 1);
            winners.Add(new RoundWinner(round.RoundNumber, winner.AthleteId, winner.Name, winner.FinalThousandths, ReportMarkdown.FormatPoints(winner.FinalThousandths)));
            List<RoundPlacement> placements = round.Document.Placements
                .OrderBy(p => p.Position)
                .Select(p => new RoundPlacement(
                    p.AthleteId, p.Name, p.Position, p.BaseThousandths, ReportMarkdown.FormatPoints(p.BaseThousandths),
                    p.ActiveBonusThousandths, ReportMarkdown.FormatBonus(p.ActiveBonusThousandths),
                    p.FinalThousandths, ReportMarkdown.FormatPoints(p.FinalThousandths),
                    p.CumulativeAfterThousandths, ReportMarkdown.FormatPoints(p.CumulativeAfterThousandths)))
                .ToList();
            evidenceRounds.Add(new EvidenceRound(
                round.RoundNumber, winner.AthleteId, winner.Name,
                round.PayloadChecksum, round.Encoding, placements,
                $"Rounds:season={target.SeasonNumber}:stage={target.StageNumber}:round={round.RoundNumber}"));
        }

        return (evidenceRounds, winners, encodings);
    }

    private static (List<ChampionshipRow> Before, List<ChampionshipRow> After) BuildChampionshipRows(
        ChampionshipView before,
        ChampionshipView after,
        Dictionary<int, ChampionshipEntry> beforeById)
    {
        List<ChampionshipRow> beforeRows = before.Standings
            .OrderBy(s => s.Rank)
            .Select(s => new ChampionshipRow(
                s.AthleteId, s.Name, s.Rank,
                s.TotalChampionshipPointsThousandths, ReportMarkdown.FormatPoints(s.TotalChampionshipPointsThousandths),
                s.TotalStageScoreThousandths, ReportMarkdown.FormatPoints(s.TotalStageScoreThousandths),
                s.StageWins, s.RoundWins, s.GapToLeaderThousandths, ReportMarkdown.FormatPoints(s.GapToLeaderThousandths),
                s.DeterministicTie, s.TieGroupSize, null, null, null, null, null, null))
            .ToList();
        List<ChampionshipRow> afterRows = after.Standings
            .OrderBy(s => s.Rank)
            .Select(s => MapAfterRow(s, beforeById))
            .ToList();
        return (beforeRows, afterRows);
    }

    private static ChampionshipRow MapAfterRow(ChampionshipEntry entry, Dictionary<int, ChampionshipEntry> beforeById)
    {
        beforeById.TryGetValue(entry.AthleteId, out ChampionshipEntry? before);
        int? rankBefore = before?.Rank;
        int? rankChange = before is null ? null : before.Rank - entry.Rank;
        int pointsBefore = before?.TotalChampionshipPointsThousandths ?? 0;
        int awarded = checked(entry.TotalChampionshipPointsThousandths - pointsBefore);
        int? gapBefore = before?.GapToLeaderThousandths;
        int? gapChange = before is null ? null : before.GapToLeaderThousandths - entry.GapToLeaderThousandths;
        return new ChampionshipRow(
            entry.AthleteId, entry.Name, entry.Rank,
            entry.TotalChampionshipPointsThousandths, ReportMarkdown.FormatPoints(entry.TotalChampionshipPointsThousandths),
            entry.TotalStageScoreThousandths, ReportMarkdown.FormatPoints(entry.TotalStageScoreThousandths),
            entry.StageWins, entry.RoundWins, entry.GapToLeaderThousandths, ReportMarkdown.FormatPoints(entry.GapToLeaderThousandths),
            entry.DeterministicTie, entry.TieGroupSize, rankBefore, rankChange, pointsBefore, awarded, gapBefore, gapChange);
    }

    private static List<string> BuildLimitations(ChampionshipView before, HistoryContext history, ResolvedTarget target)
    {
        List<string> limitations =
        [
            "Provisional championship order uses persisted stage points plus deterministic tie-break vectors (stage places, round places, raw totals); exact deterministic ties are flagged and the historical seeded draw is not replayed.",
            "Stage table ranks, championship points and earned bonuses are the persisted official values; earned bonus stays pending until the next stage boundary.",
            "Weighted career prestige/performance points are never substituted for official season championship points and are excluded from this evidence.",
            "Current mutable bonuses (AthleteCareers), later Cup results, later stages/seasons and future honours are excluded by the stage cutoff; unsupported historical claims are omitted, not inferred.",
            $"Detailed investigation uses the same snapshot via 'snapshot query' (for example StageStandings for season {target.SeasonNumber} stage {target.StageNumber}); no live-app calls are used.",
        ];
        if (!before.Available)
        {
            limitations.Add("Championship-before is unavailable for stage one; rank/gap changes are null for that boundary.");
        }

        if (history.PriorChampions.Count == 0)
        {
            limitations.Add("No completed prior Superleague season exists at this cutoff; prior-title context is empty by construction, not by omission.");
        }

        return limitations;
    }

    private static string BuildSummary(
        ResolvedTarget target,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules,
        List<StageTableRow> table,
        ChampionshipView after)
    {
        string leaderName = after.Standings.OrderBy(s => s.Rank).First().Name;
        int leaderPoints = after.Standings.OrderBy(s => s.Rank).First().TotalChampionshipPointsThousandths;
        int runnerPoints = after.Standings.OrderBy(s => s.Rank).Skip(1).First().TotalChampionshipPointsThousandths;
        StageTableRow stageWinner = table.Single(r => r.StageRank == 1);
        return $"Superleague season {target.SeasonNumber} stage {target.StageNumber} completed " +
            $"({target.CompletedRounds}/{rules.RoundsPerStage} rounds, {table.Count} athletes): " +
            $"stage winner {stageWinner.Name} ({stageWinner.StageScoreDisplay} stage, {stageWinner.ChampionshipPointsDisplay} championship). " +
            $"Championship after {after.CompletedStages} stage(s): {leaderName} leads {ReportMarkdown.FormatPoints(leaderPoints)} " +
            $"(+{ReportMarkdown.FormatPoints(checked(leaderPoints - runnerPoints))} gap, thousandths). " +
            $"Cutoff season {target.SeasonNumber} stage {target.StageNumber}; later events excluded. " +
            $"Rules v{rules.Version} ({rules.StagesPerSeason} stages x {rules.RoundsPerStage} rounds, league {rules.LeagueSize}).";
    }

    public sealed record HistoryContext(
        IReadOnlyList<StageWinnerRef> StageWinners,
        IReadOnlyList<SeasonChampionRef> PriorChampions);
}
