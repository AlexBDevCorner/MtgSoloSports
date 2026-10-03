using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

public sealed partial class SelectTypeCupTeamsHandler
{
    internal static TypeCupSelectionReportDocument BuildReport(TypeCupAllocationInsight.Result insight, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(insight);
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
                .Select(r => MapReportCandidate(r, team.CreatureType, selectionRanks, standings, rankByType))
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
        Dictionary<string, Dictionary<int, int>> rankByType)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(creatureType);
        ArgumentNullException.ThrowIfNull(selectionRanks);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(rankByType);
        TypeCupAllocation.ScoredCandidate candidate = ranked.Candidate;
        List<TypeCupSelectionReportDocument.Alternative> alternatives = candidate.EligibleTypes
            .Where(t => !string.Equals(t, creatureType, StringComparison.Ordinal) && standings.ContainsKey(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(t => new TypeCupSelectionReportDocument.Alternative(t, rankByType[t][candidate.AthleteId], standings[t].FieldsTeam))
            .ToList();
        selectionRanks.TryGetValue(candidate.AthleteId, out int selectionRank);
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
            candidate.PrestigeRaw);
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
