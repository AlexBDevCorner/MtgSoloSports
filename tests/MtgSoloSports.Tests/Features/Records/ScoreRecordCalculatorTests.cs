using MtgSoloSports.Features.Records;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Records;

public sealed class ScoreRecordCalculatorTests
{
    private static Dictionary<int, string> Names(params (int Id, string Name)[] entries)
    {
        return entries.ToDictionary(e => e.Id, e => e.Name);
    }

    private static List<ScoreRecordCalculator.LeagueRoundOccurrence> EmptyRounds() => [];

    private static List<ScoreRecordCalculator.LeagueStageOccurrence> EmptyStages() => [];

    private static List<ScoreRecordCalculator.LeaguePointsOccurrence> EmptyPoints() => [];

    private static List<ScoreRecordCalculator.CupIndividualRoundOccurrence> EmptyCupRounds() => [];

    private static List<ScoreRecordCalculator.CupIndividualStageOccurrence> EmptyCupStages() => [];

    private static List<ScoreRecordCalculator.QualifierRoundOccurrence> EmptyQualifierRounds() => [];

    private static List<ScoreRecordCalculator.QualifierStageOccurrence> EmptyQualifierStages() => [];

    private static List<ScoreRecordCalculator.TeamLegRoundOccurrence> EmptyLegRounds() => [];

    private static List<ScoreRecordCalculator.TeamLegStageOccurrence> EmptyLegStages() => [];

    private static List<ScoreRecordCalculator.TeamTotalOccurrence> EmptyTotals() => [];

    private static List<ScoreRecordCalculator.TeamRoundOccurrence> EmptyTeamRounds() => [];

    private static IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> ComputeAll(
        Dictionary<int, string> names,
        List<ScoreRecordCalculator.LeagueRoundOccurrence>? rounds = null,
        List<ScoreRecordCalculator.LeagueStageOccurrence>? stages = null,
        List<ScoreRecordCalculator.LeaguePointsOccurrence>? points = null,
        List<ScoreRecordCalculator.CupIndividualRoundOccurrence>? cupRounds = null,
        List<ScoreRecordCalculator.CupIndividualStageOccurrence>? cupStages = null,
        List<ScoreRecordCalculator.QualifierRoundOccurrence>? qualifierRounds = null,
        List<ScoreRecordCalculator.QualifierStageOccurrence>? qualifierStages = null,
        List<ScoreRecordCalculator.TeamLegRoundOccurrence>? colourLegRounds = null,
        List<ScoreRecordCalculator.TeamLegStageOccurrence>? colourLegStages = null,
        List<ScoreRecordCalculator.TeamTotalOccurrence>? colourTotals = null,
        List<ScoreRecordCalculator.TeamRoundOccurrence>? colourTeamRounds = null,
        List<ScoreRecordCalculator.TeamLegRoundOccurrence>? typeLegRounds = null,
        List<ScoreRecordCalculator.TeamLegStageOccurrence>? typeLegStages = null,
        List<ScoreRecordCalculator.TeamTotalOccurrence>? typeTotals = null,
        List<ScoreRecordCalculator.TeamRoundOccurrence>? typeTeamRounds = null)
    {
        return ScoreRecordCalculator.ComputeAll(
            names,
            rounds ?? EmptyRounds(),
            stages ?? EmptyStages(),
            points ?? EmptyPoints(),
            cupRounds ?? EmptyCupRounds(),
            cupStages ?? EmptyCupStages(),
            qualifierRounds ?? EmptyQualifierRounds(),
            qualifierStages ?? EmptyQualifierStages(),
            colourLegRounds ?? EmptyLegRounds(),
            colourLegStages ?? EmptyLegStages(),
            colourTotals ?? EmptyTotals(),
            colourTeamRounds ?? EmptyTeamRounds(),
            typeLegRounds ?? EmptyLegRounds(),
            typeLegStages ?? EmptyLegStages(),
            typeTotals ?? EmptyTotals(),
            typeTeamRounds ?? EmptyTeamRounds());
    }

