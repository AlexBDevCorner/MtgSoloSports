using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

/// <summary>
/// MSS-098: pure Chaos Cup format and eight-color quotas. Proves exact field,
/// stage sizes and advancement, per-color 128 quotas under uneven Superleague
/// distribution, first-stage 4/color balance, exit-stage categories without
/// fabricated ranks, and that existing rule snapshots still load.
/// </summary>
public sealed class ChaosCupFormatTests
{
    [Fact]
    public void Format_DefinesExactFieldAndGroups()
    {
        ChaosCupFormat.FieldSize.ShouldBe(1024);
        ChaosCupFormat.GroupSize.ShouldBe(32);
        ChaosCupFormat.RoundsPerGroup.ShouldBe(16);
        ChaosCupFormat.AdvancingPerGroup.ShouldBe(16);
        ChaosCupFormat.FinalFieldSize.ShouldBe(32);
        ChaosCupFormat.EntrantsPerColor.ShouldBe(128);
        ChaosCupFormat.FirstStagePerColorPerGroup.ShouldBe(4);
        ChaosCupFormat.StageGroupCounts.ShouldBe([32, 16, 8, 4, 2, 1]);
        ChaosCupFormat.StageCount.ShouldBe(6);
        ChaosCupFormat.ChaosFormatVersion.ShouldBe(1);
    }

    [Theory]
    [InlineData(0, 32, 1024, 512, 512)]
    [InlineData(1, 16, 512, 256, 256)]
    [InlineData(2, 8, 256, 128, 128)]
    [InlineData(3, 4, 128, 64, 64)]
    [InlineData(4, 2, 64, 32, 32)]
    [InlineData(5, 1, 32, 0, 0)]
    public void StageSizes_MatchApprovedFormat(int stage, int groups, int entrants, int advancing, int eliminated)
    {
        ChaosCupFormat.GroupCountForStage(stage).ShouldBe(groups);
        ChaosCupFormat.EntrantCountForStage(stage).ShouldBe(entrants);
        ChaosCupFormat.AdvancingCountForStage(stage).ShouldBe(advancing);
        ChaosCupFormat.EliminatedCountForStage(stage).ShouldBe(eliminated);
        ChaosCupFormat.IsFinalStage(stage).ShouldBe(stage == 5);
    }

    [Theory]
    [InlineData(0, ChaosCupFormat.ChaosExitStage.EliminatedInStageWith32Groups)]
    [InlineData(1, ChaosCupFormat.ChaosExitStage.EliminatedInStageWith16Groups)]
    [InlineData(2, ChaosCupFormat.ChaosExitStage.EliminatedInStageWith8Groups)]
    [InlineData(3, ChaosCupFormat.ChaosExitStage.EliminatedInStageWith4Groups)]
    [InlineData(4, ChaosCupFormat.ChaosExitStage.EliminatedInStageWith2Groups)]
    public void ExitStage_MapsEliminationWithoutGlobalRanks(int stage, ChaosCupFormat.ChaosExitStage expected)
    {
        ChaosCupFormat.ExitStageForElimination(stage).ShouldBe(expected);
    }

