using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared.Careers;
using MtgSoloSports.SimulationKernel.Careers;
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
        int SourceSeasonId,
        int SourceSeasonNumber,
        Dictionary<int, int> SourceLeagueIdByAthlete,
        Dictionary<int, int> SourceLeagueLevels,
        Dictionary<int, string> SourceLeagueNames);

    internal static List<TypeCupAllocation.CandidateRaw> BuildCandidates(
        SelectionInputs inputs,
        RulesV1 rules,
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> careerByAthlete,
        IReadOnlyDictionary<int, int>? chaosByAthlete = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(careerByAthlete);
        Dictionary<int, List<BonusContribution>> contributions = GroupContributions(inputs);
        Dictionary<int, int> bonusByAthlete = ComputeBonusByAthlete(inputs, contributions, rules);
        Dictionary<int, List<ColorCupSelection.StageFormEntry>> stagesByAthlete = GroupStages(inputs);
        Dictionary<int, CupSelectionMetrics.CupMetrics> metricsByAthlete = BuildMetricsMap(inputs, rules, stagesByAthlete);
        IReadOnlyDictionary<int, int> chaos = chaosByAthlete ?? new Dictionary<int, int>();
        List<TypeCupAllocation.CandidateRaw> candidates = new(inputs.ActiveAthletes.Count);
        foreach (SaveAthleteEntity athlete in inputs.ActiveAthletes.OrderBy(e => e.Id))
        {
            candidates.Add(BuildSingleCandidate(athlete, inputs, rules, bonusByAthlete, metricsByAthlete, careerByAthlete, chaos));
        }

        return candidates;
    }

    internal static async Task<Dictionary<int, int>> LoadChaosRawsAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        List<ChaosCupGroupStandingEntity> rows = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        List<ChaosCupGroupStandingEntity> finals = rows.Where(r => r.StageIndex == 5).ToList();
        if (finals.Count != SimulationKernel.Cups.ChaosCupFormat.FinalFieldSize)
        {
            return new Dictionary<int, int>();
        }

        Dictionary<int, List<ChaosCupGroupStandingEntity>> byAthlete = rows
            .GroupBy(r => r.SaveAthleteId)
            .ToDictionary(g => g.Key, g => g.ToList());
        Dictionary<int, int> raws = new(byAthlete.Count);
        foreach ((int athleteId, List<ChaosCupGroupStandingEntity> athleteRows) in byAthlete)
        {
            ChaosCupGroupStandingEntity peak = athleteRows.MaxBy(r => r.StageIndex)!;
            if (peak.StageIndex == 5)
            {
                if (peak.GroupRank < 1 || peak.GroupRank > SimulationKernel.Cups.ChaosCupFormat.FinalFieldSize)
                {
                    throw new InvalidOperationException($"Chaos Cup Final rank {peak.GroupRank} for season {source.SeasonNumber} is corrupt.");
                }

                raws[athleteId] = SimulationKernel.Cups.ChaosSelectionForm.ForFinal(peak.GroupRank);
            }
            else
            {
                if (peak.IsAdvanced)
                {
                    throw new InvalidOperationException($"Chaos Cup season {source.SeasonNumber} athlete {athleteId} is marked advanced at its last stage {peak.StageIndex}.");
                }

                raws[athleteId] = SimulationKernel.Cups.ChaosSelectionForm.ForElimination(peak.StageIndex);
            }
        }

        return raws;
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

    internal static async Task<IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore>> LoadCareerPrestigeAsync(
        SaveDbContext context,
        int sourceSeasonNumber,
        IReadOnlyCollection<int> athleteIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(athleteIds);
        if (sourceSeasonNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSeasonNumber), "Source season must be positive.");
        }

        return await CareerPerformanceLoader.LoadScoresUpToAsync(
            context, sourceSeasonNumber, athleteIds, cancellationToken).ConfigureAwait(false);
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
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> careerByAthlete,
        IReadOnlyDictionary<int, int> chaosByAthlete)
    {
        int bonusRaw = bonusByAthlete.TryGetValue(athlete.Id, out int bonus) ? bonus : 0;
        if (!metricsByAthlete.TryGetValue(athlete.Id, out CupSelectionMetrics.CupMetrics? metrics))
        {
            throw new InvalidOperationException($"Type Cup selection is missing metrics for athlete {athlete.Id}.");
        }

        int performanceRaw = metrics.AdjustedPerformanceThousandths;
        int formRaw = metrics.AdjustedFormRaw;
        chaosByAthlete.TryGetValue(athlete.Id, out int chaosRaw);
        int prestigeRaw = SelectionCareerPrestige.PrestigeRawFor(athlete.Id, careerByAthlete);
        return new TypeCupAllocation.CandidateRaw(
            athlete.Id,
            athlete.Name,
            ParsePrintedTypes(athlete),
            NormalizeNationality(athlete.TypeCupNationality),
            bonusRaw,
            performanceRaw,
            formRaw,
            chaosRaw,
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
