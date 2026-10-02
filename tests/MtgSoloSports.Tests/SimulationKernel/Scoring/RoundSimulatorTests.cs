using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Scoring;

public sealed class RoundSimulatorTests
{
    private static RulesV1 Rules => RulesV1.CreateDefault();

    [Fact]
    public void Simulate_ZeroBonus_RoundOneRankAfterEqualsPosition()
    {
        IReadOnlyList<RoundAthleteInput> roster = BuildRoster(0);
        Pcg32V1 rng = new(101UL, 202UL);

        RoundSimulationResult result = RoundSimulator.Simulate(roster, rng, Rules);

        result.Placements.Count.ShouldBe(32);
        result.Placements.Select(p => p.Position).ShouldBe(Enumerable.Range(1, 32).ToList());
        result.Checksum.Length.ShouldBe(64);

        int[] expectedBase =
        [
            77, 67, 58, 50, 43, 37, 32, 28, 25, 23, 22, 21, 20, 19, 18, 17,
            16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1,
        ];
        for (int i = 0; i < 32; i++)
        {
            RoundPlacement placement = result.Placements[i];
            placement.BasePoints.Thousandths.ShouldBe(expectedBase[i] * 1000);
            placement.ActiveBonus.Thousandths.ShouldBe(0);
            placement.FinalPoints.Thousandths.ShouldBe(expectedBase[i] * 1000);
            placement.CumulativeBefore.Thousandths.ShouldBe(0);
            placement.CumulativeAfter.Thousandths.ShouldBe(expectedBase[i] * 1000);
            placement.RankAfter.ShouldBe(placement.Position);
            placement.RankMovement.ShouldBe(placement.RankBefore - placement.Position);
        }
    }

    [Fact]
    public void Simulate_AppliesOnlySuppliedActiveBonus_FixedPoint()
    {
        List<RoundAthleteInput> roster = BuildRoster(0).ToList();
        roster[0] = roster[0] with { ActiveBonus = Bonus.FromThousandths(100) };
        roster[1] = roster[1] with { ActiveBonus = Bonus.FromThousandths(370) };
        Pcg32V1 rng = new(7UL, 9UL);

        RoundSimulationResult result = RoundSimulator.Simulate(roster, rng, Rules);

        foreach (RoundPlacement placement in result.Placements)
        {
            long expected = (long)placement.BasePoints.Thousandths * (RulesV1.BonusPercentScale + placement.ActiveBonus.Thousandths) / RulesV1.BonusPercentScale;
            placement.FinalPoints.Thousandths.ShouldBe((int)expected);
        }

        RoundPlacement? boosted = result.Placements.FirstOrDefault(p => string.Equals(p.Name, roster[0].Name, StringComparison.Ordinal));
        boosted.ShouldNotBeNull();
        boosted.ActiveBonus.Thousandths.ShouldBe(100);
    }

    [Fact]
    public void Simulate_SameSeedSameInput_ProducesIdenticalOrderAndChecksum()
    {
        IReadOnlyList<RoundAthleteInput> roster = BuildRoster(0);

        RoundSimulationResult first = RoundSimulator.Simulate(roster, new Pcg32V1(9001UL, 7002UL), Rules);
        RoundSimulationResult second = RoundSimulator.Simulate(roster, new Pcg32V1(9001UL, 7002UL), Rules);

        first.Checksum.ShouldBe(second.Checksum);
        first.Placements.Select(p => p.Name).ShouldBe(second.Placements.Select(p => p.Name).ToList());
        first.RngAfter.ShouldBe(second.RngAfter);
    }

    [Fact]
    public void Simulate_AdvancesRng_AndUsesOnlySaveRng()
    {
        IReadOnlyList<RoundAthleteInput> roster = BuildRoster(0);
        Pcg32V1 rng = new(55UL, 66UL);
        Pcg32State before = rng.Snapshot();

        RoundSimulationResult result = RoundSimulator.Simulate(roster, rng, Rules);

        result.RngAfter.ShouldNotBe(before);
        rng.Snapshot().ShouldBe(result.RngAfter);
    }

    [Fact]
    public void Simulate_CumulativeBefore_DrivesRankBeforeAndAfter()
    {
        List<RoundAthleteInput> roster = BuildRoster(0).ToList();
        roster[0] = roster[0] with { CumulativeBefore = Points.FromThousandths(200000) };
        roster[1] = roster[1] with { CumulativeBefore = Points.FromThousandths(100000) };
        Pcg32V1 rng = new(11UL, 12UL);

        RoundSimulationResult result = RoundSimulator.Simulate(roster, rng, Rules);

        RoundPlacement leader = result.Placements.Single(p => p.AthleteId == roster[0].AthleteId);
        leader.RankBefore.ShouldBe(1);
        leader.CumulativeAfter.Thousandths.ShouldBe(checked(200000 + leader.FinalPoints.Thousandths));

        foreach (RoundPlacement placement in result.Placements)
        {
            placement.CumulativeAfter.Thousandths.ShouldBe(placement.CumulativeBefore.Thousandths + placement.FinalPoints.Thousandths);
            placement.RankMovement.ShouldBe(placement.RankBefore - placement.RankAfter);
        }
    }

    [Fact]
    public void Simulate_RejectsDuplicateAthletesAndWrongCount()
    {
        List<RoundAthleteInput> roster = BuildRoster(0).ToList();
        roster[1] = roster[1] with { AthleteId = roster[0].AthleteId };
        Should.Throw<InvalidOperationException>(() => RoundSimulator.Simulate(roster, new Pcg32V1(1UL, 2UL), Rules));

        List<RoundAthleteInput> shortRoster = BuildRoster(0).Take(31).ToList();
        Should.Throw<InvalidOperationException>(() => RoundSimulator.Simulate(shortRoster, new Pcg32V1(1UL, 2UL), Rules));
    }

    [Fact]
    public void Checksum_DetectsOrderOrPointsChange()
    {
        IReadOnlyList<RoundAthleteInput> roster = BuildRoster(0);
        RoundSimulationResult result = RoundSimulator.Simulate(roster, new Pcg32V1(3UL, 4UL), Rules);

        List<RoundPlacement> reordered = [.. result.Placements];
        reordered.Reverse();
        string.Equals(RoundSimulator.ComputeChecksum(reordered), result.Checksum, StringComparison.Ordinal).ShouldBeFalse();

        RoundSimulator.ComputeChecksum(result.Placements).ShouldBe(result.Checksum);
    }

    private static IReadOnlyList<RoundAthleteInput> BuildRoster(int bonusThousandths)
    {
        List<RoundAthleteInput> roster = new(32);
        for (int i = 0; i < 32; i++)
        {
            roster.Add(new RoundAthleteInput(
                i + 1,
                $"Test Athlete {i:D2}",
                Bonus.FromThousandths(bonusThousandths),
                Points.Zero));
        }

        return roster;
    }
}
