using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.TieBreaking;

namespace MtgSoloSports.SimulationKernel.Stages;

/// <summary>
/// Pure fixed-point stage accumulation and ranking.
/// Accumulation sums final round points into stage scores and counts round
/// placements for tie-breaking. Ranking is deterministic: stage score is the
/// primary order, then round-place counts best-downward, then raw base totals,
/// then a seeded draw from the supplied <see cref="Pcg32V1"/> only when every
/// deterministic field ties. No clock, GUID ordering, database ordering or
/// ambient randomness is used. Championship points use the snapshot scoring
/// table with no bonus multiplier; earned bonus (round plus stage) is reported
/// as pending for the next-stage boundary.
/// </summary>
public static class StageCalculator
{
    /// <summary>
    /// Accumulates per-athlete stage totals from per-round finishing orders.
    /// Each round must cover positions 1..32 exactly once with unique athletes.
    /// </summary>
    public static IReadOnlyList<StageAthleteTotals> Accumulate(
        IReadOnlyList<IReadOnlyList<StageRoundEntry>> rounds,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        if (rounds.Count != rules.RoundsPerStage)
        {
            throw new InvalidOperationException(
                $"Stage accumulation requires exactly {rules.RoundsPerStage} rounds, was {rounds.Count}.");
        }

        Dictionary<int, Accumulator> accumulators = new(rules.LeagueSize);
        Dictionary<int, string> names = new(rules.LeagueSize);
        foreach (IReadOnlyList<StageRoundEntry> round in rounds)
        {
            ValidateRound(round, rules, accumulators, names);
        }

        if (accumulators.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Stage accumulation requires exactly {rules.LeagueSize} athletes, was {accumulators.Count}.");
        }

        List<StageAthleteTotals> totals = new(accumulators.Count);
        foreach ((int athleteId, Accumulator accumulator) in accumulators)
        {
            if (accumulator.CountSum != rules.RoundsPerStage)
            {
                throw new InvalidOperationException(
                    $"Athlete '{names[athleteId]}' has {accumulator.CountSum} round appearances, expected {rules.RoundsPerStage}.");
            }

            totals.Add(new StageAthleteTotals(
                athleteId,
                names[athleteId],
                accumulator.StageScore,
                accumulator.BaseScore,
                accumulator.Counts));
        }

        totals.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return totals;
    }

    /// <summary>
    /// Ranks accumulated stage totals best-first and awards championship points
    /// plus pending bonus. Stage score is primary; ties break by round-place
    /// counts best-downward, then raw base totals, then a seeded draw that
    /// consumes <paramref name="rng"/> only for exactly tied groups. Groups are
    /// canonically ordered before shuffling so input enumeration order cannot
    /// affect the result.
    /// </summary>
    public static IReadOnlyList<StageRankedAthlete> Rank(
        IReadOnlyList<StageAthleteTotals> totals,
        Pcg32V1 rng,
        RulesV1 rules,
        bool isSuperleague)
    {
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        if (totals.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Stage ranking requires exactly {rules.LeagueSize} athletes, was {totals.Count}.");
        }

        ValidateTotals(totals, rules);

        List<StageAthleteTotals> byScore = SortByScore(totals);
        List<StageRankedAthlete> ranked = new(byScore.Count);
        RankScoreGroups(byScore, ranked, rng, rules, isSuperleague);
        return ranked;
    }

    private static List<StageAthleteTotals> SortByScore(IReadOnlyList<StageAthleteTotals> totals)
    {
        List<StageAthleteTotals> byScore = [.. totals];
        byScore.Sort(static (left, right) =>
        {
            int score = right.StageScoreThousandths.CompareTo(left.StageScoreThousandths);
            return score != 0 ? score : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });
        return byScore;
    }

    private static void RankScoreGroups(
        List<StageAthleteTotals> byScore,
        List<StageRankedAthlete> ranked,
        Pcg32V1 rng,
        RulesV1 rules,
        bool isSuperleague)
    {
        int index = 0;
        while (index < byScore.Count)
        {
            int runEnd = FindScoreRunEnd(byScore, index);
            List<StageAthleteTotals> group = byScore.GetRange(index, runEnd - index);
            IReadOnlyList<StageAthleteTotals> ordered = OrderTiedGroup(group, rng, rules);
            foreach (StageAthleteTotals entry in ordered)
            {
                ranked.Add(AwardStanding(ranked.Count + 1, entry, rules, isSuperleague));
            }

            index = runEnd;
        }
    }

    private static int FindScoreRunEnd(List<StageAthleteTotals> byScore, int start)
    {
        int runEnd = start + 1;
        while (runEnd < byScore.Count &&
            byScore[runEnd].StageScoreThousandths == byScore[start].StageScoreThousandths)
        {
            runEnd++;
        }

        return runEnd;
    }

    private static StageRankedAthlete AwardStanding(int rank, StageAthleteTotals entry, RulesV1 rules, bool isSuperleague)
    {
        int championship = rules.ScoringTable[rank - 1] * RulesV1.FixedScale;
        int earned = checked(ComputeRoundBonusTotal(entry, rules, isSuperleague) +
            ScoringCalculator.StageBonusForPosition(rank, rules, isSuperleague).Thousandths);
        return new StageRankedAthlete(
            entry.AthleteId,
            entry.Name,
            rank,
            entry.StageScoreThousandths,
            entry.BaseScoreThousandths,
            championship,
            entry.RoundWins,
            entry.RoundPlaceCounts,
            earned);
    }

    private sealed class Accumulator
    {
        public int StageScore;
        public int BaseScore;
        public int[] Counts = [];
        public int CountSum;
    }

