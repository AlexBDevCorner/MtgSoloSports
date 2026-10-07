using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

public sealed partial class SelectColorCupTeamsHandler
{
    internal static Dictionary<int, List<ColorCupSelection.CandidateRaw>> BuildCandidates(
        SelectionInputs inputs,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<int, List<BonusContribution>> contributions = GroupContributions(inputs);
        Dictionary<int, int> bonusByAthlete = ComputeBonusByAthlete(inputs, contributions, rules);
        Dictionary<int, List<ColorCupSelection.StageFormEntry>> stagesByAthlete = GroupStages(inputs);
        Dictionary<int, CupSelectionMetrics.CupMetrics> metricsByAthlete = BuildMetricsMap(inputs, rules, stagesByAthlete);
        Dictionary<int, CupPrestigeCalculator.PrestigeBreakdown> prestigeByAthlete = BuildPrestigeMap(inputs, rules);
        return AssembleCandidates(inputs, rules, bonusByAthlete, metricsByAthlete, prestigeByAthlete);
    }

    internal static Dictionary<int, CupSelectionMetrics.CupMetrics> BuildMetricsMap(
        SelectionInputs inputs,
        RulesV1 rules,
        Dictionary<int, List<ColorCupSelection.StageFormEntry>> stagesByAthlete)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(stagesByAthlete);
        Dictionary<int, CupSelectionMetrics.CupMetrics> map = new(inputs.Athletes.Count);
        foreach (SaveAthleteEntity athlete in inputs.Athletes)
        {
            LeagueLevel? level = CupSelectionMetrics.ResolveLevel(
                inputs.SourceLeagueLevels, inputs.SourceLeagueIdByAthlete, athlete.Id, inputs.SourceSeasonNumber);
            inputs.PerformanceByAthlete.TryGetValue(athlete.Id, out int unadjusted);
            bool hasStanding = inputs.PerformanceByAthlete.ContainsKey(athlete.Id);
            stagesByAthlete.TryGetValue(athlete.Id, out List<ColorCupSelection.StageFormEntry>? stages);
            CupSelectionMetrics.CupMetrics metrics = CupSelectionMetrics.Build(
                hasStanding ? unadjusted : (int?)null,
                stages ?? [],
                inputs.SourceSeasonNumber,
                level,
                rules);
            map[athlete.Id] = metrics;
        }

