using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
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
        PrestigeCounts counts = CountPrestige(inputs);
        return AssembleCandidates(inputs, rules, bonusByAthlete, stagesByAthlete, counts);
    }

    internal sealed record PrestigeCounts(
        Dictionary<int, int> FeederTitles,
        Dictionary<int, int> SuperTitles,
        Dictionary<int, int> OtherMajors,
        Dictionary<int, int> SuperAppearances,
        Dictionary<int, int> StageWins,
        Dictionary<int, int> StageSeconds,
        Dictionary<int, int> StageThirds);

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

    internal static PrestigeCounts CountPrestige(SelectionInputs inputs)
    {
        Dictionary<int, int> feeder = CountHonours(inputs, (int)Features.Records.HonourKind.FeederTitle, exactKind: true);
        Dictionary<int, int> super = CountHonours(inputs, (int)Features.Records.HonourKind.SuperleagueTitle, exactKind: true);
        Dictionary<int, int> other = CountOtherMajors(inputs);
        Dictionary<int, int> appearances = CountSuperAppearances(inputs);
        (Dictionary<int, int> wins, Dictionary<int, int> seconds, Dictionary<int, int> thirds) = CountPodiums(inputs);
        return new PrestigeCounts(feeder, super, other, appearances, wins, seconds, thirds);
    }

    internal static Dictionary<int, int> CountHonours(SelectionInputs inputs, int kind, bool exactKind)
    {
        Dictionary<int, int> map = [];
        foreach (HonourEntity honour in inputs.Honours)
        {
            bool matches = exactKind ? honour.Kind == kind : honour.Kind != kind;
            if (!matches)
            {
                continue;
            }

            map.TryGetValue(honour.SaveAthleteId, out int current);
            map[honour.SaveAthleteId] = checked(current + 1);
        }

        return map;
    }

    internal static Dictionary<int, int> CountOtherMajors(SelectionInputs inputs)
    {
        Dictionary<int, int> map = [];
        foreach (HonourEntity honour in inputs.Honours)
        {
            if (honour.Kind == (int)Features.Records.HonourKind.FeederTitle
                || honour.Kind == (int)Features.Records.HonourKind.SuperleagueTitle)
            {
                continue;
            }

            map.TryGetValue(honour.SaveAthleteId, out int current);
            map[honour.SaveAthleteId] = checked(current + 1);
        }

        return map;
    }

    internal static Dictionary<int, int> CountSuperAppearances(SelectionInputs inputs)
    {
        Dictionary<int, int> map = [];
        foreach (SeasonMembershipEntity membership in inputs.Memberships)
        {
            if (membership.LeagueId is null)
            {
                continue;
            }

            if (!inputs.LeagueKinds.TryGetValue(membership.LeagueId.Value, out int kind) || kind != (int)LeagueKind.Superleague)
            {
                continue;
            }

            map.TryGetValue(membership.SaveAthleteId, out int current);
            map[membership.SaveAthleteId] = checked(current + 1);
        }

        return map;
    }

    internal static (Dictionary<int, int> Wins, Dictionary<int, int> Seconds, Dictionary<int, int> Thirds) CountPodiums(
        SelectionInputs inputs)
    {
        Dictionary<int, int> wins = [];
        Dictionary<int, int> seconds = [];
        Dictionary<int, int> thirds = [];
        foreach (StageStandingEntity row in inputs.StageRows)
        {
            switch (row.StageRank)
            {
                case 1:
                    wins.TryGetValue(row.SaveAthleteId, out int w);
                    wins[row.SaveAthleteId] = checked(w + 1);
                    break;
                case 2:
                    seconds.TryGetValue(row.SaveAthleteId, out int s);
                    seconds[row.SaveAthleteId] = checked(s + 1);
                    break;
                case 3:
                    thirds.TryGetValue(row.SaveAthleteId, out int t);
                    thirds[row.SaveAthleteId] = checked(t + 1);
                    break;
                default:
                    break;
            }
        }

        return (wins, seconds, thirds);
    }

    internal static Dictionary<int, List<ColorCupSelection.CandidateRaw>> AssembleCandidates(
        SelectionInputs inputs,
        RulesV1 rules,
        Dictionary<int, int> bonusByAthlete,
        Dictionary<int, List<ColorCupSelection.StageFormEntry>> stagesByAthlete,
        PrestigeCounts counts)
    {
        Dictionary<int, List<ColorCupSelection.CandidateRaw>> byColor = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            byColor[(int)color] = [];
        }

        foreach (SaveAthleteEntity athlete in inputs.Athletes)
        {
            ColorCupSelection.CandidateRaw candidate = BuildSingleCandidate(
                athlete, inputs, rules, bonusByAthlete, stagesByAthlete, counts);
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
        Dictionary<int, List<ColorCupSelection.StageFormEntry>> stagesByAthlete,
        PrestigeCounts counts)
    {
        int bonusRaw = bonusByAthlete.TryGetValue(athlete.Id, out int bonus) ? bonus : 0;
        int performanceRaw = inputs.PerformanceByAthlete.TryGetValue(athlete.Id, out int perf) ? perf : 0;
        stagesByAthlete.TryGetValue(athlete.Id, out List<ColorCupSelection.StageFormEntry>? stages);
        int formRaw = ColorCupSelection.ComputeFormRaw(stages ?? [], rules);
        int prestigeRaw = rules.Prestige.ComputeRaw(
            counts.FeederTitles.TryGetValue(athlete.Id, out int feeder) ? feeder : 0,
            counts.SuperTitles.TryGetValue(athlete.Id, out int super) ? super : 0,
            counts.SuperAppearances.TryGetValue(athlete.Id, out int apps) ? apps : 0,
            counts.StageWins.TryGetValue(athlete.Id, out int wins) ? wins : 0,
            counts.StageSeconds.TryGetValue(athlete.Id, out int seconds) ? seconds : 0,
            counts.StageThirds.TryGetValue(athlete.Id, out int thirds) ? thirds : 0,
            counts.OtherMajors.TryGetValue(athlete.Id, out int other) ? other : 0);
        return new ColorCupSelection.CandidateRaw(
            athlete.Id, athlete.Name, athlete.SportingColor, bonusRaw, performanceRaw, formRaw, prestigeRaw);
    }
}
