using System.Security.Cryptography;
using System.Text;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.TieBreaking;

namespace MtgSoloSports.SimulationKernel.Seasons;

/// <summary>
/// Pure fixed-point season accumulation and ranking.
/// Accumulation sums stage championship points (awarded with no bonus
/// multiplier) into season totals and aggregates stage/round placement
/// vectors for tie-breaking. Ranking is deterministic: total championship
/// points first, then stage-place counts best-downward, then round-place
/// counts best-downward, then raw totals (stage score, then base score),
/// then a seeded draw from the supplied <see cref="Pcg32V1"/> only when every
/// deterministic field ties. No clock, GUID ordering, database ordering or
/// ambient randomness is used. All arithmetic is integer-only.
/// </summary>
public static class SeasonCalculator
{
    /// <summary>
    /// Accumulates per-athlete season totals from per-stage standings.
    /// Each stage must cover ranks 1..32 exactly once with the same 32 athletes.
    /// Accepts 1..32 stages so provisional current standings reuse the same
    /// mathematics as the finalized 32-stage season table.
    /// </summary>
    public static IReadOnlyList<SeasonAthleteTotals> Accumulate(
        IReadOnlyList<IReadOnlyList<SeasonStageEntry>> stages,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        if (stages.Count < 1 || stages.Count > rules.StagesPerSeason)
        {
            throw new InvalidOperationException(
                $"Season accumulation requires 1..{rules.StagesPerSeason} stages, was {stages.Count}.");
        }

        Dictionary<int, Accumulator> accumulators = new(rules.LeagueSize);
        Dictionary<int, string> names = new(rules.LeagueSize);
        foreach (IReadOnlyList<SeasonStageEntry> stage in stages)
        {
            ValidateStage(stage, rules, accumulators, names);
        }

        if (accumulators.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Season accumulation requires exactly {rules.LeagueSize} athletes, was {accumulators.Count}.");
        }

        List<SeasonAthleteTotals> totals = new(accumulators.Count);
        foreach ((int athleteId, Accumulator accumulator) in accumulators)
        {
            if (accumulator.StageAppearances != stages.Count)
            {
                throw new InvalidOperationException(
                    $"Athlete '{names[athleteId]}' has {accumulator.StageAppearances} stage appearances, expected {stages.Count}.");
            }

            totals.Add(new SeasonAthleteTotals(
                athleteId,
                names[athleteId],
                accumulator.Championship,
                accumulator.StageScore,
                accumulator.BaseScore,
                accumulator.StagePlaceCounts,
                accumulator.RoundPlaceCounts));
        }

        totals.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return totals;
    }

    /// <summary>
    /// Ranks accumulated season totals best-first.
    /// Total championship points decide first; ties break by stage-place counts
    /// best-downward, then round-place counts best-downward, then raw totals
    /// (stage score, then base score encoded into one ordered 64-bit key), then
    /// a seeded draw that consumes <paramref name="rng"/> only for exactly tied
    /// groups. Groups are canonically ordered before shuffling so input
    /// enumeration order cannot affect the result.
    /// </summary>
    public static IReadOnlyList<SeasonRankedAthlete> Rank(
        IReadOnlyList<SeasonAthleteTotals> totals,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        if (totals.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Season ranking requires exactly {rules.LeagueSize} athletes, was {totals.Count}.");
        }

        ValidateTotals(totals, rules);

        List<SeasonAthleteTotals> byChampionship = [.. totals];
        byChampionship.Sort(static (left, right) =>
        {
            int championship = right.TotalChampionshipPointsThousandths.CompareTo(left.TotalChampionshipPointsThousandths);
            return championship != 0 ? championship : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });

        List<SeasonRankedAthlete> ranked = new(byChampionship.Count);
        int index = 0;
        while (index < byChampionship.Count)
        {
            int runEnd = index + 1;
            while (runEnd < byChampionship.Count &&
                byChampionship[runEnd].TotalChampionshipPointsThousandths == byChampionship[index].TotalChampionshipPointsThousandths)
            {
                runEnd++;
            }

            List<SeasonAthleteTotals> group = byChampionship.GetRange(index, runEnd - index);
            IReadOnlyList<SeasonAthleteTotals> ordered = OrderTiedGroup(group, rng);
            foreach (SeasonAthleteTotals entry in ordered)
            {
                int rank = ranked.Count + 1;
                ranked.Add(new SeasonRankedAthlete(
                    entry.AthleteId,
                    entry.Name,
                    rank,
                    entry.TotalChampionshipPointsThousandths,
                    entry.TotalStageScoreThousandths,
                    entry.TotalBaseScoreThousandths,
                    entry.StageWins,
                    entry.RoundWins,
                    entry.StagePlaceCounts,
                    entry.RoundPlaceCounts,
                    rank == 1));
            }

            index = runEnd;
        }

        return ranked;
    }

