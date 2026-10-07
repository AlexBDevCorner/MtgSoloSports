using System.Text.Json;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

public sealed partial class SelectTypeCupTeamsHandler
{
    internal sealed record SelectionInputs(
        List<SaveAthleteEntity> ActiveAthletes,
        Dictionary<int, int> SeasonNumbers,
        List<StageStandingEntity> StageRows,
        Dictionary<int, int> PerformanceByAthlete,
        List<HonourEntity> Honours,
        List<SeasonMembershipEntity> Memberships,
        Dictionary<int, int> LeagueKinds,
        Dictionary<int, int> LeagueLevels,
        int SourceSeasonId,
        int SourceSeasonNumber,
        Dictionary<int, int> SourceLeagueIdByAthlete,
        Dictionary<int, int> SourceLeagueLevels,
        Dictionary<int, string> SourceLeagueNames);

    internal static List<TypeCupAllocation.CandidateRaw> BuildCandidates(SelectionInputs inputs, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<int, List<BonusContribution>> contributions = GroupContributions(inputs);
        Dictionary<int, int> bonusByAthlete = ComputeBonusByAthlete(inputs, contributions, rules);
        Dictionary<int, List<ColorCupSelection.StageFormEntry>> stagesByAthlete = GroupStages(inputs);
        Dictionary<int, CupSelectionMetrics.CupMetrics> metricsByAthlete = BuildMetricsMap(inputs, rules, stagesByAthlete);
        Dictionary<int, CupPrestigeCalculator.PrestigeBreakdown> prestigeByAthlete = BuildPrestigeMap(inputs, rules);
        List<TypeCupAllocation.CandidateRaw> candidates = new(inputs.ActiveAthletes.Count);
        foreach (SaveAthleteEntity athlete in inputs.ActiveAthletes.OrderBy(e => e.Id))
        {
            candidates.Add(BuildSingleCandidate(athlete, inputs, rules, bonusByAthlete, metricsByAthlete, prestigeByAthlete));
        }

        return candidates;
    }

    internal static Dictionary<int, CupSelectionMetrics.CupMetrics> BuildMetricsMap(
        SelectionInputs inputs,
        RulesV1 rules,
        Dictionary<int, List<ColorCupSelection.StageFormEntry>> stagesByAthlete)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(stagesByAthlete);
        Dictionary<int, CupSelectionMetrics.CupMetrics> map = new(inputs.ActiveAthletes.Count);
        foreach (SaveAthleteEntity athlete in inputs.ActiveAthletes)
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
        Dictionary<int, int> map = new(inputs.ActiveAthletes.Count);
        foreach (SaveAthleteEntity athlete in inputs.ActiveAthletes)
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

    internal static TypeCupAllocation.CandidateRaw BuildSingleCandidate(
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
            throw new InvalidOperationException($"Type Cup selection is missing metrics for athlete {athlete.Id}.");
        }

        int performanceRaw = metrics.AdjustedPerformanceThousandths;
        int formRaw = metrics.AdjustedFormRaw;
        int prestigeRaw = prestigeByAthlete.TryGetValue(athlete.Id, out CupPrestigeCalculator.PrestigeBreakdown? breakdown)
            ? breakdown.TotalRaw
            : 0;
        return new TypeCupAllocation.CandidateRaw(
            athlete.Id,
            athlete.Name,
            ParsePrintedTypes(athlete),
            NormalizeNationality(athlete.TypeCupNationality),
            bonusRaw,
            performanceRaw,
            formRaw,
            prestigeRaw);
    }

    internal static IReadOnlyList<string> ParsePrintedTypes(SaveAthleteEntity athlete)
    {
        ArgumentNullException.ThrowIfNull(athlete);
        List<string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<string>>(athlete.CreatureTypesJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Athlete '{athlete.Name}' has corrupt creature types.", ex);
        }

        if (parsed is null)
        {
            return [];
        }

        SortedSet<string> distinct = new(StringComparer.Ordinal);
        foreach (string entry in parsed)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            distinct.Add(entry.Trim());
        }

        return [.. distinct];
    }

    internal static string? NormalizeNationality(string? nationality)
    {
        if (string.IsNullOrWhiteSpace(nationality))
        {
            return null;
        }

        return nationality.Trim();
    }
}