    [Fact]
    public void All_ContainsThirtyNineExplicitKeys()
    {
        ScoreRecordKey.All.Count.ShouldBe(39);
        ScoreRecordKey.All.Distinct(StringComparer.Ordinal).Count().ShouldBe(39);
        // Required baseline keys exist.
        ScoreRecordKey.All.Contains(ScoreRecordKey.LeagueSingleRound(ScoreRecordKey.ScopeWhite), StringComparer.Ordinal).ShouldBeTrue();
        ScoreRecordKey.All.Contains(ScoreRecordKey.LeagueStageBest(ScoreRecordKey.ScopeWhite), StringComparer.Ordinal).ShouldBeTrue();
        ScoreRecordKey.All.Contains(ScoreRecordKey.LeaguePointsBest(ScoreRecordKey.ScopeWhite), StringComparer.Ordinal).ShouldBeTrue();
        ScoreRecordKey.All.Contains(ScoreRecordKey.LeagueSingleRound(ScoreRecordKey.ScopeSuperleague), StringComparer.Ordinal).ShouldBeTrue();
        ScoreRecordKey.All.Contains(ScoreRecordKey.ColourCupIndividualRoundBest, StringComparer.Ordinal).ShouldBeTrue();
        ScoreRecordKey.All.Contains(ScoreRecordKey.ColourCupTeamLegRoundBest, StringComparer.Ordinal).ShouldBeTrue();
        ScoreRecordKey.All.Contains(ScoreRecordKey.ColourCupTeamTotalBest, StringComparer.Ordinal).ShouldBeTrue();
        ScoreRecordKey.All.Contains(ScoreRecordKey.TypeCupTeamTotalBest, StringComparer.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void Categories_DoNotMixIncompatibleFormats()
    {
        ScoreRecordKey.CategoryOf(ScoreRecordKey.LeagueSingleRound(ScoreRecordKey.ScopeWhite)).ShouldBe("League");
        ScoreRecordKey.CategoryOf(ScoreRecordKey.LeagueSingleRound(ScoreRecordKey.ScopeSuperleague)).ShouldBe("League");
        ScoreRecordKey.CategoryOf(ScoreRecordKey.ColourCupIndividualRoundBest).ShouldBe("Individual Cups");
        ScoreRecordKey.CategoryOf(ScoreRecordKey.QualifierSingleRoundBest).ShouldBe("Individual Cups");
        ScoreRecordKey.CategoryOf(ScoreRecordKey.ColourCupTeamTotalBest).ShouldBe("Team Cups");
        ScoreRecordKey.CategoryOf(ScoreRecordKey.TypeCupTeamTotalBest).ShouldBe("Team Cups");
        // League scopes keep feeders and Superleague separate.
        string whiteScope = ScoreRecordKey.ScopeOf(ScoreRecordKey.LeagueSingleRound(ScoreRecordKey.ScopeWhite));
        string superScope = ScoreRecordKey.ScopeOf(ScoreRecordKey.LeagueSingleRound(ScoreRecordKey.ScopeSuperleague));
        string.Equals(whiteScope, superScope, StringComparison.Ordinal).ShouldBeFalse();
        // Team scopes keep Colour and Type Cups separate.
        string colourScope = ScoreRecordKey.ScopeOf(ScoreRecordKey.ColourCupTeamTotalBest);
        string typeScope = ScoreRecordKey.ScopeOf(ScoreRecordKey.TypeCupTeamTotalBest);
        string.Equals(colourScope, typeScope, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void LeagueRound_PerScopeMaxWithEarliestOccurrence()
    {
        Dictionary<int, string> names = Names((1, "Alpha"), (2, "Beta"));
        List<ScoreRecordCalculator.LeagueRoundOccurrence> rounds =
        [
            new("white", 1, 1, 1, 1, 80000, "White League"),
            new("white", 1, 1, 2, 1, 82000, "White League"),
            new("white", 1, 2, 1, 2, 82000, "White League"),
            new("blue", 1, 1, 1, 1, 90000, "Blue League"),
        ];
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(names, rounds: rounds);
        ScoreRecordCalculator.ScoringRecordResult white =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.LeagueSingleRound("white"), StringComparison.Ordinal));
        white.Value.ShouldBe(82000);
        white.Holders.Count.ShouldBe(2);
        // Deterministic ordering by name then id: Alpha (1) before Beta (2).
        white.Holders[0].SaveAthleteId.ShouldBe(1);
        white.Holders[1].SaveAthleteId.ShouldBe(2);
        // Earliest occurrence per athlete: Alpha's max at stage 1 round 2.
        white.Holders[0].SeasonNumber.ShouldBe(1);
        white.Holders[0].StageNumber.ShouldBe(1);
        white.Holders[0].RoundNumber.ShouldBe(2);
        white.Holders[0].Competition.ShouldBe("White League");
        // Blue scope is separate and does not leak into white.
        ScoreRecordCalculator.ScoringRecordResult blue =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.LeagueSingleRound("blue"), StringComparison.Ordinal));
        blue.Value.ShouldBe(90000);
        blue.Holders.Count.ShouldBe(1);
    }

