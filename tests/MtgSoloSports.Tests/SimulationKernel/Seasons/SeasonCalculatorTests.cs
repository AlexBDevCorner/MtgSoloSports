using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Seasons;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Seasons;

public sealed class SeasonCalculatorTests
{
    private static RulesV1 Rules => RulesV1.CreateDefault();

    [Fact]
    public void Accumulate_SumsChampionshipAndAggregatesPlaceCounts()
    {
        List<List<SeasonStageEntry>> stages = BuildTwoStagesWinnerTakesAll();
        IReadOnlyList<SeasonAthleteTotals> totals = SeasonCalculator.Accumulate(stages, Rules);

        totals.Count.ShouldBe(32);
        SeasonAthleteTotals winner = totals.Single(t => t.AthleteId == 1);
        winner.TotalChampionshipPointsThousandths.ShouldBe(2 * 77_000);
        winner.StageWins.ShouldBe(2);
        winner.StagePlaceCounts[0].ShouldBe(2);
        winner.StagePlaceCounts.Sum().ShouldBe(2);
        winner.RoundPlaceCounts.Sum().ShouldBe(2 * 16);

        SeasonAthleteTotals last = totals.Single(t => t.AthleteId == 32);
        last.TotalChampionshipPointsThousandths.ShouldBe(2 * 1_000);
        last.StagePlaceCounts[31].ShouldBe(2);
    }

    [Fact]
    public void Rank_OrdersByChampionshipThenStagePlaces()
    {
        // Two athletes tie on championship; more stage wins ranks ahead.
        List<SeasonAthleteTotals> totals = [];
        totals.Add(new SeasonAthleteTotals(1, "Athlete A", 100_000, 500_000, 400_000, StageCounts(firsts: 2), RoundCounts()));
        totals.Add(new SeasonAthleteTotals(2, "Athlete B", 100_000, 500_000, 400_000, StageCounts(firsts: 0), RoundCounts()));
        for (int i = 3; i <= 32; i++)
        {
            totals.Add(new SeasonAthleteTotals(i, $"Athlete {i:D2}", 100_000 - (i * 1000), 400_000 - (i * 1000), 300_000, StageCounts(firsts: 0), RoundCounts()));
        }

        IReadOnlyList<SeasonRankedAthlete> ranked = SeasonCalculator.Rank(totals, new Pcg32V1(5UL, 6UL), Rules);

        ranked[0].AthleteId.ShouldBe(1);
        ranked[0].IsChampion.ShouldBeTrue();
        ranked[1].AthleteId.ShouldBe(2);
        ranked[1].IsChampion.ShouldBeFalse();
    }

    [Fact]
    public void Rank_TieOnChampionshipAndStagePlaces_UsesRoundPlacesThenRawTotals()
    {
        // Same championship and stage vectors; more round wins ranks ahead.
        List<SeasonAthleteTotals> totals = [];
        totals.Add(new SeasonAthleteTotals(1, "Athlete A", 90_000, 400_000, 350_000, StageCounts(firsts: 1), RoundCounts(firsts: 10)));
        totals.Add(new SeasonAthleteTotals(2, "Athlete B", 90_000, 400_000, 350_000, StageCounts(firsts: 1), RoundCounts(firsts: 2)));
        for (int i = 3; i <= 32; i++)
        {
            totals.Add(new SeasonAthleteTotals(i, $"Athlete {i:D2}", 80_000 - (i * 100), 300_000 - (i * 1000), 250_000, StageCounts(firsts: 0), RoundCounts()));
        }

        IReadOnlyList<SeasonRankedAthlete> ranked = SeasonCalculator.Rank(totals, new Pcg32V1(7UL, 8UL), Rules);

        ranked[0].AthleteId.ShouldBe(1);
        ranked[1].AthleteId.ShouldBe(2);

        // Same championship/stage/round vectors; higher raw stage score ranks ahead.
        List<SeasonAthleteTotals> rawTie = [];
        rawTie.Add(new SeasonAthleteTotals(1, "Athlete A", 90_000, 410_000, 350_000, StageCounts(firsts: 1), RoundCounts(firsts: 5)));
        rawTie.Add(new SeasonAthleteTotals(2, "Athlete B", 90_000, 400_000, 350_000, StageCounts(firsts: 1), RoundCounts(firsts: 5)));
        for (int i = 3; i <= 32; i++)
        {
            rawTie.Add(new SeasonAthleteTotals(i, $"Athlete {i:D2}", 70_000 - (i * 100), 200_000 - (i * 1000), 150_000, StageCounts(firsts: 0), RoundCounts()));
        }

        IReadOnlyList<SeasonRankedAthlete> rawRanked = SeasonCalculator.Rank(rawTie, new Pcg32V1(7UL, 8UL), Rules);
        rawRanked[0].AthleteId.ShouldBe(1);
    }