    /// <summary>
    /// Fingerprints season standings: lowercase hex SHA-256 over lines of
    /// <c>Rank:AthleteId:Name:Championship:StageScore:Base</c> in rank order.
    /// SHA-256 is a content fingerprint here, not sporting randomness.
    /// </summary>
    public static string ComputeChecksum(IReadOnlyList<SeasonRankedAthlete> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (SeasonRankedAthlete entry in ranked)
        {
            builder.Append(entry.SeasonRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.AthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.Name);
            builder.Append(':');
            builder.Append(entry.TotalChampionshipPointsThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.TotalStageScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.TotalBaseScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private sealed class Accumulator
    {
        public int Championship;
        public int StageScore;
        public int BaseScore;
        public int[] StagePlaceCounts = [];
        public int[] RoundPlaceCounts = [];
        public int StageAppearances;
    }

    private static void ValidateStage(
        IReadOnlyList<SeasonStageEntry> stage,
        RulesV1 rules,
        Dictionary<int, Accumulator> accumulators,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Season stage must contain exactly {rules.LeagueSize} entries, was {stage.Count}.");
        }

        HashSet<int> ranks = new();
        HashSet<int> athleteIds = new();
        foreach (SeasonStageEntry entry in stage)
        {
            ValidateStageEntry(entry, rules, ranks, athleteIds);
            TrackStageEntry(entry, rules, accumulators, names);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
        {
            throw new InvalidOperationException("Season stage must cover ranks 1..32 exactly once.");
        }
    }

    private static void ValidateStageEntry(
        SeasonStageEntry entry,
        RulesV1 rules,
        HashSet<int> ranks,
        HashSet<int> athleteIds)
    {
        CheckStageEntryIdentity(entry, rules, ranks, athleteIds);
        CheckStageEntryChampionship(entry, rules);
        CheckStageEntryRoundCounts(entry, rules);
    }

    private static void CheckStageEntryIdentity(
        SeasonStageEntry entry,
        RulesV1 rules,
        HashSet<int> ranks,
        HashSet<int> athleteIds)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Season stage contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Season stage contains an athlete with an empty name.");
        }

        if (entry.StageRank < 1 || entry.StageRank > rules.LeagueSize)
        {
            throw new InvalidOperationException($"Season stage rank {entry.StageRank} is out of range.");
        }

        if (!ranks.Add(entry.StageRank))
        {
            throw new InvalidOperationException($"Season stage contains duplicate rank {entry.StageRank}.");
        }

        if (!athleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Season stage contains duplicate athlete id {entry.AthleteId}.");
        }
    }

    private static void CheckStageEntryChampionship(SeasonStageEntry entry, RulesV1 rules)
    {
        int expectedChampionship = rules.ScoringTable[entry.StageRank - 1] * RulesV1.FixedScale;
        if (entry.ChampionshipPointsThousandths != expectedChampionship)
        {
            throw new InvalidOperationException(
                $"Season stage championship points for rank {entry.StageRank} must be {expectedChampionship}, was {entry.ChampionshipPointsThousandths}.");
        }

        if (entry.StageScoreThousandths < 0 || entry.BaseScoreThousandths < 0)
        {
            throw new InvalidOperationException($"Season stage totals for '{entry.Name}' cannot be negative.");
        }
    }

    private static void CheckStageEntryRoundCounts(SeasonStageEntry entry, RulesV1 rules)
    {
        if (entry.RoundPlaceCounts.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Season stage round-place counts for '{entry.Name}' must have {rules.LeagueSize} entries, was {entry.RoundPlaceCounts.Count}.");
        }

        int sum = SumRoundCounts(entry);
        if (sum != rules.RoundsPerStage)
        {
            throw new InvalidOperationException(
                $"Season stage round-place counts for '{entry.Name}' must sum to {rules.RoundsPerStage}, was {sum}.");
        }
    }

    private static int SumRoundCounts(SeasonStageEntry entry)
    {
        int sum = 0;
        foreach (int count in entry.RoundPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Season stage round-place counts for '{entry.Name}' cannot be negative.");
            }

            checked
            {
                sum += count;
            }
        }

        return sum;
    }

    private static void TrackStageEntry(
        SeasonStageEntry entry,
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
            accumulator = new Accumulator
            {
                StagePlaceCounts = new int[rules.LeagueSize],
                RoundPlaceCounts = new int[rules.LeagueSize],
            };
            accumulators[entry.AthleteId] = accumulator;
        }

        checked
        {
            accumulator.Championship += entry.ChampionshipPointsThousandths;
            accumulator.StageScore += entry.StageScoreThousandths;
            accumulator.BaseScore += entry.BaseScoreThousandths;
        }