    private static void ValidateRound(
        IReadOnlyList<StageRoundEntry> round,
        RulesV1 rules,
        Dictionary<int, Accumulator> accumulators,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Stage round must contain exactly {rules.LeagueSize} entries, was {round.Count}.");
        }

        HashSet<int> positions = new();
        HashSet<int> athleteIds = new();
        foreach (StageRoundEntry entry in round)
        {
            ValidateRoundEntry(entry, rules, positions, athleteIds);
            TrackRoundEntry(entry, rules, accumulators, names);
        }

        if (!positions.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
        {
            throw new InvalidOperationException("Stage round must cover positions 1..32 exactly once.");
        }
    }

    private static void ValidateRoundEntry(
        StageRoundEntry entry,
        RulesV1 rules,
        HashSet<int> positions,
        HashSet<int> athleteIds)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Stage round contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Stage round contains an athlete with an empty name.");
        }

        if (entry.Position < 1 || entry.Position > rules.LeagueSize)
        {
            throw new InvalidOperationException($"Stage round position {entry.Position} is out of range.");
        }

        if (!positions.Add(entry.Position))
        {
            throw new InvalidOperationException($"Stage round contains duplicate position {entry.Position}.");
        }

        if (!athleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Stage round contains duplicate athlete id {entry.AthleteId}.");
        }

        if (entry.BaseThousandths < 0 || entry.FinalThousandths < 0)
        {
            throw new InvalidOperationException($"Stage round points for '{entry.Name}' cannot be negative.");
        }

        int expectedBase = rules.ScoringTable[entry.Position - 1] * RulesV1.FixedScale;
        if (entry.BaseThousandths != expectedBase)
        {
            throw new InvalidOperationException(
                $"Stage round base points for position {entry.Position} must be {expectedBase}, was {entry.BaseThousandths}.");
        }
    }

    private static void TrackRoundEntry(
        StageRoundEntry entry,
        RulesV1 rules,
        Dictionary<int, Accumulator> accumulators,
        Dictionary<int, string> names)
    {
        if (!names.TryGetValue(entry.AthleteId, out string? known))
        {
            names[entry.AthleteId] = entry.Name;
        }
        else if (!string.Equals(known, entry.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Athlete id {entry.AthleteId} has inconsistent names.");
        }

        if (!accumulators.TryGetValue(entry.AthleteId, out Accumulator? accumulator))
        {
            accumulator = new Accumulator { Counts = new int[rules.LeagueSize] };
            accumulators[entry.AthleteId] = accumulator;
        }

        checked
        {
            accumulator.StageScore += entry.FinalThousandths;
            accumulator.BaseScore += entry.BaseThousandths;
        }

        accumulator.Counts[entry.Position - 1]++;
        accumulator.CountSum++;
    }

    private static void ValidateTotals(IReadOnlyList<StageAthleteTotals> totals, RulesV1 rules)
    {
        HashSet<int> ids = new();
        HashSet<string> athleteNames = new(StringComparer.Ordinal);
        foreach (StageAthleteTotals entry in totals)
        {
            if (entry.AthleteId <= 0)
            {
                throw new InvalidOperationException($"Stage totals contain invalid athlete id {entry.AthleteId}.");
            }

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                throw new InvalidOperationException("Stage totals contain an athlete with an empty name.");
            }

            if (!ids.Add(entry.AthleteId))
            {
                throw new InvalidOperationException($"Stage totals contain duplicate athlete id {entry.AthleteId}.");
            }

            if (!athleteNames.Add(entry.Name))
            {
                throw new InvalidOperationException($"Stage totals contain duplicate athlete '{entry.Name}'.");
            }

            if (entry.StageScoreThousandths < 0 || entry.BaseScoreThousandths < 0)
            {
                throw new InvalidOperationException($"Stage totals for '{entry.Name}' cannot be negative.");
            }

            if (entry.RoundPlaceCounts.Count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"Stage totals for '{entry.Name}' must have {rules.LeagueSize} round-place counts, was {entry.RoundPlaceCounts.Count}.");
            }

            int sum = 0;
            foreach (int count in entry.RoundPlaceCounts)
            {
                if (count < 0)
                {
                    throw new InvalidOperationException($"Stage totals for '{entry.Name}' contain a negative placement count.");
                }

                checked
                {
                    sum += count;
                }
            }

            if (sum != rules.RoundsPerStage)
            {
                throw new InvalidOperationException(
                    $"Stage totals for '{entry.Name}' sum to {sum} round appearances, expected {rules.RoundsPerStage}.");
            }
        }
    }

    private static IReadOnlyList<StageAthleteTotals> OrderTiedGroup(
        List<StageAthleteTotals> group,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        if (group.Count == 1)
        {
            return group;
        }

        IReadOnlyList<RankedEntry<StageAthleteTotals>> ranked = TieBreaker.RankWithSeededDraw(
            group,
            entry => new DeterministicTieBreakKey(
                [],
                entry.RoundPlaceCounts,
                entry.BaseScoreThousandths),
            entry => entry.Name,
            rng);
        return ranked.Select(r => r.Entry).ToList();
    }

    private static int ComputeRoundBonusTotal(StageAthleteTotals entry, RulesV1 rules, bool isSuperleague)
    {
        int total = 0;
        checked
        {
            for (int position = 1; position <= entry.RoundPlaceCounts.Count; position++)
            {
                int count = entry.RoundPlaceCounts[position - 1];
                if (count == 0)
                {
                    continue;
                }

                int perFinish = ScoringCalculator.RoundBonusForPosition(position, rules, isSuperleague).Thousandths;
                total += checked(count * perFinish);
            }
        }

        return total;
    }
}
