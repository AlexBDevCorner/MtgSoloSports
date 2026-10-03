using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.GetColorCupTeamHistory;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one Color team's
/// history across every Color Cup it was selected for: squads, team results,
/// group legs and the members' individual-event results. Read-only. Throws
/// <see cref="CupTeamHistoryNotFoundException"/> (404) for a key that is not a
/// sporting-color name or a color that was never selected, and aborts on
/// corrupt squads or legs.
/// </summary>
public sealed class GetColorCupTeamHistoryHandler
{
    private readonly SaveStore _store;

    public GetColorCupTeamHistoryHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<CupTeamHistoryResponse> HandleAsync(
        Guid saveId,
        string teamKey,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (!CupTeamKeys.TryParseColorKey(teamKey, out SportingColor color))
        {
            throw new CupTeamHistoryNotFoundException($"'{teamKey}' is not a Color Cup team.");
        }

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        int colorValue = (int)color;
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);

        List<ColorCupSelectionEntity> selections = await context.ColorCupSelections
            .AsNoTracking().Where(e => e.SportingColor == colorValue).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ColorCupTeamStandingEntity> standings = await context.ColorCupTeamStandings
            .AsNoTracking().Where(e => e.SportingColor == colorValue).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings
            .AsNoTracking().Where(e => e.SportingColor == colorValue).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ColorCupIndividualStandingEntity> individuals = await context.ColorCupIndividualStandings
            .AsNoTracking().Where(e => e.SportingColor == colorValue).ToListAsync(cancellationToken).ConfigureAwait(false);

        List<int> seasons = selections.Select(s => s.SourceSeasonNumber).Distinct().ToList();
        Dictionary<int, CupTeamHistoryBuilder.SeasonFacts> facts =
            await LoadFactsAsync(context, seasons, cancellationToken).ConfigureAwait(false);
        List<int> athleteIds = selections.Select(s => s.SaveAthleteId).Distinct().ToList();
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking().Where(e => athleteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);

        return CupTeamHistoryBuilder.Build(new CupTeamHistoryBuilder.Input(
            saveId,
            CupTeamKeys.ColorCup,
            CupTeamKeys.ColorKey(color),
            CupTeamKeys.ColorName(color),
            rules.ColorCupTeamSize,
            MapSelections(selections),
            MapStandings(standings),
            MapLegs(legs),
            MapIndividuals(individuals),
            facts,
            athletes));
    }

    /// <summary>Whole-edition facts of every season this team was selected for.</summary>
    private static async Task<Dictionary<int, CupTeamHistoryBuilder.SeasonFacts>> LoadFactsAsync(
        SaveDbContext context,
        List<int> seasons,
        CancellationToken cancellationToken)
    {
        List<ColorCupSelectionEntity> fields = await context.ColorCupSelections
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber)).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> teamRoundSeasons = await context.ColorCupTeamRounds
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber))
            .Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> individualRoundSeasons = await context.ColorCupIndividualRounds
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber))
            .Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> individualDoneSeasons = await context.ColorCupIndividualStandings
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber))
            .Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        return seasons.ToDictionary(
            season => season,
            season => new CupTeamHistoryBuilder.SeasonFacts(
                fields.Where(f => f.SourceSeasonNumber == season).Select(f => f.SportingColor).Distinct().Count(),
                teamRoundSeasons.Contains(season) || individualRoundSeasons.Contains(season),
                individualDoneSeasons.Contains(season)));
    }

    private static List<CupTeamHistoryBuilder.SelectionRow> MapSelections(List<ColorCupSelectionEntity> selections)
    {
        return selections.Select(s => new CupTeamHistoryBuilder.SelectionRow(
            s.SourceSeasonNumber,
            s.SaveAthleteId,
            s.SelectionRank,
            s.FinalRatingThousandths,
            s.BonusNormThousandths,
            s.PerformanceNormThousandths,
            s.FormNormThousandths,
            s.PrestigeNormThousandths,
            Reason: null)).ToList();
    }

    private static List<CupTeamHistoryBuilder.StandingRow> MapStandings(List<ColorCupTeamStandingEntity> standings)
    {
        return standings.Select(s => new CupTeamHistoryBuilder.StandingRow(
            s.SourceSeasonNumber,
            s.TeamRank,
            s.TeamScoreThousandths,
            s.TeamBaseThousandths,
            s.GroupWins,
            s.RoundWins,
            ((ColorCupMedal)s.Medal).ToString())).ToList();
    }

    private static List<CupTeamHistoryBuilder.LegRow> MapLegs(List<ColorCupTeamGroupStandingEntity> legs)
    {
        return legs.Select(l => new CupTeamHistoryBuilder.LegRow(
            l.SourceSeasonNumber,
            l.SaveAthleteId,
            l.GroupNumber,
            l.GroupRank,
            l.GroupScoreThousandths,
            l.BaseScoreThousandths,
            l.RoundWins)).ToList();
    }

    private static List<CupTeamHistoryBuilder.IndividualRow> MapIndividuals(List<ColorCupIndividualStandingEntity> individuals)
    {
        return individuals.Select(i => new CupTeamHistoryBuilder.IndividualRow(
            i.SourceSeasonNumber,
            i.SaveAthleteId,
            i.CupRank,
            i.CupScoreThousandths,
            ((ColorCupMedal)i.Medal).ToString())).ToList();
    }
}
