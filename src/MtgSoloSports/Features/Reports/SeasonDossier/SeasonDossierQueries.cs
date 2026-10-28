using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.Leagues.CurrentStandings;
using MtgSoloSports.Features.Reports.StandingsReport;
using MtgSoloSports.Features.Simulation.GlobalStage;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal static class SeasonDossierQueries
{
    internal static async Task<SeasonDossierData> LoadAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity season,
        SimulationKernel.Rules.RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = await LoadSeasonLeaguesAsync(context, season, cancellationToken).ConfigureAwait(false);
        LeagueEntity superleague = PickSuperleague(leagues);
        bool isOfficial = IsOfficialSuperleague(superleague);
        GetStandingsReportHandler.ReportData standings = await GetStandingsReportHandler
            .LoadReportDataAsync(context, rules, season, superleague, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> names = await GetCurrentStandingsHandler.LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        GlobalStageGate.GlobalStageView global = await GlobalStageGate
            .LoadGlobalStageAsync(context, season, rules, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StageWinnerEntry> progression = await LoadStageProgressionAsync(context, season, superleague, names, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FeederSummaryEntry> feeders = await LoadFeederSummariesAsync(context, season, leagues, superleague, global, names, cancellationToken).ConfigureAwait(false);
        PostseasonSection postseason = await LoadPostseasonAsync(context, season, cancellationToken).ConfigureAwait(false);
        (CupSummaryEntry? color, CupSummaryEntry? type) = await SeasonDossierCupQueries.LoadCupsAsync(context, saveId, season, cancellationToken).ConfigureAwait(false);
        HistorySection history = await SeasonDossierHistoryQueries.LoadHistoryAsync(context, season, superleague, standings, color, type, names, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CareerRecordEntry> records = await SeasonDossierHistoryQueries.LoadCareerRecordsAsync(context, cancellationToken).ConfigureAwait(false);
        return new SeasonDossierData(
            season.SeasonNumber,
            season.Id,
            season.IsComplete,
            standings.Status,
            standings.IsFinal,
            standings.CompletedStages,
            standings.TotalStages,
            standings.GlobalStage,
            standings.SeasonChecksum,
            0,
            new SuperleagueSection(superleague.Id, superleague.Name, isOfficial, standings.Rows),
            progression,
            feeders,
            postseason,
            color,
            type,
            history,
            records);
    }

    internal static async Task<List<LeagueEntity>> LoadSeasonLeaguesAsync(
        SaveDbContext context,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count == 0)
        {
            throw new InvalidOperationException($"Season {season.SeasonNumber} has no leagues; corrupted sporting state.");
        }

        return leagues;
    }

    internal static LeagueEntity PickSuperleague(List<LeagueEntity> leagues)
    {
        LeagueEntity? official = leagues.FirstOrDefault(l => l.Kind == (int)LeagueKind.Superleague);
        if (official is not null)
        {
            return official;
        }

        return leagues.OrderBy(l => l.Id).First();
    }

    internal static bool IsOfficialSuperleague(LeagueEntity league)
    {
        return league.Kind == (int)LeagueKind.Superleague;
    }

    internal static async Task<IReadOnlyList<StageWinnerEntry>> LoadStageProgressionAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity superleague,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<StageStandingEntity> rows = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == superleague.Id)
            .OrderBy(e => e.StageNumber)
            .ThenBy(e => e.StageRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        List<StageWinnerEntry> winners = new();
        foreach (IGrouping<int, StageStandingEntity> group in rows.GroupBy(r => r.StageNumber).OrderBy(g => g.Key))
        {
            StageStandingEntity? winner = group.OrderBy(r => r.StageRank).FirstOrDefault();
            if (winner is null)
            {
                continue;
            }

            names.TryGetValue(winner.SaveAthleteId, out string? name);
            winners.Add(new StageWinnerEntry(
                group.Key,
                winner.SaveAthleteId,
                name ?? $"Athlete {winner.SaveAthleteId}",
                winner.ChampionshipPointsThousandths,
                true));
        }

        return winners;
    }

    internal static async Task<IReadOnlyList<FeederSummaryEntry>> LoadFeederSummariesAsync(
        SaveDbContext context,
        SeasonEntity season,
        List<LeagueEntity> leagues,
        LeagueEntity superleague,
        GlobalStageGate.GlobalStageView global,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> feeders = leagues.Where(l => l.Id != superleague.Id).OrderBy(l => l.Id).ToList();
        if (feeders.Count == 0)
        {
            return [];
        }

        Dictionary<int, List<SeasonStandingEntity>> finals = await LoadFinalsByLeagueAsync(context, season, feeders, cancellationToken).ConfigureAwait(false);
        List<FeederSummaryEntry> summaries = new(feeders.Count);
        foreach (LeagueEntity feeder in feeders)
        {
            summaries.Add(MapFeeder(feeder, season, global, finals, names));
        }

        return summaries;
    }

    internal static async Task<Dictionary<int, List<SeasonStandingEntity>>> LoadFinalsByLeagueAsync(
        SaveDbContext context,
        SeasonEntity season,
        List<LeagueEntity> feeders,
        CancellationToken cancellationToken)
    {
        if (!season.IsComplete)
        {
            return new Dictionary<int, List<SeasonStandingEntity>>();
        }

        HashSet<int> ids = feeders.Select(f => f.Id).ToHashSet();
        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && ids.Contains(e.LeagueId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.GroupBy(r => r.LeagueId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.SeasonRank).ToList());
    }

    internal static FeederSummaryEntry MapFeeder(
        LeagueEntity feeder,
        SeasonEntity season,
        GlobalStageGate.GlobalStageView global,
        Dictionary<int, List<SeasonStandingEntity>> finals,
        Dictionary<int, string> names)
    {
        string level = LeagueEntityLevels.GetLevel(feeder).ToString();
        GlobalStageGate.LeagueStageStatus? status = global.Leagues.FirstOrDefault(l => l.LeagueId == feeder.Id);
        int completed = status?.CompletedStages ?? 0;
        if (!season.IsComplete)
        {
            return new FeederSummaryEntry(feeder.Id, feeder.Name, level, "provisional", completed, null, null, null);
        }

        if (!finals.TryGetValue(feeder.Id, out List<SeasonStandingEntity>? rows) || rows.Count == 0)
        {
            return new FeederSummaryEntry(feeder.Id, feeder.Name, level, "final-unavailable", completed, null, null, null);
        }

        return new FeederSummaryEntry(
            feeder.Id,
            feeder.Name,
            level,
            "final",
            completed,
            ToPodium(rows, 1, names),
            ToPodium(rows, 2, names),
            ToPodium(rows, 3, names));
    }

    internal static PodiumEntry? ToPodium(List<SeasonStandingEntity> rows, int rank, Dictionary<int, string> names)
    {
        SeasonStandingEntity? row = rows.FirstOrDefault(r => r.SeasonRank == rank);
        if (row is null)
        {
            return null;
        }

        names.TryGetValue(row.SaveAthleteId, out string? name);
        return new PodiumEntry(row.SaveAthleteId, name ?? $"Athlete {row.SaveAthleteId}", row.TotalChampionshipPointsThousandths);
    }

    internal static async Task<PostseasonSection> LoadPostseasonAsync(
        SaveDbContext context,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        SeasonEntity? successor = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == season.SeasonNumber + 1, cancellationToken)
            .ConfigureAwait(false);
        if (!season.IsComplete)
        {
            return new PostseasonSection("not-started", "not-started", "not-started", "Season in progress; postseason movement, qualifier and Cups are pending.", [], []);
        }

        if (successor is null)
        {
            return new PostseasonSection("pending", "pending", "pending", $"Season {season.SeasonNumber} final tables are persisted; movement, qualifier, rebalance and Cups for the next transition are pending.", [], []);
        }

        List<MovementEntity> movements = await context.Movements
            .AsNoTracking()
            .Where(e => e.FromSeasonId == season.Id && e.ToSeasonId == successor.Id)
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.FromSeasonRank)
            .ThenBy(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<QualifierStandingEntity> qualifiers = await context.QualifierStandings
            .AsNoTracking()
            .Where(e => e.FromSeasonId == season.Id && e.ToSeasonId == successor.Id)
            .OrderBy(e => e.QualifierBoundary)
            .ThenBy(e => e.QualifierSportingColor)
            .ThenBy(e => e.QualifierRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool isInaugural = season.SeasonNumber == 1 && !season.HasSuperleague;
        if (isInaugural)
        {
            IReadOnlyList<PostseasonMovementEntry> inauguralMoves = await MapMovementsAsync(context, movements, cancellationToken).ConfigureAwait(false);
            int inauguralRebalances = movements.Count(e => e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement
                || e.Kind == (int)MovementKind.RebalanceUp || e.Kind == (int)MovementKind.RebalanceDown);
            string inauguralRebalance = inauguralRebalances > 0
                ? $"resolved ({inauguralRebalances} rows)"
                : movements.Count > 0 ? "none-required" : "pending";
            return new PostseasonSection(
                movements.Count > 0 ? $"resolved ({movements.Count} rows)" : "pending",
                "not-applicable",
                inauguralRebalance,
                $"Season 1 inaugural transition to Season {successor.SeasonNumber}: {movements.Count} movement rows including inaugural promotions; no qualifier is scheduled for the inaugural transition; rebalance {inauguralRebalance}.",
                inauguralMoves,
                []);
        }

        int rebalances = movements.Count(e => e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement
            || e.Kind == (int)MovementKind.RebalanceUp || e.Kind == (int)MovementKind.RebalanceDown);
        string movement = movements.Count > 0 ? $"resolved ({movements.Count} rows)" : "pending";
        string qualifier = qualifiers.Count > 0 ? $"played ({qualifiers.Count} standings rows)" : "pending";
        string rebalance = rebalances > 0
            ? $"resolved ({rebalances} rows)"
            : movements.Count > 0 && qualifiers.Count > 0 ? "none-required" : "pending";
        IReadOnlyList<PostseasonMovementEntry> mappedMovements = await MapMovementsAsync(context, movements, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PostseasonQualifierEntry> mappedQualifiers = await MapQualifiersAsync(context, qualifiers, cancellationToken).ConfigureAwait(false);
        return new PostseasonSection(movement, qualifier, rebalance, $"Transition to Season {successor.SeasonNumber}: movement {movement}; qualifier {qualifier}; rebalance {rebalance}.", mappedMovements, mappedQualifiers);
    }

    internal static async Task<IReadOnlyList<PostseasonMovementEntry>> MapMovementsAsync(
        SaveDbContext context,
        List<MovementEntity> movements,
        CancellationToken cancellationToken)
    {
        if (movements.Count == 0)
        {
            return [];
        }

        List<int> athleteIds = movements.Select(m => m.SaveAthleteId).Distinct().ToList();
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => athleteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> leagues = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        return movements
            .OrderBy(m => m.Kind)
            .ThenBy(m => m.FromSeasonRank)
            .ThenBy(m => m.SaveAthleteId)
            .Select(m => new PostseasonMovementEntry(
                m.SaveAthleteId,
                names.GetValueOrDefault(m.SaveAthleteId, $"Athlete {m.SaveAthleteId}"),
                ((MovementKind)m.Kind).ToString(),
                m.FromLeagueId,
                leagues.GetValueOrDefault(m.FromLeagueId, m.FromLeagueId == 0 ? "Common pool" : $"League {m.FromLeagueId}"),
                m.ToLeagueId,
                leagues.GetValueOrDefault(m.ToLeagueId, m.ToLeagueId == 0 ? "Common pool" : $"League {m.ToLeagueId}"),
                m.FromSeasonRank,
                ((SimulationKernel.Catalog.SportingColor)m.SportingColor).ToString()))
            .ToList();
    }

    internal static async Task<IReadOnlyList<PostseasonQualifierEntry>> MapQualifiersAsync(
        SaveDbContext context,
        List<QualifierStandingEntity> qualifiers,
        CancellationToken cancellationToken)
    {
        if (qualifiers.Count == 0)
        {
            return [];
        }

        List<int> athleteIds = qualifiers.Select(q => q.SaveAthleteId).Distinct().ToList();
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => athleteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> leagues = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        return qualifiers
            .OrderBy(q => q.QualifierBoundary)
            .ThenBy(q => q.QualifierSportingColor)
            .ThenBy(q => q.QualifierRank)
            .Select(q => new PostseasonQualifierEntry(
                q.SaveAthleteId,
                names.GetValueOrDefault(q.SaveAthleteId, $"Athlete {q.SaveAthleteId}"),
                q.QualifierBoundary,
                ((SimulationKernel.Leagues.QualifierBoundary)q.QualifierBoundary).ToString(),
                q.QualifierSportingColor,
                q.QualifierRank,
                q.QualifierScoreThousandths,
                q.BaseScoreThousandths,
                q.IsQualified,
                ((QualifierRole)q.Role).ToString(),
                q.FromLeagueId,
                leagues.GetValueOrDefault(q.FromLeagueId, $"League {q.FromLeagueId}"),
                q.FromSeasonRank))
            .ToList();
    }
}
