using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.Stages;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Stages;

public sealed class StageCalculatorTests
{
    private static RulesV1 Rules => RulesV1.CreateDefault();

    [Fact]
    public void Accumulate_SumsFinalsAndCountsRoundPlacements()
    {
        List<List<StageRoundEntry>> rounds = BuildUniformRounds();
        IReadOnlyList<StageAthleteTotals> totals = StageCalculator.Accumulate(rounds, Rules);

        totals.Count.ShouldBe(32);
        foreach (StageAthleteTotals entry in totals)
        {
            entry.RoundPlaceCounts.Count.ShouldBe(32);
            entry.RoundPlaceCounts.Sum().ShouldBe(16);
        }

        // Athlete 1 wins every round: 16 first places, stage score 16 * 77_000.
        StageAthleteTotals winner = totals.Single(t => t.AthleteId == 1);
        winner.RoundWins.ShouldBe(16);
        winner.RoundPlaceCounts[0].ShouldBe(16);
        winner.StageScoreThousandths.ShouldBe(16 * 77_000);
        winner.BaseScoreThousandths.ShouldBe(16 * 77_000);
    }

    [Fact]
    public void Rank_AwardsChampionshipPointsWithNoBonusMultiplier()
    {
        IReadOnlyList<StageAthleteTotals> totals = StageCalculator.Accumulate(BuildUniformRounds(), Rules);
        IReadOnlyList<StageRankedAthlete> ranked = StageCalculator.Rank(totals, new Pcg32V1(1UL, 2UL), Rules, isSuperleague: false);

        ranked.Count.ShouldBe(32);
        ranked.Select(r => r.StageRank).ShouldBe(Enumerable.Range(1, 32).ToList());

        int[] expectedTable = [77, 67, 58, 50, 43, 37, 32, 28, 25, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1];
        foreach (StageRankedAthlete entry in ranked)
        {
            entry.ChampionshipPointsThousandths.ShouldBe(expectedTable[entry.StageRank - 1] * 1000);
        }

        // Winner: 16 round wins at +100 each plus stage win at +200.
        StageRankedAthlete winner = ranked.Single(r => r.StageRank == 1);
        winner.AthleteId.ShouldBe(1);
        winner.EarnedBonusThousandths.ShouldBe((16 * 100) + 200);

        // 11th place earns no bonus (round places 11 never pay, stage 11 never pays).
        StageRankedAthlete eleventh = ranked.Single(r => r.StageRank == 11);
        eleventh.EarnedBonusThousandths.ShouldBe(0);
    }

    [Fact]
    public void Rank_SuperleagueDoublesEarnedBonus_ChampionshipUnchanged()
    {
        IReadOnlyList<StageAthleteTotals> totals = StageCalculator.Accumulate(BuildUniformRounds(), Rules);
        IReadOnlyList<StageRankedAthlete> ranked = StageCalculator.Rank(totals, new Pcg32V1(1UL, 2UL), Rules, isSuperleague: true);

        StageRankedAthlete winner = ranked.Single(r => r.StageRank == 1);
        winner.ChampionshipPointsThousandths.ShouldBe(77_000);
        winner.EarnedBonusThousandths.ShouldBe(((16 * 100) + 200) * 2);
    }

    [Fact]
    public void Rank_SameScore_MoreRoundWinsRanksAhead()
    {
        // Two athletes tie on stage score; the one with more round 1sts wins the tie.
        List<StageAthleteTotals> totals = [];
        totals.Add(new StageAthleteTotals(1, "Athlete A", 100_000, 90_000, CountsWith(firsts: 3, seconds: 0)));
        totals.Add(new StageAthleteTotals(2, "Athlete B", 100_000, 90_000, CountsWith(firsts: 1, seconds: 5)));
        for (int i = 3; i <= 32; i++)
        {
            totals.Add(new StageAthleteTotals(i, $"Athlete {i:D2}", 100_000 - (i * 1000), 80_000 - (i * 1000), CountsWith(firsts: 0, seconds: 0)));
        }

        IReadOnlyList<StageRankedAthlete> ranked = StageCalculator.Rank(totals, new Pcg32V1(5UL, 6UL), Rules, isSuperleague: false);

        ranked[0].AthleteId.ShouldBe(1);
        ranked[1].AthleteId.ShouldBe(2);
    }

