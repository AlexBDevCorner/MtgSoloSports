using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.GetTypeCupTournament;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Records.GetHallOfFame;
using MtgSoloSports.Features.Reports.SeasonDossier;
using MtgSoloSports.Features.Reports.UniverseAlmanac;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Reports;

/// <summary>
/// MSS-088 correction: regression coverage for three P1 defects —
/// partial Color Cup medal counting, individual podium athlete identities,
/// and streak leaders outside the weighted top 20.
/// Plus second-round correction: Type Cup qualification-group evidence and
/// postseason movement/qualifier identities with Season 1 not-applicable.
/// </summary>
public sealed class SeasonDossierAlmanacCorrectionTests
{
    [Fact]
    public async Task Almanac_IndividualOnly_DoesNotCountTowardMedalTables()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(9293UL, 3031UL);
        try
        {
            await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                AlmanacCupTables pending = await UniverseAlmanacCupQueries.LoadCupTablesAsync(
                    context, new Dictionary<int, string>(), CancellationToken.None);
                pending.Editions.Single().State.ShouldBe("InProgress");
                pending.ColorIndividualMedals.Sum(r => r.Gold + r.Silver + r.Bronze).ShouldBe(0);
                pending.ColorCombinedMedals.Sum(r => r.Gold + r.Silver + r.Bronze).ShouldBe(
                    pending.ColorTeamTable.Sum(t => t.Gold + t.Silver + t.Bronze));
            }

            await new RunColorCupTeamHandler(store).HandleAsync(saveId);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                AlmanacCupTables done = await UniverseAlmanacCupQueries.LoadCupTablesAsync(
                    context, new Dictionary<int, string>(), CancellationToken.None);
                done.Editions.Single().State.ShouldBe("Completed");
                done.ColorIndividualMedals.Sum(r => r.Gold + r.Silver + r.Bronze).ShouldBe(3);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Dossier_MapIndividual_PreservesSilverAndBronzeIdentities()
    {
        List<GetColorCupIndividualMember> standings =
        [
            new(11, "Gold Card", "White", 1, 1, 9000, 8000, 3, "Gold", null),
            new(22, "Silver Card", "Blue", 2, 2, 8000, 7000, 2, "Silver", null),
            new(33, "Bronze Card", "Red", 3, 3, 7000, 6000, 1, "Bronze", null),
        ];
        var response = new GetColorCupIndividualResultResponse(
            Guid.NewGuid(), 1, 1, 3, 1, "checksum",
            0UL, 0UL, 0UL, 0UL,
            11, "Gold Card", standings);

        IndividualCupSummary? summary = SeasonDossierCupQueries.MapIndividual(response);

        summary.ShouldNotBeNull();
        summary.TopThree.Count.ShouldBe(3);
        summary.TopThree[0].AthleteId.ShouldBe(11);
        summary.TopThree[0].AthleteName.ShouldBe("Gold Card");
        summary.TopThree[1].AthleteId.ShouldBe(22);
        summary.TopThree[1].AthleteName.ShouldBe("Silver Card");
        summary.TopThree[2].AthleteId.ShouldBe(33);
        summary.TopThree[2].AthleteName.ShouldBe("Bronze Card");
        summary.ChampionAthleteId.ShouldBe(11);
    }

    [Fact]
    public void Streaks_FindsHolderOutsideWeightedTop20()
    {
        List<HallOfFameEntry> full = [];
        for (int i = 1; i <= 20; i++)
        {
            full.Add(new HallOfFameEntry(
                i, i, $"Top {i}", 0, "White",
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                1, 1,
                10000 - i, 10000 - i, 0, 0, 0, 0, 0, 0, 0, 1));
        }

        full.Add(new HallOfFameEntry(
            21, 999, "Streak Holder", 1, "Blue",
            0, 5, 5, 10, 10, 5, 5, 5, 0, 0, 0,
            5, 6,
            1, 1, 0, 0, 0, 0, 0, 0, 0, 1));

        IReadOnlyList<HallOfFameEntry> truncated = full.Take(20).ToList();
        (IReadOnlyList<StreakEntry> truncatedTitles, IReadOnlyList<StreakEntry> truncatedStages) =
            UniverseAlmanacLeaderQueries.BuildStreaks(truncated);
        truncatedTitles.ShouldBeEmpty();
        truncatedStages.ShouldBeEmpty();

        (IReadOnlyList<StreakEntry> titles, IReadOnlyList<StreakEntry> stages) =
            UniverseAlmanacLeaderQueries.BuildStreaks(full);
        titles.Single().AthleteId.ShouldBe(999);
        titles.Single().Streak.ShouldBe(5);
        stages.Single().AthleteId.ShouldBe(999);
        stages.Single().Streak.ShouldBe(6);
    }

