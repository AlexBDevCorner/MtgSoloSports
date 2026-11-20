using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Scoring;

/// <summary>
/// MSS-151 golden equivalence: pins the exact pre-refactor persisted outputs of
/// both consumers of the shared round pipeline (the individual league round in
/// <see cref="RoundSimulator"/> and the team-event group round in
/// <see cref="TeamEvent"/>), including RNG consumption for the same seed. The
/// values below were captured from the pre-consolidation engines on this
/// branch; they must stay unchanged and must never be re-captured on failure.
/// </summary>
public sealed class RoundPipelineGoldenEquivalenceTests
{
    private const string LeagueChecksum = "16b73ed54943dcbcad3bebb867936c627856ce02089a2d350c98bbd11cdc3d0e";
    private const ulong LeagueRngAfterState = 10971593315701713316UL;
    private const string LeagueOrder =
        "29:1:29:10:19|27:2:27:14:13|20:3:20:7:13|6:4:6:1:5|21:5:21:16:5|26:6:26:22:4|14:7:14:11:3|10:8:10:6:4|25:9:25:23:2|7:10:7:4:3|2:11:2:2:0|17:12:17:20:-3|12:13:12:15:-3|16:14:16:19:-3|13:15:13:17:-4|8:16:8:9:-1|9:17:9:12:-3|1:18:1:3:-2|23:19:23:26:-3|31:20:31:31:0|4:21:4:5:-1|30:22:30:30:0|22:23:22:27:-5|15:24:15:21:-6|32:25:32:32:0|19:26:19:24:-5|28:27:28:29:-1|24:28:24:28:-4|11:29:11:18:-7|3:30:3:8:-5|5:31:5:13:-8|18:32:18:25:-7";

    private const string GroupChecksum = "c95db8c3db7d06d6f8b03e7fd962a8b0d108ffe96e5fce50b643d4d32b51ac45";
    private const ulong GroupRngAfterState = 10829672108415348026UL;
    private const string GroupOrder = "103:1:3:1:2|104:2:4:4:0|102:3:2:3:-1|101:4:1:2:-1";

    [Fact]
    public void RoundSimulator_MatchesPreRefactorGolden()
    {
        List<RoundAthleteInput> roster = new(32);
        for (int i = 0; i < 32; i++)
        {
            roster.Add(new RoundAthleteInput(
                i + 1,
                $"Pipeline Athlete {i:D2}",
                Bonus.FromThousandths(i % 4 == 0 ? 250 : i % 4 == 1 ? 75 : 0),
                Points.FromThousandths((31 - i) * 3000)));
        }

        RoundSimulationResult result = RoundSimulator.Simulate(roster, new Pcg32V1(151151UL, 42UL), RulesV1.CreateDefault());

        result.Checksum.ShouldBe(LeagueChecksum);
        result.RngAfter.State.ShouldBe(LeagueRngAfterState);
        result.RngAfter.Stream.ShouldBe(42UL);
        OrderFingerprint(result).ShouldBe(LeagueOrder);
    }

    [Fact]
    public void TeamEventGroupRound_MatchesPreRefactorGolden()
    {
        List<RoundAthleteInput> roster =
        [
            new(101, "Group Athlete Alice", Bonus.FromThousandths(0), Points.FromThousandths(40_000)),
            new(102, "Group Athlete Bob", Bonus.FromThousandths(120), Points.FromThousandths(30_000)),
            new(103, "Group Athlete Cira", Bonus.FromThousandths(250), Points.FromThousandths(30_000)),
            new(104, "Group Athlete Dorn", Bonus.FromThousandths(0), Points.FromThousandths(10_000)),
        ];

        RoundSimulationResult result = TeamEvent.SimulateGroupRound(
            roster, new Pcg32V1(151151UL, 43UL), RulesV1.CreateDefault(), groupSize: 4);

        result.Checksum.ShouldBe(GroupChecksum);
        result.RngAfter.State.ShouldBe(GroupRngAfterState);
        result.RngAfter.Stream.ShouldBe(43UL);
        OrderFingerprint(result).ShouldBe(GroupOrder);
    }

    private static string OrderFingerprint(RoundSimulationResult result) =>
        string.Join("|", result.Placements.Select(p =>
            $"{p.AthleteId}:{p.Position}:{p.RankBefore}:{p.RankAfter}:{p.RankMovement}"));
}