    [Fact]
    public void Rank_ExactTie_UsesSeededDrawDeterministically()
    {
        List<StageAthleteTotals> BuildTied()
        {
            List<StageAthleteTotals> tied = [];
            for (int i = 1; i <= 32; i++)
            {
                // All athletes share score/base/counts except identity: full deterministic tie.
                tied.Add(new StageAthleteTotals(i, $"Athlete {i:D2}", 50_000, 50_000, CountsWith(firsts: 0, seconds: 0)));
            }

            return tied;
        }

        // Same seed reproduces the same tied order.
        IReadOnlyList<StageRankedAthlete> first = StageCalculator.Rank(BuildTied(), new Pcg32V1(9UL, 9UL), Rules, isSuperleague: false);
        IReadOnlyList<StageRankedAthlete> second = StageCalculator.Rank(BuildTied(), new Pcg32V1(9UL, 9UL), Rules, isSuperleague: false);
        first.Select(r => r.AthleteId).ShouldBe(second.Select(r => r.AthleteId).ToList());

        // Untied ranking never consumes RNG.
        List<StageAthleteTotals> untied = [];
        for (int i = 1; i <= 32; i++)
        {
            untied.Add(new StageAthleteTotals(i, $"Athlete {i:D2}", 100_000 - (i * 1000), 100_000 - (i * 1000), CountsWith(firsts: 0, seconds: 0)));
        }

        Pcg32V1 rng = new(77UL, 88UL);
        Pcg32State before = rng.Snapshot();
        StageCalculator.Rank(untied, rng, Rules, isSuperleague: false);
        rng.Snapshot().ShouldBe(before);
    }

    [Fact]
    public void Rank_RejectsWrongAthleteCountAndBadVectors()
    {
        List<StageAthleteTotals> shortList = [];
        for (int i = 1; i <= 31; i++)
        {
            shortList.Add(new StageAthleteTotals(i, $"Athlete {i:D2}", 10_000, 10_000, CountsWith(firsts: 0, seconds: 0)));
        }

        Should.Throw<InvalidOperationException>(() => StageCalculator.Rank(shortList, new Pcg32V1(1UL, 2UL), Rules, isSuperleague: false));

        List<StageAthleteTotals> badVector = [];
        for (int i = 1; i <= 32; i++)
        {
            badVector.Add(new StageAthleteTotals(i, $"Athlete {i:D2}", 10_000, 10_000, [1, 2, 3]));
        }

        Should.Throw<InvalidOperationException>(() => StageCalculator.Rank(badVector, new Pcg32V1(1UL, 2UL), Rules, isSuperleague: false));
    }

    [Fact]
    public void Accumulate_RejectsNonSixteenRounds()
    {
        List<List<StageRoundEntry>> fifteen = BuildUniformRounds().Take(15).ToList();
        Should.Throw<InvalidOperationException>(() => StageCalculator.Accumulate(fifteen, Rules));
    }

    private static List<List<StageRoundEntry>> BuildUniformRounds()
    {
        // Every round has the same finishing order: athlete i finishes position i.
        // Base points follow the snapshot table; finals equal base (zero active bonus).
        List<List<StageRoundEntry>> rounds = new(16);
        for (int round = 0; round < 16; round++)
        {
            List<StageRoundEntry> entries = new(32);
            for (int position = 1; position <= 32; position++)
            {
                int basePoints = Rules.ScoringTable[position - 1] * 1000;
                entries.Add(new StageRoundEntry(position, $"Athlete {position:D2}", position, basePoints, basePoints));
            }

            rounds.Add(entries);
        }

        return rounds;
    }

    private static List<int> CountsWith(int firsts, int seconds)
    {
        int[] counts = new int[32];
        counts[0] = firsts;
        counts[1] = seconds;
        counts[31] = 16 - firsts - seconds;
        return counts.ToList();
    }
}