    [Fact]
    public void Rank_ExactTie_UsesSeededDrawDeterministically()
    {
        List<SeasonAthleteTotals> BuildTied()
        {
            List<SeasonAthleteTotals> tied = [];
            for (int i = 1; i <= 32; i++)
            {
                tied.Add(new SeasonAthleteTotals(i, $"Athlete {i:D2}", 50_000, 400_000, 350_000, StageCounts(firsts: 0), RoundCounts()));
            }

            return tied;
        }

        IReadOnlyList<SeasonRankedAthlete> first = SeasonCalculator.Rank(BuildTied(), new Pcg32V1(9UL, 9UL), Rules);
        IReadOnlyList<SeasonRankedAthlete> second = SeasonCalculator.Rank(BuildTied(), new Pcg32V1(9UL, 9UL), Rules);
        first.Select(r => r.AthleteId).ShouldBe(second.Select(r => r.AthleteId).ToList());
        first[0].IsChampion.ShouldBeTrue();
        first.Skip(1).All(r => !r.IsChampion).ShouldBeTrue();

        // Untied ranking never consumes RNG.
        List<SeasonAthleteTotals> untied = [];
        for (int i = 1; i <= 32; i++)
        {
            untied.Add(new SeasonAthleteTotals(i, $"Athlete {i:D2}", 100_000 - (i * 1000), 500_000 - (i * 1000), 400_000, StageCounts(firsts: 0), RoundCounts()));
        }

        Pcg32V1 rng = new(77UL, 88UL);
        Pcg32State before = rng.Snapshot();
        SeasonCalculator.Rank(untied, rng, Rules);
        rng.Snapshot().ShouldBe(before);
    }

    [Fact]
    public void Rank_RejectsWrongCountsAndBadVectors()
    {
        List<SeasonAthleteTotals> shortList = [];
        for (int i = 1; i <= 31; i++)
        {
            shortList.Add(new SeasonAthleteTotals(i, $"Athlete {i:D2}", 10_000, 100_000, 90_000, StageCounts(firsts: 0), RoundCounts()));
        }

        Should.Throw<InvalidOperationException>(() => SeasonCalculator.Rank(shortList, new Pcg32V1(1UL, 2UL), Rules));

        List<SeasonAthleteTotals> badVector = [];
        for (int i = 1; i <= 32; i++)
        {
            badVector.Add(new SeasonAthleteTotals(i, $"Athlete {i:D2}", 10_000, 100_000, 90_000, [1, 2, 3], RoundCounts()));
        }

        Should.Throw<InvalidOperationException>(() => SeasonCalculator.Rank(badVector, new Pcg32V1(1UL, 2UL), Rules));
    }

    [Fact]
    public void Accumulate_RejectsEmptyAndTooManyStages()
    {
        Should.Throw<InvalidOperationException>(() => SeasonCalculator.Accumulate([], Rules));

        List<List<SeasonStageEntry>> tooMany = [];
        for (int s = 0; s < 33; s++)
        {
            tooMany.Add(BuildSingleStage(s));
        }

        Should.Throw<InvalidOperationException>(() => SeasonCalculator.Accumulate(tooMany, Rules));
    }

    [Fact]
    public void ComputeChecksum_IsStableAndOrderSensitive()
    {
        List<List<SeasonStageEntry>> stages = BuildTwoStagesWinnerTakesAll();
        IReadOnlyList<SeasonAthleteTotals> totals = SeasonCalculator.Accumulate(stages, Rules);
        IReadOnlyList<SeasonRankedAthlete> ranked = SeasonCalculator.Rank(totals, new Pcg32V1(1UL, 2UL), Rules);

        string first = SeasonCalculator.ComputeChecksum(ranked);
        string second = SeasonCalculator.ComputeChecksum(ranked);
        first.ShouldBe(second);
        first.Length.ShouldBe(64);
    }

    private static List<List<SeasonStageEntry>> BuildTwoStagesWinnerTakesAll()
    {
        List<List<SeasonStageEntry>> stages = new(2);
        for (int s = 0; s < 2; s++)
        {
            stages.Add(BuildSingleStage(s));
        }

        return stages;
    }

    private static List<SeasonStageEntry> BuildSingleStage(int stageIndex)
    {
        // Same finishing order every stage: athlete i finishes position i.
        // Championship follows the snapshot table; round counts put all 16
        // round appearances at the athlete's stage position for simplicity.
        List<SeasonStageEntry> entries = new(32);
        for (int position = 1; position <= 32; position++)
        {
            int championship = Rules.ScoringTable[position - 1] * 1000;
            int[] roundCounts = new int[32];
            roundCounts[position - 1] = 16;
            entries.Add(new SeasonStageEntry(
                position,
                $"Athlete {position:D2}",
                position,
                championship,
                100_000 + (32 - position) * 1000 + stageIndex,
                90_000 + (32 - position) * 1000,
                roundCounts.ToList()));
        }

        return entries;
    }

    private static List<int> StageCounts(int firsts)
    {
        int[] counts = new int[32];
        counts[0] = firsts;
        counts[31] = 2 - firsts;
        return counts.ToList();
    }

    private static List<int> RoundCounts(int firsts = 0)
    {
        int[] counts = new int[32];
        counts[0] = firsts;
        counts[31] = 32 - firsts;
        return counts.ToList();
    }
}
