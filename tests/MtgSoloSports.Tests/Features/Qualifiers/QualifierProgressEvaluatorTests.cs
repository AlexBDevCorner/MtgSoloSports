using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Qualifiers;

/// <summary>
/// MSS-075: authoritative qualifier progress without hardcoded counts.
/// </summary>
public sealed class QualifierProgressEvaluatorTests
{
    [Fact]
    public void ExpectedTotals_SingleTier_IsOneEvent()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        rules.FeederDivisionsPerColor.ShouldBe(1);
        QualifierProgressEvaluator.ExpectedEvents(rules).ShouldBe(1);
        QualifierProgressEvaluator.ExpectedTotals(rules).ShouldBe((rules.QualifierSize, rules.QualifierRounds));
        rules.QualifierSize.ShouldBe(32);
        rules.QualifierRounds.ShouldBe(16);
    }

    [Fact]
    public void ExpectedTotals_ThreeTier_IsSeventeenEvents()
    {
        RulesV1 rules = RulesV3.CreateDefault();
        rules.FeederDivisionsPerColor.ShouldBe(3);
        QualifierProgressEvaluator.ExpectedEvents(rules).ShouldBe(17);
        (int standings, int rounds) = QualifierProgressEvaluator.ExpectedTotals(rules);
        standings.ShouldBe(288);
        rounds.ShouldBe(272);
    }

    [Fact]
    public void IsEventComplete_RequiresFieldRoundsAndWinners()
    {
        RulesV1 rules = RulesV3.CreateDefault();
        List<QualifierStandingEntity> standings = CompleteStandings(32, 8);
        List<QualifierRoundEntity> rounds = CompleteRounds(16);
        QualifierProgressEvaluator.IsEventComplete(
            standings, rounds, QualifierBoundary.Superleague, rules, 3).ShouldBeTrue();

        List<QualifierStandingEntity> feederStandings = CompleteStandings(16, 8);
        QualifierProgressEvaluator.IsEventComplete(
            feederStandings, rounds, QualifierBoundary.Feeder1Feeder2, rules, 3).ShouldBeTrue();

        QualifierProgressEvaluator.IsEventComplete(
            [], [], QualifierBoundary.Superleague, rules, 3).ShouldBeFalse();
    }

    [Fact]
    public void ValidateEventNotCorrupt_StandingsWithoutRounds_Throws()
    {
        RulesV1 rules = RulesV3.CreateDefault();
        List<QualifierStandingEntity> standings = CompleteStandings(32, 8);
        Should.Throw<InvalidOperationException>(() => QualifierProgressEvaluator.ValidateEventNotCorrupt(
            standings, [], QualifierBoundary.Superleague, rules, 3));
    }

    [Fact]
    public void ValidateEventNotCorrupt_PartialRoundsWithoutStandings_IsValid()
    {
        RulesV1 rules = RulesV3.CreateDefault();
        Should.NotThrow(() => QualifierProgressEvaluator.ValidateEventNotCorrupt(
            [], CompleteRounds(5), QualifierBoundary.Superleague, rules, 3));
    }

    private static List<QualifierStandingEntity> CompleteStandings(int count, int qualified)
    {
        List<QualifierStandingEntity> rows = new(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new QualifierStandingEntity
            {
                FromSeasonId = 1,
                ToSeasonId = 2,
                QualifierBoundary = (int)QualifierBoundary.Superleague,
                QualifierSportingColor = -1,
                SaveAthleteId = i + 1,
                QualifierRank = i + 1,
                QualifierScoreThousandths = 1000,
                BaseScoreThousandths = 900,
                RoundWins = 0,
                RoundPlaceCountsJson = "[]",
                IsQualified = i < qualified,
                Role = 0,
                FromLeagueId = 1,
                FromSeasonRank = 1,
                SportingColor = 0,
            });
        }

        return rows;
    }

    private static List<QualifierRoundEntity> CompleteRounds(int count)
    {
        List<QualifierRoundEntity> rows = new(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new QualifierRoundEntity
            {
                FromSeasonId = 1,
                ToSeasonId = 2,
                QualifierBoundary = 0,
                QualifierSportingColor = -1,
                RoundNumber = i + 1,
                RulesVersion = 3,
                RngBeforeState = 1,
                RngBeforeStream = 2,
                RngAfterState = 3,
                RngAfterStream = 4,
                PayloadJson = "{}",
                PayloadChecksum = "abc",
            });
        }

        return rows;
    }
}
