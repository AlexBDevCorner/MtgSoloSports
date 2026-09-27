using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

public sealed class TeamEventTests
{
    [Fact]
    public void SimulateGroupRound_EightAthletes_UsesFirstEightScoringEntries()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<RoundAthleteInput> roster = [];
        for (int i = 1; i <= 8; i++)
        {
            roster.Add(new RoundAthleteInput(i, $"Athlete {i:00}", Bonus.Zero, Points.Zero));
        }

        Pcg32V1 rng = new(11UL, 22UL);
        RoundSimulationResult result = TeamEvent.SimulateGroupRound(roster, rng, rules, groupSize: 8);

        result.Placements.Count.ShouldBe(8);
        result.Placements.Select(p => p.Position).OrderBy(p => p).ShouldBe(Enumerable.Range(1, 8).ToList());
        foreach (RoundPlacement placement in result.Placements)
        {
            int expectedBase = rules.ScoringTable[placement.Position - 1] * RulesV1.FixedScale;
            placement.BasePoints.Thousandths.ShouldBe(expectedBase);
            placement.FinalPoints.Thousandths.ShouldBe(expectedBase);
        }
    }

    [Fact]
    public void SimulateGroupRound_VariableSize_SupportsTypeCupReuse()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<RoundAthleteInput> roster =
        [
            new(1, "Alpha", Bonus.Zero, Points.Zero),
            new(2, "Beta", Bonus.Zero, Points.Zero),
            new(3, "Gamma", Bonus.Zero, Points.Zero),
            new(4, "Delta", Bonus.Zero, Points.Zero),
        ];

        Pcg32V1 rng = new(33UL, 44UL);
        RoundSimulationResult result = TeamEvent.SimulateGroupRound(roster, rng, rules, groupSize: 4);

        result.Placements.Count.ShouldBe(4);
        result.Placements.Select(p => p.Position).OrderBy(p => p).ShouldBe([1, 2, 3, 4]);
    }

    [Fact]
    public void AccumulateLeg_RejectsAthleteChangingTeams()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<List<TeamEvent.TeamGroupRoundEntry>> rounds = [];
        for (int round = 0; round < 8; round++)
        {
            List<TeamEvent.TeamGroupRoundEntry> entries = [];
            for (int position = 1; position <= 8; position++)
            {
                int athlete = position;
                string team = athlete == 1 && round >= 4 ? "Blue" : "White";
                int basePoints = rules.ScoringTable[position - 1] * RulesV1.FixedScale;
                entries.Add(new TeamEvent.TeamGroupRoundEntry(
                    athlete, $"Athlete {athlete:00}", athlete, team, position, basePoints, basePoints));
            }

            rounds.Add(entries);
        }

        Should.Throw<InvalidOperationException>(() => TeamEvent.AccumulateLeg(rounds, 8, 8, rules));
    }

    [Fact]
    public void RankLeg_TiedScore_BreaksByRoundCountsThenBase()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        TeamEvent.TeamLegTotals ahead = new(
            1, "Alpha", 0, "White", 100_000, 90_000,
            [2, 0, 0, 0, 0, 0, 0, 6]);
        TeamEvent.TeamLegTotals behind = new(
            2, "Beta", 1, "Blue", 100_000, 95_000,
            [1, 0, 0, 0, 0, 0, 0, 7]);

        Pcg32V1 rng = new(55UL, 66UL);
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked = TeamEvent.RankLeg([ahead, behind, .. LegFillers()], rng, rules, 8);

        ranked.Single(r => r.AthleteId == 1).LegRank.ShouldBeLessThan(
            ranked.Single(r => r.AthleteId == 2).LegRank);
    }

    [Fact]
    public void BuildTeamInputs_TeamScore_IsSumOfFourLegs()
    {
        List<TeamEvent.TeamLegRanked> legs = [];
        for (int team = 0; team < 2; team++)
        {
            for (int leg = 0; leg < 4; leg++)
            {
                legs.Add(new TeamEvent.TeamLegRanked(
                    team * 10 + leg + 1,
                    $"T{team}L{leg}",
                    team,
                    team == 0 ? "White" : "Blue",
                    (leg % 2) + 1,
                    10_000 * (leg + 1),
                    9_000 * (leg + 1),
                    leg == 0 ? 1 : 0,
                    [leg == 0 ? 1 : 0, leg == 0 ? 0 : 1]));
            }
        }

        IReadOnlyList<TeamEvent.TeamScoreInput> inputs = TeamEvent.BuildTeamInputs(legs, groupSize: 2);

        inputs.Count.ShouldBe(2);
        inputs.Single(t => t.TeamId == 0).TotalScoreThousandths.ShouldBe(100_000);
        inputs.Single(t => t.TeamId == 1).TotalScoreThousandths.ShouldBe(100_000);
    }

    [Fact]
    public void RankTeams_TiedScore_PrefersMoreGroupWins()
    {
        TeamEvent.TeamScoreInput moreWins = new(
            0, "White", 100_000, 90_000,
            [2, 1, 1, 0, 0, 0, 0, 0],
            [4, 4, 4, 4, 4, 4, 4, 4]);
        TeamEvent.TeamScoreInput fewerWins = new(
            1, "Blue", 100_000, 95_000,
            [1, 2, 1, 0, 0, 0, 0, 0],
            [4, 4, 4, 4, 4, 4, 4, 4]);

        Pcg32V1 rng = new(77UL, 88UL);
        IReadOnlyList<TeamEvent.TeamRanked> ranked = TeamEvent.RankTeams([moreWins, fewerWins], rng);

        ranked[0].TeamId.ShouldBe(0);
        ranked[0].TeamRank.ShouldBe(1);
        ranked[1].TeamRank.ShouldBe(2);
    }

    [Fact]
    public void RankTeams_FullTie_IsDeterministicWithSeededDraw()
    {
        TeamEvent.TeamScoreInput white = new(
            0, "White", 100_000, 90_000,
            [1, 1, 1, 1, 0, 0, 0, 0],
            [4, 4, 4, 4, 4, 4, 4, 4]);
        TeamEvent.TeamScoreInput blue = new(
            1, "Blue", 100_000, 90_000,
            [1, 1, 1, 1, 0, 0, 0, 0],
            [4, 4, 4, 4, 4, 4, 4, 4]);

        Pcg32V1 first = new(99UL, 111UL);
        Pcg32V1 second = new(99UL, 111UL);
        IReadOnlyList<TeamEvent.TeamRanked> firstRanked = TeamEvent.RankTeams([white, blue], first);
        IReadOnlyList<TeamEvent.TeamRanked> secondRanked = TeamEvent.RankTeams([blue, white], second);

        firstRanked.Select(r => r.TeamId).ShouldBe(secondRanked.Select(r => r.TeamId).ToList());
    }

    private static List<TeamEvent.TeamLegTotals> LegFillers()
    {
        List<TeamEvent.TeamLegTotals> fillers = [];
        for (int i = 3; i <= 8; i++)
        {
            fillers.Add(new TeamEvent.TeamLegTotals(
                i, $"Filler {i:00}", i, $"Team {i:00}", 1_000 * i, 900 * i,
                [0, 0, 0, 0, 0, 0, 0, 8]));
        }

        return fillers;
    }
}
