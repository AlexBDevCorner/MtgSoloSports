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
/// played so far (no ranking, no tie-break, no RNG). Read-only; never resimulates.
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

        if (await HistoryEventRows.IsCompleteAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false))
        {
            List<HistoryEventTeamRow> final = await LoadFinalAsync(context, season, color, cancellationToken).ConfigureAwait(false);
            return new HistoryEventTeamStandingsResponse(saveId, seasonNumber, eventKey, IsFinal: true, shape.GroupCount, final);
        }

        (int groupsCompleted, List<HistoryEventTeamRow> provisional) =
            await ProjectProvisionalAsync(context, season, eventKey, shape, color, cancellationToken).ConfigureAwait(false);
        return new HistoryEventTeamStandingsResponse(saveId, seasonNumber, eventKey, IsFinal: false, groupsCompleted, provisional);
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

        return (await context.TypeCupTeamStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == season.Id)
                .OrderBy(e => e.TeamRank)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(e => new HistoryEventTeamRow(e.CreatureType, e.TeamRank, e.TeamScoreThousandths))
            .ToList();
    }

    /// <summary>Sums each team's stored round points over every round played so far (display only).</summary>
    internal static async Task<(int GroupsCompleted, List<HistoryEventTeamRow> Teams)> ProjectProvisionalAsync(
        SaveDbContext context,
        SeasonEntity season,
        string eventKey,
        PostseasonEvents.EventShape shape,
        bool color,
        CancellationToken cancellationToken)
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

        List<HistoryEventRows.StoredRound> rows = await HistoryEventRows.LoadAsync(context, season, eventKey, cancellationToken).ConfigureAwait(false);
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
