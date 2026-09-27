using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.GlobalStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Seasons;

namespace MtgSoloSports.Features.Leagues.CurrentStandings;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads a league's current
/// season standings accumulated from persisted stage championship points
/// (never resimulates). Provisional standings sum completed stages with the
/// same kernel mathematics as finalization, breaking full ties with a fork of
/// the versioned save RNG that is discarded (reads never mutate RNG).
/// Completed seasons read persisted final rows so views stay stable.
/// </summary>
public sealed class GetCurrentStandingsHandler
{
    private readonly SaveStore _store;

    public GetCurrentStandingsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetCurrentStandingsResponse> HandleAsync(Guid saveId, int leagueId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (leagueId <= 0)
        {
            throw new ArgumentException("League id must be positive.", nameof(leagueId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity season = await AdvanceRoundHandler.LoadSeasonAsync(context, metadata, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);
        GlobalStageGate.GlobalStageView global = await GlobalStageGate
            .LoadGlobalStageAsync(context, season, rules, cancellationToken)
            .ConfigureAwait(false);

        if (season.IsComplete)
        {
            return await BuildFinalResponseAsync(context, saveId, season, league, global, cancellationToken).ConfigureAwait(false);
        }

        return await BuildProvisionalResponseAsync(context, saveId, season, league, rules, global, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<LeagueEntity> LoadLeagueAsync(
        SaveDbContext context,
        SeasonEntity season,
        int leagueId,
        CancellationToken cancellationToken)
    {
        LeagueEntity? league = await context.Leagues
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == leagueId, cancellationToken)
            .ConfigureAwait(false);
        if (league is null || league.SeasonId != season.Id)
        {
            throw new InvalidOperationException($"League {leagueId} is not part of season {season.SeasonNumber}.");
        }

        return league;
    }

    internal static async Task<GetCurrentStandingsResponse> BuildFinalResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity season,
        LeagueEntity league,
        GlobalStageGate.GlobalStageView global,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.SeasonRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, AthleteCardInfo> cards = await LoadCardsAsync(context, cancellationToken).ConfigureAwait(false);
        List<CurrentStandingEntry> standings = MapPersisted(rows, names, cards);
        string checksum = ComputePersistedChecksum(rows, names);
        return new GetCurrentStandingsResponse(
            saveId,
            season.SeasonNumber,
            league.Id,
            league.Name,
            rows.Count == 0 ? 0 : 32,
            global.CurrentStage,
            true,
            true,
            checksum,
            standings);
    }

    internal static async Task<GetCurrentStandingsResponse> BuildProvisionalResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity season,
        LeagueEntity league,
        RulesV1 rules,
        GlobalStageGate.GlobalStageView global,
        CancellationToken cancellationToken)
    {
        List<StageStandingEntity> stageRows = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.StageNumber)
            .ThenBy(e => e.StageRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        int completedStages = stageRows.Select(r => r.StageNumber).Distinct().Count();
        if (completedStages == 0)
        {
            return EmptyResponse(saveId, season, league, global);
        }

        List<List<SeasonStageEntry>> groups = GroupProvisional(stageRows, league, names);
        Pcg32V1 fork = await ForkRngAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SeasonAthleteTotals> totals = SeasonCalculator.Accumulate(groups, rules);
        IReadOnlyList<SeasonRankedAthlete> ranked = SeasonCalculator.Rank(totals, fork, rules);
        Dictionary<int, AthleteCardInfo> cards = await LoadCardsAsync(context, cancellationToken).ConfigureAwait(false);
        return MapProvisional(saveId, season, league, global, completedStages, ranked, cards);
    }

    internal static GetCurrentStandingsResponse EmptyResponse(
        Guid saveId,
        SeasonEntity season,
        LeagueEntity league,
        GlobalStageGate.GlobalStageView global)
    {
        return new GetCurrentStandingsResponse(
            saveId,
            season.SeasonNumber,
            league.Id,
            league.Name,
            0,
            global.CurrentStage,
            global.IsSeasonComplete,
            false,
            string.Empty,
            []);
    }

    internal static List<List<SeasonStageEntry>> GroupProvisional(
        List<StageStandingEntity> stageRows,
        LeagueEntity league,
        Dictionary<int, string> names)
    {
        List<int> stageNumbers = stageRows.Select(r => r.StageNumber).Distinct().OrderBy(n => n).ToList();
        List<List<SeasonStageEntry>> groups = new(stageNumbers.Count);
        foreach (int stageNumber in stageNumbers)
        {
            List<StageStandingEntity> stageGroup = stageRows.Where(r => r.StageNumber == stageNumber).ToList();
            groups.Add(MapStageGroup(stageGroup, league, stageNumber, names));
        }

        return groups;
    }

    internal static List<SeasonStageEntry> MapStageGroup(
        List<StageStandingEntity> stageGroup,
        LeagueEntity league,
        int stageNumber,
        Dictionary<int, string> names)
    {
        List<SeasonStageEntry> entries = new(stageGroup.Count);
        foreach (StageStandingEntity row in stageGroup)
        {
            entries.Add(MapStageRow(row, league, names));
        }

        return entries;
    }

    internal static SeasonStageEntry MapStageRow(
        StageStandingEntity row,
        LeagueEntity league,
        Dictionary<int, string> names)
    {
        if (!names.TryGetValue(row.SaveAthleteId, out string? name))
        {
            throw new InvalidOperationException($"League '{league.Name}' references unknown athlete {row.SaveAthleteId}.");
        }

        List<int>? roundCounts = JsonSerializer.Deserialize<List<int>>(row.RoundPlaceCountsJson);
        if (roundCounts is null)
        {
            throw new InvalidOperationException($"League '{league.Name}' stage {row.StageNumber} has corrupt round counts.");
        }

        return new SeasonStageEntry(
            row.SaveAthleteId,
            name,
            row.StageRank,
            row.ChampionshipPointsThousandths,
            row.StageScoreThousandths,
            row.BaseScoreThousandths,
            roundCounts);
    }

    internal static GetCurrentStandingsResponse MapProvisional(
        Guid saveId,
        SeasonEntity season,
        LeagueEntity league,
        GlobalStageGate.GlobalStageView global,
        int completedStages,
        IReadOnlyList<SeasonRankedAthlete> ranked,
        Dictionary<int, AthleteCardInfo>? cards = null)
    {
        List<CurrentStandingEntry> standings = new(ranked.Count);
        foreach (SeasonRankedAthlete entry in ranked)
        {
            (int color, string colorName, string? imageUrl) = LookupCard(cards, entry.AthleteId);
            standings.Add(new CurrentStandingEntry(
                entry.AthleteId,
                entry.Name,
                entry.SeasonRank,
                entry.TotalChampionshipPointsThousandths,
                entry.TotalStageScoreThousandths,
                entry.TotalBaseScoreThousandths,
                entry.StageWins,
                entry.RoundWins,
                false,
                color,
                colorName,
                imageUrl));
        }

        return new GetCurrentStandingsResponse(
            saveId,
            season.SeasonNumber,
            league.Id,
            league.Name,
            completedStages,
            global.CurrentStage,
            global.IsSeasonComplete,
            false,
            SeasonCalculator.ComputeChecksum(ranked),
            standings);
    }

    internal static List<CurrentStandingEntry> MapPersisted(
        List<SeasonStandingEntity> rows,
        Dictionary<int, string> names,
        Dictionary<int, AthleteCardInfo>? cards = null)
    {
        List<CurrentStandingEntry> standings = new(rows.Count);
        foreach (SeasonStandingEntity row in rows)
        {
            if (!names.TryGetValue(row.SaveAthleteId, out string? name))
            {
                name = $"Athlete {row.SaveAthleteId}";
            }

            (int color, string colorName, string? imageUrl) = LookupCard(cards, row.SaveAthleteId);
            standings.Add(new CurrentStandingEntry(
                row.SaveAthleteId,
                name,
                row.SeasonRank,
                row.TotalChampionshipPointsThousandths,
                row.TotalStageScoreThousandths,
                row.TotalBaseScoreThousandths,
                row.StageWins,
                row.RoundWins,
                row.IsChampion,
                color,
                colorName,
                imageUrl));
        }

        return standings;
    }

    internal static string ComputePersistedChecksum(List<SeasonStandingEntity> rows, Dictionary<int, string> names)
    {
        List<SeasonRankedAthlete> ranked = new(rows.Count);
        foreach (SeasonStandingEntity row in rows.OrderBy(r => r.SeasonRank))
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            List<int> stageCounts = JsonSerializer.Deserialize<List<int>>(row.StagePlaceCountsJson) ?? [];
            List<int> roundCounts = JsonSerializer.Deserialize<List<int>>(row.RoundPlaceCountsJson) ?? [];
            ranked.Add(new SeasonRankedAthlete(
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}",
                row.SeasonRank,
                row.TotalChampionshipPointsThousandths,
                row.TotalStageScoreThousandths,
                row.TotalBaseScoreThousandths,
                row.StageWins,
                row.RoundWins,
                stageCounts,
                roundCounts,
                row.IsChampion));
        }

        return ranked.Count == 0 ? string.Empty : SeasonCalculator.ComputeChecksum(ranked);
    }

    internal static async Task<Dictionary<int, string>> LoadNamesAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal sealed record AthleteCardInfo(string Name, int SportingColor, string? ImageUrl);

    internal static async Task<Dictionary<int, AthleteCardInfo>> LoadCardsAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(
                e => e.Id,
                e => new AthleteCardInfo(e.Name, e.SportingColor, e.ImageUrl),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static (int SportingColor, string SportingColorName, string? ImageUrl) LookupCard(
        Dictionary<int, AthleteCardInfo>? cards,
        int athleteId)
    {
        if (cards is not null && cards.TryGetValue(athleteId, out AthleteCardInfo? info))
        {
            string colorName = Enum.IsDefined(typeof(SimulationKernel.Catalog.SportingColor), info.SportingColor)
                ? ((SimulationKernel.Catalog.SportingColor)info.SportingColor).ToString()
                : "Unknown";
            return (info.SportingColor, colorName, info.ImageUrl);
        }

        return (0, "Unknown", null);
    }

    internal static async Task<Pcg32V1> ForkRngAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        RngStateEntity row = await context.RngStates
            .AsNoTracking()
            .SingleAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        return Pcg32V1.Restore(row.ToState());
    }
}