        return map;
    }

    internal static Dictionary<int, CupPrestigeCalculator.PrestigeBreakdown> BuildPrestigeMap(
        SelectionInputs inputs,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        List<CupPrestigeCalculator.PrestigeHonour> honours = new(inputs.Honours.Count);
        foreach (HonourEntity honour in inputs.Honours)
        {
            honours.Add(new CupPrestigeCalculator.PrestigeHonour(honour.SaveAthleteId, honour.Kind, honour.LeagueId));
        }

        List<CupPrestigeCalculator.PrestigeStage> stages = new(inputs.StageRows.Count);
        foreach (StageStandingEntity row in inputs.StageRows)
        {
            stages.Add(new CupPrestigeCalculator.PrestigeStage(row.SaveAthleteId, row.LeagueId, row.StageRank));
        }

        List<CupPrestigeCalculator.PrestigeMembership> memberships = new(inputs.Memberships.Count);
        foreach (SeasonMembershipEntity membership in inputs.Memberships)
        {
            memberships.Add(new CupPrestigeCalculator.PrestigeMembership(membership.SaveAthleteId, membership.LeagueId));
        }

        Dictionary<int, LeagueLevel> levels = new(inputs.LeagueLevels.Count);
        foreach (KeyValuePair<int, int> entry in inputs.LeagueLevels)
        {
            if (!Enum.IsDefined(typeof(LeagueLevel), entry.Value))
            {
                throw new InvalidOperationException($"League {entry.Key} has corrupt level {entry.Value}.");
            }

            levels[entry.Key] = (LeagueLevel)entry.Value;
        }

        return new Dictionary<int, CupPrestigeCalculator.PrestigeBreakdown>(
            CupPrestigeCalculator.ComputeAll(honours, stages, memberships, levels, rules));
    }

    internal static Dictionary<int, List<BonusContribution>> GroupContributions(SelectionInputs inputs)
    {
        Dictionary<int, List<BonusContribution>> map = [];
        foreach (StageStandingEntity row in inputs.StageRows)
        {
            if (!inputs.SeasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Stage standing {row.Id} references unknown season {row.SeasonId}.");
            }

            if (!map.TryGetValue(row.SaveAthleteId, out List<BonusContribution>? list))
            {
                list = [];
                map[row.SaveAthleteId] = list;
            }

            list.Add(new BonusContribution(
                seasonNumber,
                row.StageNumber,
                SimulationKernel.FixedPoint.Bonus.FromThousandths(row.EarnedBonusThousandths)));
        }

        return map;
    }

    internal static Dictionary<int, int> ComputeBonusByAthlete(
        SelectionInputs inputs,
        Dictionary<int, List<BonusContribution>> contributions,
        RulesV1 rules)
    {
        int cupSeason = inputs.SeasonNumbers.Values.Max() + 1;
        Dictionary<int, int> map = new(inputs.Athletes.Count);
        foreach (SaveAthleteEntity athlete in inputs.Athletes)
        {
            contributions.TryGetValue(athlete.Id, out List<BonusContribution>? list);
            IReadOnlyList<BonusContribution> empty = list ?? [];
            SimulationKernel.FixedPoint.Bonus effective = BonusCalculator.EffectiveBonus(empty, cupSeason, 1, rules);
            map[athlete.Id] = effective.Thousandths;
        }

        return map;
    }

    internal static Dictionary<int, List<ColorCupSelection.StageFormEntry>> GroupStages(SelectionInputs inputs)
    {
        Dictionary<int, List<ColorCupSelection.StageFormEntry>> map = [];
        foreach (StageStandingEntity row in inputs.StageRows)
        {
            if (!inputs.SeasonNumbers.TryGetValue(row.SeasonId, out int seasonNumber))
            {
                throw new InvalidOperationException($"Stage standing {row.Id} references unknown season {row.SeasonId}.");
            }

            if (!map.TryGetValue(row.SaveAthleteId, out List<ColorCupSelection.StageFormEntry>? list))
            {
                list = [];
                map[row.SaveAthleteId] = list;
            }

            list.Add(new ColorCupSelection.StageFormEntry(seasonNumber, row.StageNumber, row.ChampionshipPointsThousandths));
        }

        return map;
    }

    internal static Dictionary<int, List<ColorCupSelection.CandidateRaw>> AssembleCandidates(
        SelectionInputs inputs,
        RulesV1 rules,
        Dictionary<int, int> bonusByAthlete,
        Dictionary<int, CupSelectionMetrics.CupMetrics> metricsByAthlete,
        Dictionary<int, CupPrestigeCalculator.PrestigeBreakdown> prestigeByAthlete)
    {
        Dictionary<int, List<ColorCupSelection.CandidateRaw>> byColor = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            byColor[(int)color] = [];
        }

        foreach (SaveAthleteEntity athlete in inputs.Athletes)
        {
            ColorCupSelection.CandidateRaw candidate = BuildSingleCandidate(
                athlete, inputs, rules, bonusByAthlete, metricsByAthlete, prestigeByAthlete);
            byColor[athlete.SportingColor].Add(candidate);
        }

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            if (byColor[(int)color].Count == 0)
            {
                throw new InvalidOperationException($"Sporting color {color} has no candidates.");
            }
        }

        return byColor;
    }

    internal static ColorCupSelection.CandidateRaw BuildSingleCandidate(
        SaveAthleteEntity athlete,
        SelectionInputs inputs,
        RulesV1 rules,
        Dictionary<int, int> bonusByAthlete,
        Dictionary<int, CupSelectionMetrics.CupMetrics> metricsByAthlete,
        Dictionary<int, CupPrestigeCalculator.PrestigeBreakdown> prestigeByAthlete)
    {
        int bonusRaw = bonusByAthlete.TryGetValue(athlete.Id, out int bonus) ? bonus : 0;
        if (!metricsByAthlete.TryGetValue(athlete.Id, out CupSelectionMetrics.CupMetrics? metrics))
        {
            throw new InvalidOperationException($"Color Cup selection is missing metrics for athlete {athlete.Id}.");
        }

        int performanceRaw = metrics.AdjustedPerformanceThousandths;
        int formRaw = metrics.AdjustedFormRaw;
        int prestigeRaw = prestigeByAthlete.TryGetValue(athlete.Id, out CupPrestigeCalculator.PrestigeBreakdown? breakdown)
            ? breakdown.TotalRaw
            : 0;
        return new ColorCupSelection.CandidateRaw(
            athlete.Id, athlete.Name, athlete.SportingColor, bonusRaw, performanceRaw, formRaw, prestigeRaw);
    }
}
