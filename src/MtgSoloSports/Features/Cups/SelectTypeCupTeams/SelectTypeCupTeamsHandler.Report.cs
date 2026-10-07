using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

public sealed partial class SelectTypeCupTeamsHandler
{
    internal static TypeCupSelectionReportDocument BuildReport(
        TypeCupAllocationInsight.Result insight,
        Dictionary<int, CupSelectionMetrics.CupMetrics> metricsByAthlete,
        Dictionary<int, CupPrestigeCalculator.PrestigeBreakdown> prestigeByAthlete,
        SelectionInputs inputs,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(insight);
        ArgumentNullException.ThrowIfNull(metricsByAthlete);
        ArgumentNullException.ThrowIfNull(prestigeByAthlete);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<string, TypeCupAllocationInsight.TypeStanding> standings =
            insight.Types.ToDictionary(t => t.CreatureType, StringComparer.Ordinal);
        Dictionary<string, Dictionary<int, int>> rankByType = insight.Types.ToDictionary(
            t => t.CreatureType,
            t => t.Ranking.ToDictionary(r => r.Candidate.AthleteId, r => r.TypeRank),
            StringComparer.Ordinal);

        List<TypeCupSelectionReportDocument.Team> teams = new(insight.Allocation.Teams.Count);
        foreach (TypeCupAllocation.AllocatedTeam team in insight.Allocation.Teams)
        {
            TypeCupAllocationInsight.TypeStanding standing = standings[team.CreatureType];
            Dictionary<int, int> selectionRanks = team.Members.ToDictionary(m => m.AthleteId, m => m.SelectionRank);
            List<TypeCupSelectionReportDocument.Candidate> ranking = standing.Ranking
                .Where(r => r.TypeRank <= TypeCupSelectionReportDocument.ShortlistSize || selectionRanks.ContainsKey(r.Candidate.AthleteId))
                .Select(r => MapReportCandidate(r, team.CreatureType, selectionRanks, standings, rankByType, metricsByAthlete, prestigeByAthlete, inputs))
                .ToList();
            teams.Add(new TypeCupSelectionReportDocument.Team(team.CreatureType, standing.Ranking.Count, ranking));
        }

        List<TypeCupSelectionReportDocument.MissedType> missed = insight.Types
            .Where(t => !t.FieldsTeam)
            .Select(t => new TypeCupSelectionReportDocument.MissedType(t.CreatureType, t.Ranking.Count))
            .ToList();
        return new TypeCupSelectionReportDocument(
            TypeCupSelectionReportDocument.PayloadVersion,
            insight.CandidateCount,
            rules.CupBonusWeightPermille,
            rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille,
            rules.CupPrestigeWeightPermille,
            teams,
            missed);
    }