        accumulator.StagePlaceCounts[entry.StageRank - 1]++;
        for (int i = 0; i < rules.LeagueSize; i++)
        {
            checked
            {
                accumulator.RoundPlaceCounts[i] += entry.RoundPlaceCounts[i];
            }
        }

        accumulator.StageAppearances++;
    }

    private static void ValidateTotals(IReadOnlyList<SeasonAthleteTotals> totals, RulesV1 rules)
    {
        HashSet<int> ids = new();
        HashSet<string> athleteNames = new(StringComparer.Ordinal);
        int? expectedStageAppearances = null;
        foreach (SeasonAthleteTotals entry in totals)
        {
            CheckTotalsIdentity(entry, ids, athleteNames);
            expectedStageAppearances = CheckTotalsCoverage(entry, rules, expectedStageAppearances);
        }
    }

    private static void CheckTotalsIdentity(
        SeasonAthleteTotals entry,
        HashSet<int> ids,
        HashSet<string> athleteNames)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Season totals contain invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Season totals contain an athlete with an empty name.");
        }

        if (!ids.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Season totals contain duplicate athlete id {entry.AthleteId}.");
        }

        if (!athleteNames.Add(entry.Name))
        {
            throw new InvalidOperationException($"Season totals contain duplicate athlete '{entry.Name}'.");
        }

        if (entry.TotalChampionshipPointsThousandths < 0 ||
            entry.TotalStageScoreThousandths < 0 ||
            entry.TotalBaseScoreThousandths < 0)
        {
            throw new InvalidOperationException($"Season totals for '{entry.Name}' cannot be negative.");
        }
    }

    private static int? CheckTotalsCoverage(
        SeasonAthleteTotals entry,
        RulesV1 rules,
        int? expectedStageAppearances)
    {
        if (entry.StagePlaceCounts.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Season totals for '{entry.Name}' must have {rules.LeagueSize} stage-place counts, was {entry.StagePlaceCounts.Count}.");
        }

        if (entry.RoundPlaceCounts.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Season totals for '{entry.Name}' must have {rules.LeagueSize} round-place counts, was {entry.RoundPlaceCounts.Count}.");
        }

        return CheckTotalsSums(entry, rules, expectedStageAppearances);
    }

    private static int? CheckTotalsSums(
        SeasonAthleteTotals entry,
        RulesV1 rules,
        int? expectedStageAppearances)
    {
        int stageSum = SumNonNegative(entry.StagePlaceCounts, entry.Name);
        int roundSum = SumNonNegative(entry.RoundPlaceCounts, entry.Name);

        if (stageSum < 1 || stageSum > rules.StagesPerSeason)
        {
            throw new InvalidOperationException(
                $"Season totals for '{entry.Name}' cover {stageSum} stages, expected 1..{rules.StagesPerSeason}.");
        }

        if (expectedStageAppearances is null)
        {
            expectedStageAppearances = stageSum;
        }
        else if (expectedStageAppearances != stageSum)
        {
            throw new InvalidOperationException(
                $"Season totals cover inconsistent stage counts ({expectedStageAppearances} vs {stageSum}).");
        }

        checked
        {
            if (roundSum != stageSum * rules.RoundsPerStage)
            {
                throw new InvalidOperationException(
                    $"Season totals for '{entry.Name}' cover {roundSum} round appearances, expected {stageSum * rules.RoundsPerStage}.");
            }
        }

        return expectedStageAppearances;
    }

    private static int SumNonNegative(IReadOnlyList<int> vector, string name)
    {
        int sum = 0;
        foreach (int count in vector)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Season totals for '{name}' contain a negative placement count.");
            }

            checked
            {
                sum += count;
            }
        }

        return sum;
    }

    private static IReadOnlyList<SeasonAthleteTotals> OrderTiedGroup(
        List<SeasonAthleteTotals> group,
        Pcg32V1 rng)
    {
        if (group.Count == 1)
        {
            return group;
        }

        IReadOnlyList<RankedEntry<SeasonAthleteTotals>> ranked = TieBreaker.RankWithSeededDraw(
            group,
            entry => new DeterministicTieBreakKey(
                entry.StagePlaceCounts,
                entry.RoundPlaceCounts,
                EncodeRawTotals(entry.TotalStageScoreThousandths, entry.TotalBaseScoreThousandths)),
            entry => entry.Name,
            rng);
        return ranked.Select(r => r.Entry).ToList();
    }

    internal static long EncodeRawTotals(int stageScoreThousandths, int baseScoreThousandths)
    {
        // Stage score decides before base score; packing into one ordered
        // 64-bit key keeps the generic tie-breaker unchanged while preserving
        // that order (higher stage score ranks ahead, base breaks remaining ties).
        checked
        {
            return (((long)stageScoreThousandths) << 32) | (uint)baseScoreThousandths;
        }
    }
}
