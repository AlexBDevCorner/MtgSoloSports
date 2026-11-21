using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.Stages;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Rules;

/// <summary>
/// MSS-152: rules validation runs once per snapshot load (the
/// RulesSnapshotCodec decode path validates through Create), never per
/// round call. This minimal counted-validation seam proves the kernel
/// round paths no longer re-run the ~80-invariant validation; corrupt
/// snapshots still abort at the load boundary, which is covered by
/// RulesSnapshotCorruptionTests.
/// </summary>
public sealed class RulesValidationCadenceTests
{
    private sealed class CountingValidationRules : RulesV2
    {
        public CountingValidationRules(RulesV2 template)
            : base(template.ExtractCore(), template.ExtractSportingSnapshot())
        {
        }

        public int ValidateCalls { get; private set; }

        public override void Validate()
        {
            ValidateCalls++;
            base.Validate();
        }
    }

    [Fact]
    public void KernelRoundPaths_DoNotRevalidateRules()
    {
        CountingValidationRules rules = new(RulesV2.CreateDefault());
        Pcg32V1 rng = new(1UL, 2UL);

        // One full league stage worth of league rounds plus team-event group
        // rounds and one stage ranking: none of the per-round hot paths may
        // re-run the snapshot validation.
        for (int round = 0; round < 16; round++)
        {
            RoundSimulator.Simulate(BuildRoster(32), rng, rules);
            TeamEvent.SimulateGroupRound(BuildRoster(8), rng, rules, groupSize: 8);
        }

        StageCalculator.RankWithFieldSize(BuildStageTotals(), rng, rules, LeagueLevel.Superleague, 32);

        rules.ValidateCalls.ShouldBe(0);

        // The rules object still validates on demand, and the snapshot-load
        // boundary (Create/decode) runs exactly that validation once.
        rules.Validate();
        rules.ValidateCalls.ShouldBe(1);
        RulesV1.CreateDefault().Version.ShouldBe(1);
    }

    private static IReadOnlyList<RoundAthleteInput> BuildRoster(int size)
    {
        List<RoundAthleteInput> roster = new(size);
        for (int i = 0; i < size; i++)
        {
            roster.Add(new RoundAthleteInput(
                i + 1,
                $"Test Athlete {i:D2}",
                Bonus.Zero,
                Points.Zero));
        }

        return roster;
    }

    private static List<StageAthleteTotals> BuildStageTotals()
    {
        List<StageAthleteTotals> totals = new(32);
        for (int i = 1; i <= 32; i++)
        {
            int[] counts = new int[32];
            counts[i - 1] = 16;
            totals.Add(new StageAthleteTotals(
                i,
                $"Athlete {i:D2}",
                100_000 - i,
                50_000,
                counts.ToList()));
        }

        return totals;
    }
}
