using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.History.GetEventTeamStandings;

/// <summary>
/// Endpoint -&gt; Handler direct call. Team standings of a team Cup event: the
/// persisted final ranking once the event is complete, otherwise a provisional
/// display projection summing each team's stored round points over every round
/// played so far (no ranking, no tie-break, no RNG). With a before-round cursor
/// the projection covers only the rounds played ahead of that round, so a round
/// reveal can add its own stored points on top. Read-only; never resimulates.
/// </summary>
public sealed class GetHistoryEventTeamStandingsHandler
{
    private readonly SaveStore _store;

    public GetHistoryEventTeamStandingsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<HistoryEventTeamStandingsResponse> HandleAsync(
        Guid saveId,
        int seasonNumber,
        string eventKey,
        int? beforeGroup = null,
        int? beforeRound = null,
        CancellationToken cancellationToken = default)
    {
        HistoryEventRows.ValidateRequest(saveId, seasonNumber, eventKey);
        if (!string.Equals(eventKey, PostseasonEvents.ColorCupTeam, StringComparison.Ordinal)
            && !string.Equals(eventKey, PostseasonEvents.TypeCupTeam, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{PostseasonEvents.Title(eventKey)} has no team standings.", nameof(eventKey));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        PostseasonEvents.EventShape shape = PostseasonEvents.Shape(eventKey, rules);
        SeasonEntity season = await HistoryEventRows.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        bool color = string.Equals(eventKey, PostseasonEvents.ColorCupTeam, StringComparison.Ordinal);
        (int Group, int Round)? before = ValidateBefore(beforeGroup, beforeRound, shape);
        Dictionary<int, string> teamByAthlete = await LoadTeamByAthleteAsync(context, season, eventKey, color, cancellationToken).ConfigureAwait(false);
        List<HistoryEventTeamMember> members = teamByAthlete
            .OrderBy(pair => pair.Key)
            .Select(pair => new HistoryEventTeamMember(pair.Key, pair.Value))
            .ToList();

        if (before is null
            && await HistoryEventRows.IsCompleteAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false))
        {
            List<HistoryEventTeamRow> final = await LoadFinalAsync(context, season, color, cancellationToken).ConfigureAwait(false);
            return new HistoryEventTeamStandingsResponse(saveId, seasonNumber, eventKey, IsFinal: true, shape.GroupCount, final, members);
        }

        (int groupsCompleted, List<HistoryEventTeamRow> provisional) =
            await ProjectProvisionalAsync(context, season, eventKey, shape, teamByAthlete, before, cancellationToken).ConfigureAwait(false);
        return new HistoryEventTeamStandingsResponse(saveId, seasonNumber, eventKey, IsFinal: false, groupsCompleted, provisional, members);
    }

    internal static async Task<List<HistoryEventTeamRow>> LoadFinalAsync(
        SaveDbContext context, SeasonEntity season, bool color, CancellationToken cancellationToken)
    {
        if (color)
        {
            return (await context.ColorCupTeamStandings.AsNoTracking()
                    .Where(e => e.SourceSeasonId == season.Id)
                    .OrderBy(e => e.TeamRank)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(e => new HistoryEventTeamRow(((SportingColor)e.SportingColor).ToString(), e.TeamRank, e.TeamScoreThousandths))
                .ToList();
        }

        List<TypeCupTeamStandingEntity> rows = await context.TypeCupTeamStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == season.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        bool hasLegacy = rows.Any(e => e.TournamentPhase == (int)MtgSoloSports.SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField);
        IEnumerable<TypeCupTeamStandingEntity> official = hasLegacy
            ? rows.OrderBy(e => e.TeamRank)
            : rows.Where(e => e.TournamentPhase == (int)MtgSoloSports.SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final).OrderBy(e => e.TeamRank);
        return official
            .Select(e => new HistoryEventTeamRow(e.CreatureType, e.TeamRank, e.TeamScoreThousandths))
            .ToList();
    }

    private static (int Group, int Round)? ValidateBefore(int? beforeGroup, int? beforeRound, PostseasonEvents.EventShape shape)
    {
        if (beforeGroup is null && beforeRound is null)
        {
            return null;
        }

        if (beforeGroup is not int group || beforeRound is not int round)
        {
            throw new ArgumentException("A before-round cursor needs both a group and a round.", nameof(beforeRound));
        }

        if (group < 1 || group > shape.GroupCount || round < 1 || round > shape.RoundsPerGroup)
        {
            throw new ArgumentException($"Group {group} round {round} is outside the event.", nameof(beforeRound));
        }

        return (group, round);
    }

    private static async Task<Dictionary<int, string>> LoadTeamByAthleteAsync(
        SaveDbContext context, SeasonEntity season, string eventKey, bool color, CancellationToken cancellationToken)
    {
        Dictionary<int, string> teamByAthlete = color
            ? (await context.ColorCupSelections.AsNoTracking()
                    .Where(e => e.SourceSeasonId == season.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(e => e.SaveAthleteId, e => ((SportingColor)e.SportingColor).ToString())
            : (await context.TypeCupSelections.AsNoTracking()
                    .Where(e => e.SourceSeasonId == season.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(e => e.SaveAthleteId, e => e.CreatureType);
        if (teamByAthlete.Count == 0)
        {
            throw new HistoryNotFoundException($"{PostseasonEvents.Title(eventKey)} has no selected field for Season {season.SeasonNumber}.");
        }

        return teamByAthlete;
    }

    /// <summary>Sums each team's stored round points over every round played so far, or ahead of <paramref name="before"/> (display only).</summary>
    internal static async Task<(int GroupsCompleted, List<HistoryEventTeamRow> Teams)> ProjectProvisionalAsync(
        SaveDbContext context,
        SeasonEntity season,
        string eventKey,
        PostseasonEvents.EventShape shape,
        Dictionary<int, string> teamByAthlete,
        (int Group, int Round)? before,
        CancellationToken cancellationToken)
    {
        List<HistoryEventRows.StoredRound> rows = await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false);
        if (before is (int beforeGroup, int beforeRound))
        {
            rows = rows
                .Where(row => row.Group < beforeGroup || (row.Group == beforeGroup && row.Round < beforeRound))
                .ToList();
        }

        int groupsCompleted = rows.Count / shape.RoundsPerGroup;
        Dictionary<string, long> totals = teamByAthlete.Values.Distinct(StringComparer.Ordinal).ToDictionary(team => team, _ => 0L, StringComparer.Ordinal);
        foreach (HistoryEventRows.StoredRound row in rows)
        {
            foreach (RoundPayloadEntry placement in HistoryEventRows.DecodePlacements(eventKey, row.PayloadJson))
            {
                if (!teamByAthlete.TryGetValue(placement.AthleteId, out string? team))
                {
                    throw new InvalidOperationException($"{PostseasonEvents.Title(eventKey)} round references athlete {placement.AthleteId} outside the selected field.");
                }

                totals[team] += placement.FinalThousandths;
            }
        }

        List<HistoryEventTeamRow> teams = totals
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new HistoryEventTeamRow(pair.Key, Rank: null, checked((int)pair.Value)))
            .ToList();
        return (groupsCompleted, teams);
    }
}