    [Fact]
    public void Dossier_MapTournament_PreservesQualificationGroupCutLines()
    {
        GetTypeCupTournamentResponse tournament = BuildPartialTournament();
        TypeCupSummary summary = SeasonDossierCupQueries.MapTournament(tournament, []);

        summary.FinishedQualificationGroups.ShouldBe(2);
        summary.QualificationGroupCount.ShouldBe(3);
        summary.QualificationGroups.Count.ShouldBe(2);
        summary.QualificationPolicyVersion.ShouldBe(2);
        TypeCupQualificationGroupEntry first = summary.QualificationGroups.OrderBy(g => g.QualificationGroup).First();
        first.QualificationGroup.ShouldBe(1);
        first.GroupSize.ShouldBe(3);
        first.Teams.Count.ShouldBe(3);
        first.Teams.OrderBy(t => t.TeamRank).Select(t => t.CreatureType).ShouldBe(["Elf", "Dwarf", "Goblin"], StringComparer.Ordinal);
        first.Teams.Single(t => t.TeamRank == 1).TeamScoreThousandths.ShouldBe(90000);
        first.Teams.Single(t => t.TeamRank == 1).QualificationStatus.ShouldBe("Guaranteed");
        first.Teams.Single(t => t.TeamRank == 2).QualificationStatus.ShouldBe("WildcardCandidate");
        first.WildcardCandidate.ShouldBe("Dwarf");
        first.QualifiedTeams.Contains("Elf", StringComparer.Ordinal).ShouldBeTrue();
        first.EliminatedTeams.Contains("Goblin", StringComparer.Ordinal).ShouldBeTrue();
        summary.Wildcards.Count.ShouldBe(1);
        summary.Wildcards.Single().CreatureType.ShouldBe("Kavu");
        summary.GuaranteedPlacesPerGroup.ShouldBe([1, 1, 1]);
    }

    [Fact]
    public async Task Dossier_LoadPostseason_Inaugural_HasIdentitiesAndNotApplicableQualifier()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-dossier-post-");
        try
        {
            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            PostseasonSection postseason = await SeasonDossierQueries.LoadPostseasonAsync(context, season, CancellationToken.None);
            postseason.Movements.Count.ShouldBeGreaterThanOrEqualTo(32);
            postseason.Movements.ShouldAllBe(m => m.AthleteId > 0 && !string.IsNullOrWhiteSpace(m.AthleteName));
            postseason.Movements.Count(m => string.Equals(m.Kind, "InauguralPromotion", StringComparison.Ordinal)).ShouldBe(32);
            postseason.Movements.Select(m => m.AthleteId).Distinct().Count().ShouldBe(postseason.Movements.Count);
            postseason.QualifierStatus.ShouldBe("not-applicable");
            postseason.RebalanceStatus.ShouldStartWith("resolved");
            postseason.Qualifiers.ShouldBeEmpty();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Dossier_LoadPostseason_QualifierResolved_HasPromotionAndQualifiedFlags()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierResolvedAsync("mtgsolosports-dossier-qual-");
        try
        {
            await new RunQualifierHandler(store).HandleAsync(saveId);
            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            PostseasonSection postseason = await SeasonDossierQueries.LoadPostseasonAsync(context, season, CancellationToken.None);
            postseason.MovementStatus.ShouldStartWith("resolved");
            postseason.QualifierStatus.ShouldStartWith("played");
            postseason.Movements.Count.ShouldBeGreaterThan(0);
            postseason.Movements.ShouldAllBe(m => m.AthleteId > 0 && !string.IsNullOrWhiteSpace(m.AthleteName));
            postseason.Movements.ShouldContain(m => string.Equals(m.Kind, "AutomaticPromotion", StringComparison.Ordinal));
            postseason.Movements.ShouldContain(m => string.Equals(m.Kind, "AutomaticRelegation", StringComparison.Ordinal));
            postseason.Qualifiers.Count.ShouldBe(32);
            postseason.Qualifiers.Count(q => q.IsQualified).ShouldBe(8);
            postseason.Qualifiers.ShouldAllBe(q => q.AthleteId > 0 && !string.IsNullOrWhiteSpace(q.AthleteName));
            postseason.Qualifiers.ShouldAllBe(q => q.QualifierRank >= 1 && q.QualifierRank <= 32);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static GetTypeCupTournamentResponse BuildPartialTournament()
    {
        List<TypeCupTournamentQualificationGroup> groups =
        [
            new(
                1,
                3,
                1,
                "checksum-1",
                [
                    new("Elf", 1, 90000, 85000, "None", true, "Guaranteed"),
                    new("Dwarf", 2, 80000, 79000, "None", false, "WildcardCandidate"),
                    new("Goblin", 3, 70000, 69000, "None", false, "Eliminated"),
                ],
                [],
                ["Elf"],
                ["Dwarf", "Goblin"],
                1,
                "Dwarf",
                null),
            new(
                2,
                3,
                1,
                "checksum-2",
                [
                    new("Kavu", 1, 91000, 86000, "None", true, "Guaranteed"),
                    new("Merfolk", 2, 81000, 80000, "None", true, "Wildcard"),
                    new("Sliver", 3, 71000, 70000, "None", false, "Eliminated"),
                ],
                [],
                ["Kavu", "Merfolk"],
                ["Sliver"],
                1,
                "Merfolk",
                "Merfolk"),
        ];
        List<TypeCupTournamentWildcard> wildcards =
        [
            new("Kavu", 2, 1, 91000, 86000, 3, 273000, 255000, false),
        ];
        return new GetTypeCupTournamentResponse(
            Guid.NewGuid(),
            2,
            7,
            false,
            9,
            3,
            [3, 3, 3],
            [1, 1, 1],
            "draw-checksum",
            groups,
            [],
            null,
            null,
            string.Empty,
            2,
            1,
            [1, 1, 1],
            wildcards);
    }
}