    [Fact]
    public void ExitStage_FinalHasNoEliminationCategory()
    {
        Should.Throw<InvalidOperationException>(() => ChaosCupFormat.ExitStageForElimination(5));
        Enum.GetValues<ChaosCupFormat.ChaosExitStage>().Length.ShouldBe(6);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void InvalidStageIndex_Rejected(int stage)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ChaosCupFormat.GroupCountForStage(stage));
        Should.Throw<ArgumentOutOfRangeException>(() => ChaosCupFormat.EntrantCountForStage(stage));
    }

    [Fact]
    public void Quotas_NormalSeason800Active_UnevenSuperleague()
    {
        // Feeders 96/color plus uneven Superleague with no color quotas.
        Dictionary<SportingColor, int> superleague = new()
        {
            [SportingColor.White] = 10,
            [SportingColor.Blue] = 6,
            [SportingColor.Black] = 4,
            [SportingColor.Red] = 2,
            [SportingColor.Green] = 0,
            [SportingColor.Multicolor] = 3,
            [SportingColor.Hybrid] = 5,
            [SportingColor.Colorless] = 2,
        };
        superleague.Values.Sum().ShouldBe(32);

        Dictionary<SportingColor, int> active = superleague.ToDictionary(kv => kv.Key, kv => 96 + kv.Value);
        active.Values.Sum().ShouldBe(800);

        IReadOnlyDictionary<SportingColor, int> invitations = ChaosCupFormat.ComputePoolInvitations(active);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            invitations[color].ShouldBe(128 - active[color]);
        }

        invitations.Values.Sum().ShouldBe(224);
        ChaosCupFormat.TotalPoolInvitations(800).ShouldBe(224);

        // Real pools hold 256-active per color, always enough for 128-active.
        Dictionary<SportingColor, int> pool = active.ToDictionary(kv => kv.Key, kv => 256 - kv.Value);
        Should.NotThrow(() => ChaosCupFormat.ValidatePoolAvailability(active, pool));
    }

    [Fact]
    public void Quotas_Season1_768Active()
    {
        Dictionary<SportingColor, int> active = Enum.GetValues<SportingColor>().ToDictionary(c => c, _ => 96);
        IReadOnlyDictionary<SportingColor, int> invitations = ChaosCupFormat.ComputePoolInvitations(active);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            invitations[color].ShouldBe(32);
        }

        invitations.Values.Sum().ShouldBe(256);
        ChaosCupFormat.TotalPoolInvitations(768).ShouldBe(256);
    }

    [Fact]
    public void Quotas_LegacyV1Season1_256Active()
    {
        Dictionary<SportingColor, int> active = Enum.GetValues<SportingColor>().ToDictionary(c => c, _ => 32);
        IReadOnlyDictionary<SportingColor, int> invitations = ChaosCupFormat.ComputePoolInvitations(active);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            invitations[color].ShouldBe(96);
        }

        invitations.Values.Sum().ShouldBe(768);
        ChaosCupFormat.TotalPoolInvitations(256).ShouldBe(768);
    }

    [Fact]
    public void Quotas_LegacyV1PostInaugural_288ActiveUneven()
    {
        Dictionary<SportingColor, int> superleague = new()
        {
            [SportingColor.White] = 8,
            [SportingColor.Blue] = 6,
            [SportingColor.Black] = 4,
            [SportingColor.Red] = 4,
            [SportingColor.Green] = 2,
            [SportingColor.Multicolor] = 2,
            [SportingColor.Hybrid] = 3,
            [SportingColor.Colorless] = 3,
        };
        Dictionary<SportingColor, int> active = superleague.ToDictionary(kv => kv.Key, kv => 32 + kv.Value);
        active.Values.Sum().ShouldBe(288);

        IReadOnlyDictionary<SportingColor, int> invitations = ChaosCupFormat.ComputePoolInvitations(active);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            invitations[color].ShouldBe(128 - active[color]);
        }

        invitations.Values.Sum().ShouldBe(736);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(129)]
    public void PoolInvitations_InvalidActive_Rejected(int active)
    {
        Should.Throw<InvalidOperationException>(() => ChaosCupFormat.PoolInvitationsForColor(active));
    }

    [Fact]
    public void PoolAvailability_TooSmall_Rejected()
    {
        Dictionary<SportingColor, int> active = Enum.GetValues<SportingColor>().ToDictionary(c => c, _ => 96);
        Dictionary<SportingColor, int> pool = Enum.GetValues<SportingColor>().ToDictionary(c => c, _ => 160);
        pool[SportingColor.White] = 31;
        Should.Throw<InvalidOperationException>(() => ChaosCupFormat.ValidatePoolAvailability(active, pool));
    }

    [Fact]
    public void EntrantField_Valid1024With128PerColor_Passes()
    {
        List<ChaosCupFormat.ChaosEntrant> field = BuildBalancedField();
        Should.NotThrow(() => ChaosCupFormat.ValidateEntrantField(field));
    }

    [Fact]
    public void EntrantField_Duplicate_Rejected()
    {
        List<ChaosCupFormat.ChaosEntrant> field = BuildBalancedField();
        field[0] = field[1];
        Should.Throw<InvalidOperationException>(() => ChaosCupFormat.ValidateEntrantField(field));
    }

    [Fact]
    public void EntrantField_WrongPerColor_Rejected()
    {
        List<ChaosCupFormat.ChaosEntrant> field = BuildBalancedField();
        field[0] = field[0] with { Color = SportingColor.Blue };
        Should.Throw<InvalidOperationException>(() => ChaosCupFormat.ValidateEntrantField(field));
    }

    [Fact]
    public void EntrantField_UnknownColor_Rejected()
    {
        List<ChaosCupFormat.ChaosEntrant> field = BuildBalancedField();
        field[0] = new ChaosCupFormat.ChaosEntrant(field[0].AthleteId, (SportingColor)99);
        Should.Throw<InvalidOperationException>(() => ChaosCupFormat.ValidateEntrantField(field));
    }

    [Fact]
    public void FirstStageGroups_Balanced4PerColor_Passes()
    {
        List<ChaosCupFormat.ChaosEntrant> field = BuildBalancedField();
        List<IReadOnlyList<ChaosCupFormat.ChaosEntrant>> groups = BuildBalancedFirstStage(field);
        Should.NotThrow(() => ChaosCupFormat.ValidateFirstStageGroups(field, groups));
    }

    [Fact]
    public void FirstStageGroups_UnbalancedColor_Rejected()
    {
        List<ChaosCupFormat.ChaosEntrant> field = BuildBalancedField();
        List<IReadOnlyList<ChaosCupFormat.ChaosEntrant>> groups = BuildBalancedFirstStage(field);
        // Swap one White with one Blue across groups 0/1 to break 4/color.
        List<ChaosCupFormat.ChaosEntrant> first = groups[0].ToList();
        List<ChaosCupFormat.ChaosEntrant> second = groups[1].ToList();
        int whiteInFirst = first.FindIndex(e => e.Color == SportingColor.White);
        int blueInSecond = second.FindIndex(e => e.Color == SportingColor.Blue);
        (first[whiteInFirst], second[blueInSecond]) = (second[blueInSecond], first[whiteInFirst]);
        groups[0] = first;
        groups[1] = second;
        Should.Throw<InvalidOperationException>(() => ChaosCupFormat.ValidateFirstStageGroups(field, groups));
    }

    [Fact]
    public void LaterStages_AcceptPureRandomColorMix()
    {
        // Stage 1 holds 512 entrants in 16 groups; uneven colors are legal.
        List<ChaosCupFormat.ChaosEntrant> stage = Enumerable.Range(1, 512)
            .Select(id => new ChaosCupFormat.ChaosEntrant(id, (SportingColor)((id - 1) % 2 == 0 ? 0 : 1)))
            .ToList();
        List<IReadOnlyList<ChaosCupFormat.ChaosEntrant>> groups = Enumerable.Range(0, 16)
            .Select(g => (IReadOnlyList<ChaosCupFormat.ChaosEntrant>)stage.Skip(g * 32).Take(32).ToList())
            .ToList();
        Should.NotThrow(() => ChaosCupFormat.ValidateStageGroups(1, stage, groups));
    }

    [Fact]
    public void Format_MatchesVersionedRules()
    {
        Should.NotThrow(() => ChaosCupFormat.ValidateFormatAgainstRules(RulesV1.CreateDefault()));
        Should.NotThrow(() => ChaosCupFormat.ValidateFormatAgainstRules(RulesV2.CreateDefault()));
        Should.NotThrow(() => ChaosCupFormat.ValidateFormatAgainstRules(RulesV3.CreateDefault()));
    }

    [Fact]
    public void ExistingSnapshots_LoadAndMatchFormat()
    {
        string legacyJson = """{"version":1,"algorithm":"Pcg32V1","algorithmVersion":1,"sportingColorCount":8,"athletesPerSportingColor":256,"totalAthletesInSave":2048,"regularLeagueCount":8,"leagueSize":32,"superleagueSize":32,"stagesPerSeason":32,"roundsPerStage":16,"qualifierSize":32,"qualifierRounds":16,"qualifierWinners":8,"superleagueSafeCount":16,"superleagueRelegatedCount":8,"superleagueQualifierIncumbentCount":8,"feederAutoPromotedCount":8,"feederQualifierCount":24,"inauguralQualifiedPerLeague":4,"superleagueBonusMultiplier":2,"colorCupColorCount":8,"colorCupTeamSize":4,"colorCupIndividualRounds":16,"colorCupTeamGroupRounds":8,"typeCupMinTeamSize":4,"typeCupGroupRounds":8,"recentFormStageCount":10,"cupBonusWeightPermille":350,"cupPerformanceWeightPermille":300,"cupFormWeightPermille":250,"cupPrestigeWeightPermille":100,"cupPrestigeFeederTitlePoints":100,"cupPrestigeSuperleagueTitlePoints":300,"cupPrestigeSuperleagueAppearancePoints":20,"cupPrestigeStageWinPoints":10,"cupPrestigeStageSecondPoints":5,"cupPrestigeStageThirdPoints":2,"cupPrestigeOtherMajorHonourPoints":150,"scoringTable":[77,67,58,50,43,37,32,28,25,23,22,21,20,19,18,17,16,15,14,13,12,11,10,9,8,7,6,5,4,3,2,1],"roundBonusThousandths":[100,90,80,70,60,50,40,30,20,10],"stageBonusThousandths":[200,180,160,140,120,100,80,60,40,20],"bonusAgeWeightsThousandths":[1000,800,600,400,200,0],"recentFormWeights":[1,2,3,4,5,6,7,8,9,10]}""";
        RulesV1 legacy = RulesSnapshotCodec.Decode(legacyJson);
        Should.NotThrow(() => legacy.Validate());
        Should.NotThrow(() => ChaosCupFormat.ValidateFormatAgainstRules(legacy));

        foreach (RulesV1 rules in new RulesV1[] { RulesV1.CreateDefault(), RulesV2.CreateDefault(), RulesV3.CreateDefault() })
        {
            RulesV1 roundTrip = RulesSnapshotCodec.Decode(RulesSnapshotCodec.Encode(rules));
            Should.NotThrow(() => roundTrip.Validate());
            Should.NotThrow(() => ChaosCupFormat.ValidateFormatAgainstRules(roundTrip));
        }
    }

    private static List<ChaosCupFormat.ChaosEntrant> BuildBalancedField()
    {
        List<ChaosCupFormat.ChaosEntrant> field = new(ChaosCupFormat.FieldSize);
        int id = 1;
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            for (int i = 0; i < ChaosCupFormat.EntrantsPerColor; i++)
            {
                field.Add(new ChaosCupFormat.ChaosEntrant(id++, color));
            }
        }

        return field;
    }

    private static List<IReadOnlyList<ChaosCupFormat.ChaosEntrant>> BuildBalancedFirstStage(
        IReadOnlyList<ChaosCupFormat.ChaosEntrant> field)
    {
        Dictionary<SportingColor, List<ChaosCupFormat.ChaosEntrant>> byColor = Enum.GetValues<SportingColor>()
            .ToDictionary(c => c, _ => new List<ChaosCupFormat.ChaosEntrant>());
        foreach (ChaosCupFormat.ChaosEntrant entrant in field)
        {
            byColor[entrant.Color].Add(entrant);
        }

        List<IReadOnlyList<ChaosCupFormat.ChaosEntrant>> groups = new(32);
        for (int g = 0; g < 32; g++)
        {
            List<ChaosCupFormat.ChaosEntrant> group = new(32);
            foreach (SportingColor color in Enum.GetValues<SportingColor>())
            {
                group.AddRange(byColor[color].Skip(g * 4).Take(4));
            }

            groups.Add(group);
        }

        return groups;
    }
}
