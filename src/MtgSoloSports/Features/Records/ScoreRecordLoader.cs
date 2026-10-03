using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Records;

/// <summary>
/// Loads scoring-record inputs from authoritative persisted results only.
/// League round and Cup/Qualifier round maxima decode the immutable compact
/// round payloads (the same bytes used by exact-round replay); stage and
/// season totals come from normalized standings without payloads. No RNG,
/// clock, mutation or resimulation. Corrupt references abort.
/// Historical ownership is by persisted athlete id plus persisted
/// season/league/stage/round/group identity, never by current membership,
/// so later league/team changes cannot rewrite record ownership.
/// </summary>
public static class ScoreRecordLoader
{
    internal sealed record ScoringInputs(
        Dictionary<int, string> AthleteNames,
        List<ScoreRecordCalculator.LeagueRoundOccurrence> LeagueRounds,
        List<ScoreRecordCalculator.LeagueStageOccurrence> LeagueStages,
        List<ScoreRecordCalculator.LeaguePointsOccurrence> LeaguePoints,
        List<ScoreRecordCalculator.CupIndividualRoundOccurrence> ColourRounds,
        List<ScoreRecordCalculator.CupIndividualStageOccurrence> ColourStages,
        List<ScoreRecordCalculator.QualifierRoundOccurrence> QualifierRounds,
        List<ScoreRecordCalculator.QualifierStageOccurrence> QualifierStages,
        List<ScoreRecordCalculator.TeamLegRoundOccurrence> ColourLegRounds,
        List<ScoreRecordCalculator.TeamLegStageOccurrence> ColourLegStages,
        List<ScoreRecordCalculator.TeamTotalOccurrence> ColourTotals,
        List<ScoreRecordCalculator.TeamRoundOccurrence> ColourTeamRounds,
        List<ScoreRecordCalculator.TeamLegRoundOccurrence> TypeLegRounds,
        List<ScoreRecordCalculator.TeamLegStageOccurrence> TypeLegStages,
        List<ScoreRecordCalculator.TeamTotalOccurrence> TypeTotals,
        List<ScoreRecordCalculator.TeamRoundOccurrence> TypeTeamRounds);

    internal static async Task<ScoringInputs> LoadAsync(
        SaveDbContext context,
        Dictionary<int, string> athleteNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(athleteNames);
        BaseMaps maps = await LoadBaseMapsAsync(context, cancellationToken).ConfigureAwait(false);
        LeagueInputs league = await LoadLeagueInputsAsync(context, maps, cancellationToken).ConfigureAwait(false);
        CupInputs cups = await LoadCupInputsAsync(context, maps, cancellationToken).ConfigureAwait(false);
        return new ScoringInputs(
            athleteNames,
            league.Rounds,
            league.Stages,
            league.Points,
            cups.ColourRounds,
            cups.ColourStages,
            cups.QualifierRounds,
            cups.QualifierStages,
            cups.ColourLegRounds,
            cups.ColourLegStages,
            cups.ColourTotals,
            cups.ColourTeamRounds,
            cups.TypeLegRounds,
            cups.TypeLegStages,
            cups.TypeTotals,
            cups.TypeTeamRounds);
    }

    internal sealed record BaseMaps(
        Dictionary<int, int> SeasonNumbers,
        Dictionary<int, LeagueInfo> Leagues,
        Dictionary<int, int> AthleteColors);

    internal sealed record LeagueInputs(
        List<ScoreRecordCalculator.LeagueRoundOccurrence> Rounds,
        List<ScoreRecordCalculator.LeagueStageOccurrence> Stages,
        List<ScoreRecordCalculator.LeaguePointsOccurrence> Points);

