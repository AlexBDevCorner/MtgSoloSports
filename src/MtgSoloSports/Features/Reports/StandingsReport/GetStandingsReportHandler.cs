using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.History.ListStages;
using MtgSoloSports.Features.Leagues.CurrentStandings;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.GlobalStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Seasons;

namespace MtgSoloSports.Features.Reports.StandingsReport;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Builds a versioned
/// standings report for exactly the requested season and league. Completed
/// seasons read persisted final rows; the active incomplete season accumulates
/// persisted completed stages with the same kernel mathematics as the live
/// table, breaking full ties with a discarded fork of the versioned save RNG
/// (reads never mutate RNG). Never falls back to another season or league.
/// Read-only: single AsNoTracking context in one committed read transaction
/// for a consistent snapshot, no mutation, no external services. Corrupt stage
/// cursors abort via GlobalStageGate and surface as HTTP 400; never repaired
/// with a synthetic stage value.
/// </summary>
public sealed class GetStandingsReportHandler
{
    private readonly SaveStore _store;

    public GetStandingsReportHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

    public sealed record StandingsReportRow(
        int Rank,
        int AthleteId,
        string Name,
        int TotalChampionshipPointsThousandths,
        int TotalStageScoreThousandths,
        int TotalBaseScoreThousandths,
        int StageWins,
        int RoundWins,
        bool IsChampion,
        string? Zone,
        string? ZoneLabel);

    public async Task<ReportFile> HandleAsync(Guid saveId, int leagueId, int seasonNumber, string? format, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (leagueId <= 0)
        {
            throw new ArgumentException("League id must be positive.", nameof(leagueId));
        }

        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(seasonNumber));
        }

        string normalized = ReportContract.NormalizeFormat(format);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity season = await ListHistoryCompetitionsHandler.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await ListHistoryStagesHandler.LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);

        ReportData data = await LoadReportDataAsync(context, rules, season, league, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(normalized, "md", StringComparison.Ordinal))
        {
            string markdown = StandingsReportMarkdown.Build(saveId, metadata, rules, season, league, data);
            return new ReportFile(
                Encoding.UTF8.GetBytes(markdown),
                ReportContract.MarkdownContentType,
                ReportFilenames.StandingsFileName(seasonNumber, leagueId, league.Name, "md"));
        }

        string json = BuildJson(saveId, metadata, rules, season, league, data);
        return new ReportFile(
            Encoding.UTF8.GetBytes(json),
            ReportContract.JsonContentType,
            ReportFilenames.StandingsFileName(seasonNumber, leagueId, league.Name, "json"));
    }

    internal sealed record ReportData(
        bool IsFinal,
        string Status,
        int CompletedStages,
        int TotalStages,
        int GlobalStage,
        string SeasonChecksum,
        IReadOnlyList<StandingsReportRow> Rows);

    internal static async Task<ReportData> LoadReportDataAsync(
        SaveDbContext context,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity season,
        LeagueEntity league,
        CancellationToken cancellationToken)
    {
        if (season.IsComplete)
        {
            return await LoadFinalAsync(context, rules, season, league, cancellationToken).ConfigureAwait(false);
        }

        return await LoadProvisionalAsync(context, rules, season, league, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ReportData> LoadFinalAsync(
        SaveDbContext context,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity season,
        LeagueEntity league,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.SeasonRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new HistoryNotFoundException($"Season {season.SeasonNumber} for league '{league.Name}' has no final table.");
        }

        if (rows.Count != 32 || !rows.Select(r => r.SeasonRank).ToHashSet().SetEquals(Enumerable.Range(1, 32)))
        {
            throw new InvalidOperationException($"League '{league.Name}' must cover ranks 1..32 exactly once.");
        }

        Dictionary<int, string> names = await GetCurrentStandingsHandler.LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        LeagueLevel level = LeagueEntityLevels.GetLevel(league);
        List<StandingsReportRow> standings = MapFinalRows(rows, names, level);
        string checksum = GetCurrentStandingsHandler.ComputePersistedChecksum(rows, names);
        GlobalStageGate.GlobalStageView global = await GlobalStageGate.LoadGlobalStageAsync(context, season, rules, cancellationToken).ConfigureAwait(false);

        return new ReportData(
            true,
            "final",
            rules.StagesPerSeason,
            rules.StagesPerSeason,
            global.CurrentStage,
            checksum,
            standings);
    }

    internal static List<StandingsReportRow> MapFinalRows(
        List<SeasonStandingEntity> rows,
        Dictionary<int, string> names,
        LeagueLevel level)
    {
        List<StandingsReportRow> standings = new(rows.Count);
        foreach (SeasonStandingEntity row in rows)
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            string? zone = ReportZones.ZoneForRank(level, row.SeasonRank, true);
            standings.Add(new StandingsReportRow(
                row.SeasonRank,
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}",
                row.TotalChampionshipPointsThousandths,
                row.TotalStageScoreThousandths,
                row.TotalBaseScoreThousandths,
                row.StageWins,
                row.RoundWins,
                row.IsChampion,
                zone,
                zone is null ? null : ReportZones.ZoneLabel(level, zone)));
        }

        return standings;
    }

    internal static async Task<ReportData> LoadProvisionalAsync(
        SaveDbContext context,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity season,
        LeagueEntity league,
        CancellationToken cancellationToken)
    {
        List<StageStandingEntity> stageRows = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.StageNumber)
            .ThenBy(e => e.StageRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await GetCurrentStandingsHandler.LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        int completedStages = stageRows.Select(r => r.StageNumber).Distinct().Count();
        GlobalStageGate.GlobalStageView global = await GlobalStageGate.LoadGlobalStageAsync(context, season, rules, cancellationToken).ConfigureAwait(false);

        if (completedStages == 0)
        {
            return new ReportData(false, "provisional", 0, rules.StagesPerSeason, global.CurrentStage, string.Empty, []);
        }

        List<List<SeasonStageEntry>> groups = GetCurrentStandingsHandler.GroupProvisional(stageRows, league, names);
        Pcg32V1 fork = await GetCurrentStandingsHandler.ForkRngAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SeasonAthleteTotals> totals = SeasonCalculator.Accumulate(groups, rules);
        IReadOnlyList<SeasonRankedAthlete> ranked = SeasonCalculator.Rank(totals, fork, rules);
        return new ReportData(
            false,
            "provisional",
            completedStages,
            rules.StagesPerSeason,
            global.CurrentStage,
            SeasonCalculator.ComputeChecksum(ranked),
            MapProvisionalRows(ranked));
    }

    internal static List<StandingsReportRow> MapProvisionalRows(IReadOnlyList<SeasonRankedAthlete> ranked)
    {
        List<StandingsReportRow> standings = new(ranked.Count);
        foreach (SeasonRankedAthlete entry in ranked)
        {
            standings.Add(new StandingsReportRow(
                entry.SeasonRank,
                entry.AthleteId,
                entry.Name,
                entry.TotalChampionshipPointsThousandths,
                entry.TotalStageScoreThousandths,
                entry.TotalBaseScoreThousandths,
                entry.StageWins,
                entry.RoundWins,
                false,
                null,
                null));
        }

        return standings;
    }

    internal static string BuildJson(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity season,
        LeagueEntity league,
        ReportData data)
    {
        return StandingsReportJson.Build(saveId, metadata, rules, season, league, data);
    }

    internal static string BuildMarkdown(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity season,
        LeagueEntity league,
        ReportData data)
    {
        return StandingsReportMarkdown.Build(saveId, metadata, rules, season, league, data);
    }
}
