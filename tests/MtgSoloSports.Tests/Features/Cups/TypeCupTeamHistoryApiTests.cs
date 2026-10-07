using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.GetTypeCupTeamHistory;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class TypeCupTeamHistoryApiTests
{
    [Fact]
    public async Task History_TeamInBothEditions_MatchesStoredRowsAndAggregates()
    {
        var (store, root, saveId) = await PrepareTwoTypeCupsAsync(4141UL, 5252UL);
        try
        {
            CupTeamHistoryResponse history = await new GetTypeCupTeamHistoryHandler(store).HandleAsync(saveId, "Elf");

            history.Cup.ShouldBe("Type");
            history.TeamKey.ShouldBe("Elf");
            history.TeamName.ShouldBe("Elf");
            history.Seasons.Select(s => $"{s.SourceSeasonNumber}:{s.State}:{s.TeamCount}")
                .ShouldBe(["4:Completed:3", "2:Completed:2"]);
            history.IndividualMedals.ShouldBeEmpty();
            history.Seasons.SelectMany(s => s.Squad).ShouldAllBe(m => m.Individual == null);

            foreach (CupTeamHistoryResponse.Season season in history.Seasons)
            {
                await AssertSeasonMatchesStoredRowsAsync(store, saveId, season);
            }

            history.Seasons[1].Squad.ShouldAllBe(m => string.Equals(m.Reason, "OnlyType", StringComparison.Ordinal));
            history.Seasons[0].Squad.ShouldAllBe(m => string.Equals(m.Reason, "Capped", StringComparison.Ordinal));
            history.Honours.Editions.ShouldBe(2);
            history.Honours.TotalScoreThousandths.ShouldBe(history.Seasons.Sum(s => (long)s.TeamScoreThousandths!.Value));
            history.Honours.BestRank.ShouldBe(history.Seasons.Min(s => s.TeamRank));
            history.Roster.Count.ShouldBe(4);
            history.Roster.ShouldAllBe(r => r.Caps == 2 && r.FirstSeasonNumber == 2 && r.LastSeasonNumber == 4);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertSeasonMatchesStoredRowsAsync(SaveStore store, Guid saveId, CupTeamHistoryResponse.Season season)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<TypeCupSelectionEntity> seasonRows = await context.TypeCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonNumber == season.SourceSeasonNumber)
            .OrderBy(e => e.SelectionRank)
            .ToListAsync().ConfigureAwait(false);
        List<TypeCupSelectionEntity> stored = seasonRows
            .Where(e => string.Equals(e.CreatureType, "Elf", StringComparison.Ordinal))
            .ToList();
        season.Squad.Select(m => $"{m.AthleteId}:{m.SelectionRank}:{m.FinalRatingThousandths}")
            .ShouldBe(stored.Select(e => $"{e.SaveAthleteId}:{e.SelectionRank}:{e.FinalRatingThousandths}"));

        GetTypeCupTeamResultResponse team = await new GetTypeCupTeamResultHandler(store)
            .HandleAsync(saveId, season.SourceSeasonNumber).ConfigureAwait(false);
        GetTypeCupTeamMember result = team.Teams.Single(t => string.Equals(t.CreatureType, "Elf", StringComparison.Ordinal));
        season.TeamRank.ShouldBe(result.TeamRank);
        season.Medal.ShouldBe(result.Medal);
        season.TeamScoreThousandths.ShouldBe(result.TeamScoreThousandths);
        foreach (CupTeamHistoryResponse.SquadMember member in season.Squad)
        {
            GetTypeCupTeamLegMember leg = team.Legs.Single(l => l.AthleteId == member.AthleteId);
            member.Leg.ShouldNotBeNull();
            member.Leg.GroupRank.ShouldBe(leg.GroupRank);
            member.Leg.GroupScoreThousandths.ShouldBe(leg.GroupScoreThousandths);
            member.Leg.GroupSize.ShouldBe(season.TeamCount);
        }
    }

    [Fact]
    public async Task History_TeamWithOneAppearance_HasOneSeason()
    {
        var (store, root, saveId) = await PrepareTwoTypeCupsAsync(4242UL, 5353UL);
        try
        {
            CupTeamHistoryResponse history = await new GetTypeCupTeamHistoryHandler(store).HandleAsync(saveId, "Goblin");
            history.Seasons.Select(s => s.SourceSeasonNumber).ShouldBe([4]);
            history.Honours.Editions.ShouldBe(1);
            history.Roster.ShouldAllBe(r => r.Caps == 1);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task History_UnknownOrDifferentlyCasedType_IsNotFound()
    {
        var (store, root, saveId) = await PrepareTwoTypeCupsAsync(4343UL, 5454UL);
        try
        {
            GetTypeCupTeamHistoryHandler handler = new(store);
            await Should.ThrowAsync<CupTeamHistoryNotFoundException>(() => handler.HandleAsync(saveId, "Orc"));
            await Should.ThrowAsync<CupTeamHistoryNotFoundException>(() => handler.HandleAsync(saveId, "elf"));
            await Should.ThrowAsync<CupTeamHistoryNotFoundException>(() => handler.HandleAsync(saveId, " "));
            await Should.ThrowAsync<ArgumentException>(() => handler.HandleAsync(Guid.Empty, "Elf"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task History_WithoutStoredSelectionReports_StillLoadsWithoutReasons()
    {
        var (store, root, saveId) = await PrepareTwoTypeCupsAsync(4444UL, 5555UL);
        try
        {
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                context.CupSelectionReports.RemoveRange(await context.CupSelectionReports.ToListAsync());
                await context.SaveChangesAsync();
            }

            CupTeamHistoryResponse history = await new GetTypeCupTeamHistoryHandler(store).HandleAsync(saveId, "Dwarf");
            history.Seasons.Count.ShouldBe(2);
            history.Seasons.SelectMany(s => s.Squad).Count().ShouldBe(8);
            history.Seasons.SelectMany(s => s.Squad).ShouldAllBe(m => m.Reason == null && m.FinalRatingThousandths >= 0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task History_SelectedButNotPlayed_HasNoResult()
    {
        var (store, root, saveId) = await PrepareTypeCupAsync(4545UL, 5656UL);
        try
        {
            CupTeamHistoryResponse history = await new GetTypeCupTeamHistoryHandler(store).HandleAsync(saveId, "Elf");
            history.Seasons.Single().State.ShouldBe("Selected");
            history.Seasons.Single().TeamRank.ShouldBeNull();
            history.Seasons.Single().Squad.ShouldAllBe(m => m.Leg == null);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task History_FinalistTeam_PreservesQualificationAndFinalWithoutThrow()
    {
        var (store, root) = CreateStore();
        try
        {
            (Guid saveId, List<int> athleteIds) = await SeedFinalistTeamAsync(store);
            CupTeamHistoryResponse history = await new GetTypeCupTeamHistoryHandler(store).HandleAsync(saveId, "Goblin");
            AssertFinalistHistory(history, athleteIds);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task History_EliminatedTeam_KeepsQualificationWithCutoff()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SeedEliminatedTeamAsync(store);
            CupTeamHistoryResponse history = await new GetTypeCupTeamHistoryHandler(store).HandleAsync(saveId, "Elf");
            AssertEliminatedHistory(history);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task<(Guid SaveId, List<int> AthleteIds)> SeedFinalistTeamAsync(SaveStore store)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            "Type Tournament Team Hist", 6161UL, 6262UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        int seasonId = await SeasonIdAsync(store, saveId).ConfigureAwait(false);
        List<int> athleteIds = await FirstAthletesAsync(store, saveId, 4).ConfigureAwait(false);
        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            AddSelections(context, seasonId, "Goblin", athleteIds);
            AddStanding(context, seasonId, phase: 1, qualGroup: 1, "Goblin", teamRank: 5, medal: 0);
            AddStanding(context, seasonId, phase: 2, qualGroup: 0, "Goblin", teamRank: 2, medal: 2);
            AddFinalistLegs(context, seasonId, "Goblin", athleteIds);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        return (saveId, athleteIds);
    }

    private static void AssertFinalistHistory(CupTeamHistoryResponse history, List<int> athleteIds)
    {
        history.Seasons.Count.ShouldBe(2);
        history.Seasons.ShouldAllBe(s => s.SourceSeasonNumber == 2);
        CupTeamHistoryResponse.Season final = history.Seasons[0];
        final.TournamentPhase.ShouldBe(2);
        final.TournamentStage.ShouldBe("Final");
        final.TeamRank.ShouldBe(2);
        final.Medal.ShouldBe("Silver");
        final.QualifiedForFinal.ShouldBeTrue();
        final.EliminatedInQualification.ShouldBeFalse();
        CupTeamHistoryResponse.Season qual = history.Seasons[1];
        qual.TournamentPhase.ShouldBe(1);
        qual.QualificationGroup.ShouldBe(1);
        qual.TournamentStage.ShouldBe("Qualification Group A");
        qual.TeamRank.ShouldBe(5);
        qual.QualifiedForFinal.ShouldBeTrue();
        qual.EliminatedInQualification.ShouldBeFalse();
        final.Squad.ShouldAllBe(m => m.Leg != null);
        qual.Squad.ShouldAllBe(m => m.Leg != null);
        final.Squad.Select(m => m.Leg!.GroupScoreThousandths).ShouldAllBe(v => v == 150_000);
        qual.Squad.Select(m => m.Leg!.GroupScoreThousandths).ShouldAllBe(v => v == 60_000);
        history.Honours.Editions.ShouldBe(1);
        history.Honours.Silver.ShouldBe(1);
        history.Honours.Gold.ShouldBe(0);
        athleteIds.Count.ShouldBe(4);
    }

    private static async Task<Guid> SeedEliminatedTeamAsync(SaveStore store)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            "Type Tournament Eliminated Hist", 6363UL, 6464UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        int seasonId = await SeasonIdAsync(store, saveId).ConfigureAwait(false);
        List<int> athleteIds = await FirstAthletesAsync(store, saveId, 4).ConfigureAwait(false);
        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            AddSelections(context, seasonId, "Elf", athleteIds);
            AddStanding(context, seasonId, phase: 1, qualGroup: 2, "Elf", teamRank: 30, medal: 0);
            // Another team reaches the Final, so the tournament is decided
            // and the Elf qualification becomes an elimination.
            AddStanding(context, seasonId, phase: 2, qualGroup: 0, "Goblin", teamRank: 1, medal: 1);
            for (int i = 0; i < athleteIds.Count; i++)
            {
                context.TypeCupTeamGroupStandings.Add(new TypeCupTeamGroupStandingEntity
                {
                    SourceSeasonId = seasonId,
                    SourceSeasonNumber = 2,
                    TournamentPhase = 1,
                    QualificationGroup = 2,
                    GroupNumber = i + 1,
                    SaveAthleteId = athleteIds[i],
                    GroupRank = 5,
                    GroupScoreThousandths = 40_000,
                    BaseScoreThousandths = 35_000,
                    RoundWins = 0,
                    RoundPlaceCountsJson = "[]",
                    CreatureType = "Elf",
                    SelectionRank = i + 1,
                });
            }

            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        return saveId;
    }

    private static void AssertEliminatedHistory(CupTeamHistoryResponse history)
    {
        CupTeamHistoryResponse.Season qual = history.Seasons.Single();
        qual.TournamentStage.ShouldBe("Qualification Group B");
        qual.TeamRank.ShouldBe(30);
        qual.EliminatedInQualification.ShouldBeTrue();
        qual.QualifiedForFinal.ShouldBeFalse();
        history.Honours.Gold.ShouldBe(0);
        history.Honours.BestRank.ShouldBeNull();
    }

    private static async Task<int> SeasonIdAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Seasons.AsNoTracking()
            .Where(e => e.SeasonNumber == 1).Select(e => e.Id).SingleAsync().ConfigureAwait(false);
    }

    private static async Task<List<int>> FirstAthletesAsync(SaveStore store, Guid saveId, int count)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<int> ids = await context.SaveAthletes.AsNoTracking()
            .OrderBy(e => e.Id).Take(count).Select(e => e.Id).ToListAsync().ConfigureAwait(false);
        ids.Count.ShouldBe(count);
        return ids;
    }

    private static void AddSelections(SaveDbContext context, int seasonId, string creatureType, List<int> athleteIds)
    {
        for (int i = 0; i < athleteIds.Count; i++)
        {
            context.TypeCupSelections.Add(new TypeCupSelectionEntity
            {
                SourceSeasonId = seasonId,
                SourceSeasonNumber = 2,
                CreatureType = creatureType,
                SaveAthleteId = athleteIds[i],
                SelectionRank = i + 1,
                TypeRank = i + 1,
                FinalRatingThousandths = 900_000 - (i * 10_000),
                BonusNormThousandths = 1000,
                PerformanceNormThousandths = 800,
                FormNormThousandths = 600,
                PrestigeNormThousandths = 400,
                BonusRawThousandths = 1000,
                PerformanceRawThousandths = 800,
                FormRaw = 600,
                PrestigeRaw = 400,
                RulesVersion = 1,
            });
        }
    }

    private static void AddStanding(SaveDbContext context, int seasonId, int phase, int qualGroup, string creatureType, int teamRank, int medal)
    {
        int score = phase == 2 ? 500_000 + (teamRank * 1_000) : 210_000;
        context.TypeCupTeamStandings.Add(new TypeCupTeamStandingEntity
        {
            SourceSeasonId = seasonId,
            SourceSeasonNumber = 2,
            TournamentPhase = phase,
            QualificationGroup = qualGroup,
            CreatureType = creatureType,
            TeamRank = teamRank,
            TeamScoreThousandths = score,
            TeamBaseThousandths = score - 20_000,
            GroupWins = 1,
            RoundWins = 3,
            GroupPlaceCountsJson = "[]",
            RoundPlaceCountsJson = "[]",
            Medal = medal,
        });
    }

    private static void AddFinalistLegs(SaveDbContext context, int seasonId, string creatureType, List<int> athleteIds)
    {
        // Qualification Group A table plus the Final: the same team holds
        // one leg per stage, so the old single-stage SingleOrDefault throws.
        for (int i = 0; i < athleteIds.Count; i++)
        {
            context.TypeCupTeamGroupStandings.Add(new TypeCupTeamGroupStandingEntity
            {
                SourceSeasonId = seasonId,
                SourceSeasonNumber = 2,
                TournamentPhase = 1,
                QualificationGroup = 1,
                GroupNumber = i + 1,
                SaveAthleteId = athleteIds[i],
                GroupRank = 3 + i,
                GroupScoreThousandths = 60_000,
                BaseScoreThousandths = 55_000,
                RoundWins = 1,
                RoundPlaceCountsJson = "[]",
                CreatureType = creatureType,
                SelectionRank = i + 1,
            });
            context.TypeCupTeamGroupStandings.Add(new TypeCupTeamGroupStandingEntity
            {
                SourceSeasonId = seasonId,
                SourceSeasonNumber = 2,
                TournamentPhase = 2,
                QualificationGroup = 0,
                GroupNumber = i + 1,
                SaveAthleteId = athleteIds[i],
                GroupRank = 1 + (i % 2),
                GroupScoreThousandths = 150_000,
                BaseScoreThousandths = 140_000,
                RoundWins = 2,
                RoundPlaceCountsJson = "[]",
                CreatureType = creatureType,
                SelectionRank = i + 1,
            });
        }
    }
}