    internal static TypeCupSelectionReportDocument.Candidate MapReportCandidate(
        TypeCupAllocationInsight.RankedCandidate ranked,
        string creatureType,
        Dictionary<int, int> selectionRanks,
        Dictionary<string, TypeCupAllocationInsight.TypeStanding> standings,
        Dictionary<string, Dictionary<int, int>> rankByType,
        Dictionary<int, CupSelectionMetrics.CupMetrics> metricsByAthlete,
        Dictionary<int, CupPrestigeCalculator.PrestigeBreakdown> prestigeByAthlete,
        SelectionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(creatureType);
        ArgumentNullException.ThrowIfNull(selectionRanks);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(rankByType);
        ArgumentNullException.ThrowIfNull(metricsByAthlete);
        ArgumentNullException.ThrowIfNull(prestigeByAthlete);
        ArgumentNullException.ThrowIfNull(inputs);
        TypeCupAllocation.ScoredCandidate candidate = ranked.Candidate;
        List<TypeCupSelectionReportDocument.Alternative> alternatives = BuildAlternatives(candidate, creatureType, standings, rankByType);
        selectionRanks.TryGetValue(candidate.AthleteId, out int selectionRank);
        (string? leagueName, int? leagueLevel, int factor, int unadjustedPerformance, int unadjustedForm) =
            ResolveLeagueExplanation(candidate.AthleteId, metricsByAthlete, inputs);
        prestigeByAthlete.TryGetValue(candidate.AthleteId, out CupPrestigeCalculator.PrestigeBreakdown? prestige);

        return new TypeCupSelectionReportDocument.Candidate(
            candidate.AthleteId,
            ranked.TypeRank,
            selectionRank,
            ranked.AssignedType,
            candidate.CappedNationality is not null,
            alternatives,
            candidate.FinalRatingThousandths,
            candidate.BonusNormThousandths,
            candidate.PerformanceNormThousandths,
            candidate.FormNormThousandths,
            candidate.PrestigeNormThousandths,
            candidate.BonusRawThousandths,
            candidate.PerformanceRawThousandths,
            candidate.FormRaw,
            candidate.PrestigeRaw,
            leagueName,
            leagueLevel,
            factor,
            unadjustedPerformance,
            unadjustedForm,
            prestige?.SuperTitleRaw ?? 0,
            prestige?.Feeder1TitleRaw ?? 0,
            prestige?.Feeder2TitleRaw ?? 0,
            prestige?.Feeder3TitleRaw ?? 0,
            prestige?.AppearanceRaw ?? 0,
            prestige?.SuperStageRaw ?? 0,
            prestige?.Feeder1StageRaw ?? 0,
            prestige?.Feeder2StageRaw ?? 0,
            prestige?.Feeder3StageRaw ?? 0,
            prestige?.MajorCupRaw ?? 0);
    }

    internal static List<TypeCupSelectionReportDocument.Alternative> BuildAlternatives(
        TypeCupAllocation.ScoredCandidate candidate,
        string creatureType,
        Dictionary<string, TypeCupAllocationInsight.TypeStanding> standings,
        Dictionary<string, Dictionary<int, int>> rankByType)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(creatureType);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(rankByType);
        return candidate.EligibleTypes
            .Where(t => !string.Equals(t, creatureType, StringComparison.Ordinal) && standings.ContainsKey(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => new TypeCupSelectionReportDocument.Alternative(t, rankByType[t][candidate.AthleteId], standings[t].FieldsTeam))
            .ToList();
    }

    internal static (string? LeagueName, int? LeagueLevel, int Factor, int UnadjustedPerformance, int UnadjustedForm) ResolveLeagueExplanation(
        int athleteId,
        Dictionary<int, CupSelectionMetrics.CupMetrics> metricsByAthlete,
        SelectionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(metricsByAthlete);
        ArgumentNullException.ThrowIfNull(inputs);
        if (!metricsByAthlete.TryGetValue(athleteId, out CupSelectionMetrics.CupMetrics? metrics))
        {
            return ("Pool", null, 0, 0, 0);
        }

        int? level = metrics.Level.HasValue ? (int)metrics.Level.Value : null;
        string name = "Pool";
        if (metrics.Level.HasValue
            && inputs.SourceLeagueIdByAthlete.TryGetValue(athleteId, out int leagueId)
            && inputs.SourceLeagueNames.TryGetValue(leagueId, out string? resolved))
        {
            name = resolved;
        }

        return (name, level, metrics.StrengthFactorPermille, metrics.UnadjustedPerformanceThousandths, metrics.UnadjustedFormAggregate);
    }

    internal static void AddReport(
        SaveDbContext context,
        SeasonEntity source,
        TypeCupSelectionReportDocument report,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(rules);
        context.CupSelectionReports.Add(new CupSelectionReportEntity
        {
            SourceSeasonId = source.Id,
            SourceSeasonNumber = source.SeasonNumber,
            Cup = CupExtensionPoint.TypeCup,
            RulesVersion = rules.Version,
            PayloadJson = report.ToStored(),
        });
    }
}