    internal sealed record CupInputs(
        List<ScoreRecordCalculator.CupIndividualRoundOccurrence> ColourRounds,
        List<ScoreRecordCalculator.CupIndividualStageOccurrence> ColourStages,
        List<ScoreRecordCalculator.QualifierRoundOccurrence> QualifierRounds,
        List<ScoreRecordCalculator.QualifierStageOccurrence> QualifierStages,
        List<ScoreRecordCalculator.TeamLegRoundOccurrence> ColourLegRounds,
        List<ScoreRecordCalculator.TeamLegStageOccurrence> ColourLegStages,
        List<ScoreRecordCalculator.TeamTotalOccurrence> ColourTotals,
        List<ScoreRecordCalculator.TeamRoundOccurrence> ColourTeamRounds,
        List<ScoreRecordCalculator.TeamLegRoundOccurrence> TypeLegRounds,
        List<ScoreRecordCalculator.TeamLegStageOccurrence> TypeLegStages,
        List<ScoreRecordCalculator.TeamTotalOccurrence> TypeTotals,
        List<ScoreRecordCalculator.TeamRoundOccurrence> TypeTeamRounds);

    internal static async Task<BaseMaps> LoadBaseMapsAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        Dictionary<int, int> seasonNumbers = await context.Seasons
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeagueInfo> leagues = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(
                e => e.Id,
                e => new LeagueInfo(e.Kind, e.SportingColor, e.Name),
                cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> athleteColors = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SportingColor, cancellationToken)
            .ConfigureAwait(false);
        return new BaseMaps(seasonNumbers, leagues, athleteColors);
    }

    internal static async Task<LeagueInputs> LoadLeagueInputsAsync(
        SaveDbContext context,
        BaseMaps maps,
        CancellationToken cancellationToken)
    {
        List<ScoreRecordCalculator.LeagueStageOccurrence> stages = await LoadLeagueStagesAsync(
            context, maps.SeasonNumbers, maps.Leagues, cancellationToken).ConfigureAwait(false);
        List<ScoreRecordCalculator.LeaguePointsOccurrence> points = await LoadLeaguePointsAsync(
            context, maps.SeasonNumbers, maps.Leagues, cancellationToken).ConfigureAwait(false);
        List<ScoreRecordCalculator.LeagueRoundOccurrence> rounds = await LoadLeagueRoundsAsync(
            context, maps.SeasonNumbers, maps.Leagues, cancellationToken).ConfigureAwait(false);
        return new LeagueInputs(rounds, stages, points);
    }

    internal static async Task<CupInputs> LoadCupInputsAsync(
        SaveDbContext context,
        BaseMaps maps,
        CancellationToken cancellationToken)
    {
        List<ScoreRecordCalculator.CupIndividualRoundOccurrence> colourRounds = await LoadColourIndividualRoundsAsync(
            context, cancellationToken).ConfigureAwait(false);
        List<ScoreRecordCalculator.CupIndividualStageOccurrence> colourStages = await LoadColourIndividualStagesAsync(
            context, cancellationToken).ConfigureAwait(false);
        List<ScoreRecordCalculator.QualifierRoundOccurrence> qualifierRounds = await LoadQualifierRoundsAsync(
            context, maps.SeasonNumbers, cancellationToken).ConfigureAwait(false);
        List<ScoreRecordCalculator.QualifierStageOccurrence> qualifierStages = await LoadQualifierStagesAsync(
            context, maps.SeasonNumbers, cancellationToken).ConfigureAwait(false);
        (List<ScoreRecordCalculator.TeamLegRoundOccurrence> ColourLegRounds,
            List<ScoreRecordCalculator.TeamRoundOccurrence> ColourTeamRounds) colourTeam =
            await LoadColourTeamRoundsAsync(context, maps.AthleteColors, cancellationToken).ConfigureAwait(false);
        List<ScoreRecordCalculator.TeamLegStageOccurrence> colourLegStages = await LoadColourLegStagesAsync(
            context, cancellationToken).ConfigureAwait(false);
        List<ScoreRecordCalculator.TeamTotalOccurrence> colourTotals = await LoadColourTotalsAsync(
            context, cancellationToken).ConfigureAwait(false);
        Dictionary<(int SeasonId, int AthleteId), string> typeTeams = await LoadTypeTeamMapAsync(
            context, cancellationToken).ConfigureAwait(false);
        (List<ScoreRecordCalculator.TeamLegRoundOccurrence> TypeLegRounds,
            List<ScoreRecordCalculator.TeamRoundOccurrence> TypeTeamRounds) typeTeam =
            await LoadTypeTeamRoundsAsync(context, typeTeams, cancellationToken).ConfigureAwait(false);
        List<ScoreRecordCalculator.TeamLegStageOccurrence> typeLegStages = await LoadTypeLegStagesAsync(
            context, cancellationToken).ConfigureAwait(false);
        List<ScoreRecordCalculator.TeamTotalOccurrence> typeTotals = await LoadTypeTotalsAsync(
            context, cancellationToken).ConfigureAwait(false);
        return new CupInputs(
            colourRounds, colourStages, qualifierRounds, qualifierStages,
            colourTeam.ColourLegRounds, colourLegStages, colourTotals, colourTeam.ColourTeamRounds,
            typeTeam.TypeLegRounds, typeLegStages, typeTotals, typeTeam.TypeTeamRounds);
    }

    internal sealed record LeagueInfo(int Kind, int SportingColor, string Name);

    internal static async Task<List<ScoreRecordCalculator.LeagueStageOccurrence>> LoadLeagueStagesAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        Dictionary<int, LeagueInfo> leagues,
        CancellationToken cancellationToken)
    {
        List<StageProbe> rows = await context.StageStandings
            .AsNoTracking()
            .Select(e => new StageProbe(e.SeasonId, e.LeagueId, e.StageNumber, e.SaveAthleteId, e.StageScoreThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.LeagueStageOccurrence> occurrences = new(rows.Count);
        foreach (StageProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Stage standing references unknown season {row.SeasonId}.");
            }

            if (!leagues.TryGetValue(row.LeagueId, out LeagueInfo? league))
            {
                throw new InvalidOperationException($"Stage standing references unknown league {row.LeagueId}.");
            }

            string scope = ScoreRecordKey.LeagueScopeFor(league.Kind, league.SportingColor);
            occurrences.Add(new ScoreRecordCalculator.LeagueStageOccurrence(
                scope, seasonNumber, row.StageNumber, row.SaveAthleteId, row.StageScoreThousandths, league.Name));
        }

        return occurrences;
    }

    internal static async Task<List<ScoreRecordCalculator.LeaguePointsOccurrence>> LoadLeaguePointsAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        Dictionary<int, LeagueInfo> leagues,
        CancellationToken cancellationToken)
    {
        List<SeasonPointsProbe> rows = await context.SeasonStandings
            .AsNoTracking()
            .Select(e => new SeasonPointsProbe(e.SeasonId, e.LeagueId, e.SaveAthleteId, e.TotalChampionshipPointsThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.LeaguePointsOccurrence> occurrences = new(rows.Count);
        foreach (SeasonPointsProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Season standing references unknown season {row.SeasonId}.");
            }

            if (!leagues.TryGetValue(row.LeagueId, out LeagueInfo? league))
            {
                throw new InvalidOperationException($"Season standing references unknown league {row.LeagueId}.");
            }

            string scope = ScoreRecordKey.LeagueScopeFor(league.Kind, league.SportingColor);
            occurrences.Add(new ScoreRecordCalculator.LeaguePointsOccurrence(
                scope, seasonNumber, row.SaveAthleteId, row.Points, league.Name));
        }

        return occurrences;
    }

    internal static async Task<List<ScoreRecordCalculator.LeagueRoundOccurrence>> LoadLeagueRoundsAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        Dictionary<int, LeagueInfo> leagues,
        CancellationToken cancellationToken)
    {
        List<RoundProbe> rows = await context.Rounds
            .AsNoTracking()
            .Select(e => new RoundProbe(e.SeasonId, e.LeagueId, e.StageNumber, e.RoundNumber, e.PayloadJson))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.LeagueRoundOccurrence> occurrences = [];
        foreach (RoundProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Round references unknown season {row.SeasonId}.");
            }

            if (!leagues.TryGetValue(row.LeagueId, out LeagueInfo? league))
            {
                throw new InvalidOperationException($"Round references unknown league {row.LeagueId}.");
            }

            string scope = ScoreRecordKey.LeagueScopeFor(league.Kind, league.SportingColor);
            RoundPayloadDocument document;
            try
            {
                document = RoundPayloadCodec.DecodeRound(row.PayloadJson);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"League round {row.StageNumber}/{row.RoundNumber} payload is corrupt.", ex);
            }

            foreach (RoundPayloadEntry placement in document.Placements)
            {
                occurrences.Add(new ScoreRecordCalculator.LeagueRoundOccurrence(
                    scope,
                    seasonNumber,
                    row.StageNumber,
                    row.RoundNumber,
                    placement.AthleteId,
                    placement.FinalThousandths,
                    league.Name));
            }
        }

        return occurrences;
    }

    internal static async Task<List<ScoreRecordCalculator.CupIndividualRoundOccurrence>> LoadColourIndividualRoundsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<CupRoundProbe> rows = await context.ColorCupIndividualRounds
            .AsNoTracking()
            .Select(e => new CupRoundProbe(e.SourceSeasonNumber, e.RoundNumber, e.PayloadJson))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.CupIndividualRoundOccurrence> occurrences = [];
        foreach (CupRoundProbe row in rows)
        {
            ColorCupIndividualRoundPayloadDocument document;
            try
            {
                document = ColorCupIndividualRoundPayloadDocument.FromStored(row.PayloadJson);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Colour Cup individual round {row.RoundNumber} payload is corrupt.", ex);
            }

            foreach (RoundPayloadEntry placement in document.Placements)
            {
                occurrences.Add(new ScoreRecordCalculator.CupIndividualRoundOccurrence(
                    row.SeasonNumber, row.RoundNumber, placement.AthleteId, placement.FinalThousandths));
            }
        }

        return occurrences;
    }

    internal static async Task<List<ScoreRecordCalculator.CupIndividualStageOccurrence>> LoadColourIndividualStagesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.ColorCupIndividualStandings
            .AsNoTracking()
            .Select(e => new ScoreRecordCalculator.CupIndividualStageOccurrence(
                e.SourceSeasonNumber, e.SaveAthleteId, e.CupScoreThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<List<ScoreRecordCalculator.QualifierRoundOccurrence>> LoadQualifierRoundsAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        CancellationToken cancellationToken)
    {
        List<QualifierRoundProbe> rows = await context.QualifierRounds
            .AsNoTracking()
            .Select(e => new QualifierRoundProbe(e.FromSeasonId, e.ToSeasonId, e.RoundNumber, e.PayloadJson))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.QualifierRoundOccurrence> occurrences = [];
        foreach (QualifierRoundProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.FromSeasonId, out int fromNumber))
            {
                throw new InvalidOperationException($"Qualifier round references unknown season {row.FromSeasonId}.");
            }

            if (!seasonNumbers.TryGetValue(row.ToSeasonId, out int toNumber))
            {
                throw new InvalidOperationException($"Qualifier round references unknown season {row.ToSeasonId}.");
            }

            QualifierRoundPayloadDocument document;
            try
            {
                string json = RoundPayloadCodec.DecodeToJson(row.PayloadJson);
                document = QualifierRoundPayloadDocument.FromJson(json);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Qualifier round {row.RoundNumber} payload is corrupt.", ex);
            }

            foreach (RoundPayloadEntry placement in document.Placements)
            {
                occurrences.Add(new ScoreRecordCalculator.QualifierRoundOccurrence(
                    fromNumber, toNumber, row.RoundNumber, placement.AthleteId, placement.FinalThousandths));
            }
        }

        return occurrences;
    }

    internal static async Task<List<ScoreRecordCalculator.QualifierStageOccurrence>> LoadQualifierStagesAsync(
        SaveDbContext context,
        Dictionary<int, int> seasonNumbers,
        CancellationToken cancellationToken)
    {
        List<QualifierStageProbe> rows = await context.QualifierStandings
            .AsNoTracking()
            .Select(e => new QualifierStageProbe(e.FromSeasonId, e.ToSeasonId, e.SaveAthleteId, e.QualifierScoreThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.QualifierStageOccurrence> occurrences = new(rows.Count);
        foreach (QualifierStageProbe row in rows)
        {
            if (!seasonNumbers.TryGetValue(row.FromSeasonId, out int fromNumber))
            {
                throw new InvalidOperationException($"Qualifier standing references unknown season {row.FromSeasonId}.");
            }

            if (!seasonNumbers.TryGetValue(row.ToSeasonId, out int toNumber))
            {
                throw new InvalidOperationException($"Qualifier standing references unknown season {row.ToSeasonId}.");
            }

            occurrences.Add(new ScoreRecordCalculator.QualifierStageOccurrence(
                fromNumber, toNumber, row.SaveAthleteId, row.Score));
        }

        return occurrences;
    }

    internal static async Task<(
        List<ScoreRecordCalculator.TeamLegRoundOccurrence> LegRounds,
        List<ScoreRecordCalculator.TeamRoundOccurrence> TeamRounds)> LoadColourTeamRoundsAsync(
        SaveDbContext context,
        Dictionary<int, int> athleteColors,
        CancellationToken cancellationToken)
    {
        List<TeamRoundProbe> rows = await context.ColorCupTeamRounds
            .AsNoTracking()
            .Select(e => new TeamRoundProbe(e.SourceSeasonNumber, e.GroupNumber, e.RoundNumber, e.PayloadJson))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.TeamLegRoundOccurrence> legRounds = [];
        Dictionary<(int Season, int Round, string Team), (int Sum, HashSet<int> Groups)> teamSums = [];
        foreach (TeamRoundProbe row in rows)
        {
            AccumulateColourTeamRow(row, athleteColors, legRounds, teamSums);
        }

        return (legRounds, BuildCompleteTeamRounds(teamSums));
    }

    private static void AccumulateColourTeamRow(
        TeamRoundProbe row,
        Dictionary<int, int> athleteColors,
        List<ScoreRecordCalculator.TeamLegRoundOccurrence> legRounds,
        Dictionary<(int Season, int Round, string Team), (int Sum, HashSet<int> Groups)> teamSums)
    {
        ColorCupTeamRoundPayloadDocument document;
        try
        {
            document = ColorCupTeamRoundPayloadDocument.FromStored(row.PayloadJson);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Colour Cup team round {row.GroupNumber}/{row.RoundNumber} payload is corrupt.", ex);
        }

        foreach (RoundPayloadEntry placement in document.Placements)
        {
            string teamKey = ResolveColourTeamKey(placement.AthleteId, athleteColors);
            legRounds.Add(new ScoreRecordCalculator.TeamLegRoundOccurrence(
                row.SeasonNumber, row.GroupNumber, row.RoundNumber, placement.AthleteId, teamKey, placement.FinalThousandths));
            (int Season, int Round, string Team) mapKey = (row.SeasonNumber, row.RoundNumber, teamKey);
            if (!teamSums.TryGetValue(mapKey, out (int Sum, HashSet<int> Groups) existing))
            {
                existing = (0, []);
                teamSums[mapKey] = existing;
            }

            existing.Groups.Add(row.GroupNumber);
            teamSums[mapKey] = (checked(existing.Sum + placement.FinalThousandths), existing.Groups);
        }
    }

    internal static string ResolveColourTeamKey(int athleteId, Dictionary<int, int> athleteColors)
    {
        if (!athleteColors.TryGetValue(athleteId, out int color))
        {
            throw new InvalidOperationException($"Colour Cup team round references unknown athlete {athleteId}.");
        }

        if (!Enum.IsDefined(typeof(SportingColor), color))
        {
            throw new InvalidOperationException($"Colour Cup team round references corrupt sporting color {color}.");
        }

        return ((SportingColor)color).ToString();
    }

    internal static List<ScoreRecordCalculator.TeamRoundOccurrence> BuildCompleteTeamRounds(
        Dictionary<(int Season, int Round, string Team), (int Sum, HashSet<int> Groups)> teamSums)
    {
        List<ScoreRecordCalculator.TeamRoundOccurrence> teamRounds = [];
        foreach (((int Season, int Round, string Team) key, (int Sum, HashSet<int> Groups) value) in teamSums)
        {
            // Only complete team rounds (all four rank groups present) count as
            // team single-round totals; partial live progress never inflates or
            // deflates the record.
            if (value.Groups.Count != 4)
            {
                continue;
            }

            teamRounds.Add(new ScoreRecordCalculator.TeamRoundOccurrence(key.Season, key.Round, key.Team, value.Sum));
        }

        return teamRounds;
    }

    internal static async Task<List<ScoreRecordCalculator.TeamLegStageOccurrence>> LoadColourLegStagesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<ColourLegProbe> rows = await context.ColorCupTeamGroupStandings
            .AsNoTracking()
            .Select(e => new ColourLegProbe(e.SourceSeasonNumber, e.GroupNumber, e.SaveAthleteId, e.SportingColor, e.GroupScoreThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.TeamLegStageOccurrence> occurrences = new(rows.Count);
        foreach (ColourLegProbe row in rows)
        {
            if (!Enum.IsDefined(typeof(SportingColor), row.SportingColor))
            {
                throw new InvalidOperationException($"Colour Cup leg standing has corrupt sporting color {row.SportingColor}.");
            }

            string teamKey = ((SportingColor)row.SportingColor).ToString();
            occurrences.Add(new ScoreRecordCalculator.TeamLegStageOccurrence(
                row.SeasonNumber, row.GroupNumber, row.AthleteId, teamKey, row.Score));
        }

        return occurrences;
    }

    internal static async Task<List<ScoreRecordCalculator.TeamTotalOccurrence>> LoadColourTotalsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<ColourTotalProbe> rows = await context.ColorCupTeamStandings
            .AsNoTracking()
            .Select(e => new ColourTotalProbe(e.SourceSeasonNumber, e.SportingColor, e.TeamScoreThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.TeamTotalOccurrence> occurrences = new(rows.Count);
        foreach (ColourTotalProbe row in rows)
        {
            if (!Enum.IsDefined(typeof(SportingColor), row.SportingColor))
            {
                throw new InvalidOperationException($"Colour Cup team standing has corrupt sporting color {row.SportingColor}.");
            }

            string teamKey = ((SportingColor)row.SportingColor).ToString();
            occurrences.Add(new ScoreRecordCalculator.TeamTotalOccurrence(row.SeasonNumber, teamKey, row.Score));
        }

        return occurrences;
    }

    internal static async Task<Dictionary<(int SeasonId, int AthleteId), string>> LoadTypeTeamMapAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<TypeMapProbe> rows = await context.TypeCupSelections
            .AsNoTracking()
            .Select(e => new TypeMapProbe(e.SourceSeasonId, e.SaveAthleteId, e.CreatureType))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<(int SeasonId, int AthleteId), string> map = [];
        foreach (TypeMapProbe row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.CreatureType))
            {
                throw new InvalidOperationException("Type Cup selection has corrupt empty creature type.");
            }

            map[(row.SeasonId, row.AthleteId)] = row.CreatureType;
        }

        return map;
    }

    internal static async Task<(
        List<ScoreRecordCalculator.TeamLegRoundOccurrence> LegRounds,
        List<ScoreRecordCalculator.TeamRoundOccurrence> TeamRounds)> LoadTypeTeamRoundsAsync(
        SaveDbContext context,
        Dictionary<(int SeasonId, int AthleteId), string> typeTeams,
        CancellationToken cancellationToken)
    {
        List<TypeTeamRoundProbe> rows = await context.TypeCupTeamRounds
            .AsNoTracking()
            .Select(e => new TypeTeamRoundProbe(e.SourceSeasonId, e.SourceSeasonNumber, e.GroupNumber, e.RoundNumber, e.PayloadJson))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.TeamLegRoundOccurrence> legRounds = [];
        Dictionary<(int Season, int Round, string Team), (int Sum, HashSet<int> Groups)> teamSums = [];
        foreach (TypeTeamRoundProbe row in rows)
        {
            TypeCupTeamRoundPayloadDocument document;
            try
            {
                document = TypeCupTeamRoundPayloadDocument.FromStored(row.PayloadJson);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Type Cup team round {row.GroupNumber}/{row.RoundNumber} payload is corrupt.", ex);
            }

            foreach (RoundPayloadEntry placement in document.Placements)
            {
                if (!typeTeams.TryGetValue((row.SeasonId, placement.AthleteId), out string? teamKey) ||
                    string.IsNullOrWhiteSpace(teamKey))
                {
                    throw new InvalidOperationException(
                        $"Type Cup team round references athlete {placement.AthleteId} without a selection for season {row.SeasonNumber}.");
                }

                legRounds.Add(new ScoreRecordCalculator.TeamLegRoundOccurrence(
                    row.SeasonNumber, row.GroupNumber, row.RoundNumber, placement.AthleteId, teamKey, placement.FinalThousandths));
                (int Season, int Round, string Team) mapKey = (row.SeasonNumber, row.RoundNumber, teamKey);
                if (!teamSums.TryGetValue(mapKey, out (int Sum, HashSet<int> Groups) existing))
                {
                    existing = (0, []);
                    teamSums[mapKey] = existing;
                }

                existing.Groups.Add(row.GroupNumber);
                teamSums[mapKey] = (checked(existing.Sum + placement.FinalThousandths), existing.Groups);
            }
        }

        List<ScoreRecordCalculator.TeamRoundOccurrence> teamRounds = [];
        foreach (((int Season, int Round, string Team) key, (int Sum, HashSet<int> Groups) value) in teamSums)
        {
            if (value.Groups.Count != 4)
            {
                continue;
            }

            teamRounds.Add(new ScoreRecordCalculator.TeamRoundOccurrence(key.Season, key.Round, key.Team, value.Sum));
        }

        return (legRounds, teamRounds);
    }

    internal static async Task<List<ScoreRecordCalculator.TeamLegStageOccurrence>> LoadTypeLegStagesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<TypeLegProbe> rows = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Select(e => new TypeLegProbe(e.SourceSeasonNumber, e.GroupNumber, e.SaveAthleteId, e.CreatureType, e.GroupScoreThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.TeamLegStageOccurrence> occurrences = new(rows.Count);
        foreach (TypeLegProbe row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.CreatureType))
            {
                throw new InvalidOperationException("Type Cup leg standing has corrupt empty creature type.");
            }

            occurrences.Add(new ScoreRecordCalculator.TeamLegStageOccurrence(
                row.SeasonNumber, row.GroupNumber, row.AthleteId, row.CreatureType, row.Score));
        }

        return occurrences;
    }

    internal static async Task<List<ScoreRecordCalculator.TeamTotalOccurrence>> LoadTypeTotalsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<TypeTotalProbe> rows = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Select(e => new TypeTotalProbe(e.SourceSeasonNumber, e.CreatureType, e.TeamScoreThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ScoreRecordCalculator.TeamTotalOccurrence> occurrences = new(rows.Count);
        foreach (TypeTotalProbe row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.CreatureType))
            {
                throw new InvalidOperationException("Type Cup team standing has corrupt empty creature type.");
            }

            occurrences.Add(new ScoreRecordCalculator.TeamTotalOccurrence(row.SeasonNumber, row.CreatureType, row.Score));
        }

        return occurrences;
    }

    private sealed record StageProbe(int SeasonId, int LeagueId, int StageNumber, int SaveAthleteId, int StageScoreThousandths);

    private sealed record SeasonPointsProbe(int SeasonId, int LeagueId, int SaveAthleteId, int Points);

    private sealed record RoundProbe(int SeasonId, int LeagueId, int StageNumber, int RoundNumber, string PayloadJson);

    private sealed record CupRoundProbe(int SeasonNumber, int RoundNumber, string PayloadJson);

    private sealed record QualifierRoundProbe(int FromSeasonId, int ToSeasonId, int RoundNumber, string PayloadJson);

    private sealed record QualifierStageProbe(int FromSeasonId, int ToSeasonId, int SaveAthleteId, int Score);

    private sealed record TeamRoundProbe(int SeasonNumber, int GroupNumber, int RoundNumber, string PayloadJson);

    private sealed record ColourLegProbe(int SeasonNumber, int GroupNumber, int AthleteId, int SportingColor, int Score);

    private sealed record ColourTotalProbe(int SeasonNumber, int SportingColor, int Score);

    private sealed record TypeMapProbe(int SeasonId, int AthleteId, string CreatureType);

    private sealed record TypeTeamRoundProbe(int SeasonId, int SeasonNumber, int GroupNumber, int RoundNumber, string PayloadJson);

    private sealed record TypeLegProbe(int SeasonNumber, int GroupNumber, int AthleteId, string CreatureType, int Score);

    private sealed record TypeTotalProbe(int SeasonNumber, string CreatureType, int Score);
}