    [Fact]
    public void LeagueStage_PerScopeMax()
    {
        Dictionary<int, string> names = Names((1, "Alpha"), (2, "Beta"));
        List<ScoreRecordCalculator.LeagueStageOccurrence> stages =
        [
            new("white", 1, 5, 1, 1200000, "White League"),
            new("white", 2, 3, 2, 1250000, "White League"),
            new("white", 2, 7, 1, 1250000, "White League"),
        ];
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(names, stages: stages);
        ScoreRecordCalculator.ScoringRecordResult white =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.LeagueStageBest("white"), StringComparison.Ordinal));
        white.Value.ShouldBe(1250000);
        white.Holders.Count.ShouldBe(2);
        white.Holders[0].SaveAthleteId.ShouldBe(1);
        white.Holders[1].SaveAthleteId.ShouldBe(2);
        // Superleague stays vacant when no occurrences exist for that scope.
        ScoreRecordCalculator.ScoringRecordResult super =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.LeagueStageBest("superleague"), StringComparison.Ordinal));
        super.Value.ShouldBe(0);
        super.Holders.ShouldBeEmpty();
    }

    [Fact]
    public void LeaguePoints_ScopedPerLeague()
    {
        Dictionary<int, string> names = Names((1, "Alpha"), (2, "Beta"));
        List<ScoreRecordCalculator.LeaguePointsOccurrence> points =
        [
            new("white", 1, 1, 500000, "White League"),
            new("white", 2, 2, 510000, "White League"),
            new("superleague", 2, 1, 600000, "Superleague"),
        ];
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(names, points: points);
        ScoreRecordCalculator.ScoringRecordResult white =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.LeaguePointsBest("white"), StringComparison.Ordinal));
        white.Value.ShouldBe(510000);
        white.Holders.Single().SaveAthleteId.ShouldBe(2);
        white.Holders.Single().SeasonNumber.ShouldBe(2);
        ScoreRecordCalculator.ScoringRecordResult super =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.LeaguePointsBest("superleague"), StringComparison.Ordinal));
        super.Value.ShouldBe(600000);
        super.Holders.Single().SaveAthleteId.ShouldBe(1);
    }

    [Fact]
    public void ColourIndividualRound_PreservesTies()
    {
        Dictionary<int, string> names = Names((1, "Beta"), (2, "Alpha"));
        List<ScoreRecordCalculator.CupIndividualRoundOccurrence> rounds =
        [
            new(1, 3, 1, 85000),
            new(1, 7, 2, 85000),
            new(3, 2, 1, 84000),
        ];
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(names, cupRounds: rounds);
        ScoreRecordCalculator.ScoringRecordResult record =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.ColourCupIndividualRoundBest, StringComparison.Ordinal));
        record.Value.ShouldBe(85000);
        record.Holders.Count.ShouldBe(2);
        // Ordered by name: Alpha (2) before Beta (1).
        record.Holders[0].SaveAthleteId.ShouldBe(2);
        record.Holders[1].SaveAthleteId.ShouldBe(1);
        record.Holders[0].SeasonNumber.ShouldBe(1);
        record.Holders[0].RoundNumber.ShouldBe(7);
        record.Holders[0].Competition.ShouldBe("Colour Cup individual");
    }

    [Fact]
    public void ColourIndividualStage_OldSeasonHolderSurvives()
    {
        Dictionary<int, string> names = Names((1, "Alpha"), (2, "Beta"));
        List<ScoreRecordCalculator.CupIndividualStageOccurrence> stages =
        [
            new(1, 1, 1300000),
            new(3, 2, 1200000),
        ];
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(names, cupStages: stages);
        ScoreRecordCalculator.ScoringRecordResult record =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.ColourCupIndividualStageBest, StringComparison.Ordinal));
        record.Value.ShouldBe(1300000);
        record.Holders.Single().SaveAthleteId.ShouldBe(1);
        record.Holders.Single().SeasonNumber.ShouldBe(1);
    }

    [Fact]
    public void Qualifier_RoundAndStageSeparate()
    {
        Dictionary<int, string> names = Names((1, "Alpha"));
        List<ScoreRecordCalculator.QualifierRoundOccurrence> rounds = [new(2, 3, 5, 1, 81000)];
        List<ScoreRecordCalculator.QualifierStageOccurrence> stages = [new(2, 3, 1, 1290000)];
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(
            names, qualifierRounds: rounds, qualifierStages: stages);
        computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.QualifierSingleRoundBest, StringComparison.Ordinal)).Value.ShouldBe(81000);
        computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.QualifierStageBest, StringComparison.Ordinal)).Value.ShouldBe(1290000);
    }

    [Fact]
    public void TeamLegRound_IncludesGroupAndTeamContext()
    {
        Dictionary<int, string> names = Names((1, "Alpha"), (2, "Beta"));
        List<ScoreRecordCalculator.TeamLegRoundOccurrence> legs =
        [
            new(1, 1, 3, 1, "White", 30000),
            new(1, 2, 3, 2, "Blue", 32000),
        ];
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(names, colourLegRounds: legs);
        ScoreRecordCalculator.ScoringRecordResult record =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.ColourCupTeamLegRoundBest, StringComparison.Ordinal));
        record.Value.ShouldBe(32000);
        record.Holders.Single().SaveAthleteId.ShouldBe(2);
        record.Holders.Single().TeamKey.ShouldBe("Blue");
        record.Holders.Single().GroupNumber.ShouldBe(2);
        record.Holders.Single().RoundNumber.ShouldBe(3);
    }

    [Fact]
    public void TeamLegStage_ColourAndTypeSeparate()
    {
        Dictionary<int, string> names = Names((1, "Alpha"), (2, "Beta"));
        List<ScoreRecordCalculator.TeamLegStageOccurrence> colour = [new(1, 1, 1, "White", 250000)];
        List<ScoreRecordCalculator.TeamLegStageOccurrence> type = [new(2, 1, 2, "Elf", 260000)];
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(
            names, colourLegStages: colour, typeLegStages: type);
        computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.ColourCupTeamLegStageBest, StringComparison.Ordinal)).Value.ShouldBe(250000);
        computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.TypeCupTeamLegStageBest, StringComparison.Ordinal)).Value.ShouldBe(260000);
    }

    [Fact]
    public void TeamTotals_HoldersAreTeamsOrderedByName()
    {
        Dictionary<int, string> names = Names((1, "Alpha"));
        List<ScoreRecordCalculator.TeamTotalOccurrence> colour =
        [
            new(1, "White", 1000000),
            new(3, "Blue", 1000000),
            new(2, "Red", 900000),
        ];
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(names, colourTotals: colour);
        ScoreRecordCalculator.ScoringRecordResult record =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.ColourCupTeamTotalBest, StringComparison.Ordinal));
        record.Value.ShouldBe(1000000);
        record.Holders.Count.ShouldBe(2);
        record.Holders[0].SaveAthleteId.ShouldBeNull();
        record.Holders[0].TeamKey.ShouldBe("Blue");
        record.Holders[1].TeamKey.ShouldBe("White");
        record.Holders[0].SeasonNumber.ShouldBe(3);
    }

    [Fact]
    public void TeamRound_AggregatedHoldersAreTeams()
    {
        Dictionary<int, string> names = Names((1, "Alpha"));
        List<ScoreRecordCalculator.TeamRoundOccurrence> rounds =
        [
            new(1, 3, "White", 120000),
            new(1, 5, "Blue", 120000),
        ];
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(names, colourTeamRounds: rounds);
        ScoreRecordCalculator.ScoringRecordResult record =
            computed.Single(r => string.Equals(r.RecordKey, ScoreRecordKey.ColourCupTeamRoundBest, StringComparison.Ordinal));
        record.Value.ShouldBe(120000);
        record.Holders.Count.ShouldBe(2);
        record.Holders[0].TeamKey.ShouldBe("Blue");
        record.Holders[1].TeamKey.ShouldBe("White");
    }

    [Fact]
    public void Vacant_WhenNoOccurrences()
    {
        Dictionary<int, string> names = Names((1, "Alpha"));
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(names);
        foreach (ScoreRecordCalculator.ScoringRecordResult record in computed)
        {
            record.Value.ShouldBe(0);
            record.Holders.ShouldBeEmpty();
        }
    }

    [Fact]
    public void Corrupt_NegativeValueAborts()
    {
        Dictionary<int, string> names = Names((1, "Alpha"));
        List<ScoreRecordCalculator.LeagueRoundOccurrence> rounds =
            [new("white", 1, 1, 1, 1, -5, "White League")];
        Should.Throw<InvalidOperationException>(() => ComputeAll(names, rounds: rounds));
    }

    [Fact]
    public void Corrupt_UnknownAthleteAborts()
    {
        Dictionary<int, string> names = Names((1, "Alpha"));
        List<ScoreRecordCalculator.CupIndividualRoundOccurrence> rounds = [new(1, 1, 999, 80000)];
        Should.Throw<InvalidOperationException>(() => ComputeAll(names, cupRounds: rounds));
    }

    [Fact]
    public void ComputeAll_CoversEveryKnownKey()
    {
        Dictionary<int, string> names = Names((1, "Alpha"));
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed = ComputeAll(names);
        computed.Select(r => r.RecordKey).OrderBy(k => k, StringComparer.Ordinal)
            .ShouldBe(ScoreRecordKey.All.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }
}
