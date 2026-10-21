using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Records.GetHallOfFame;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Records;

public sealed class CareerPerformanceIntegrationTests
{
    [Fact]
    public async Task CountsEveryFinalizedEventOnce_PeriodFiltersRecompute()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-career-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Career Perf", 4242UL, 777UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> ids = await TakeAthletesAsync(store, saveId, 12);
            await SeedTwoSeasonsAsync(store, saveId, ids);
            await AssertPeriodBehaviorAsync(store, saveId, ids);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RejectsInvalidRange()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-career-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Career Perf Range", 4242UL, 777UL, UniverseTestCatalog.Build());
            GetHallOfFameHandler handler = new(store);
            await Should.ThrowAsync<ArgumentException>(() => handler.HandleAsync(created.Detail.SaveId, 20, 8, 7));
            await Should.ThrowAsync<ArgumentException>(() => handler.HandleAsync(created.Detail.SaveId, 20, 0, 7));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task StagedQualificationWithoutFinal_AwardsNothing()
    {
        // Regression for MSS-083 correction: phase-1 qualification rows
        // persisted before the Final must not create provisional credit,
        // not even reduced qualifier points for group winners.
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-career-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Career Provisional", 4242UL, 777UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> ids = await TakeAthletesAsync(store, saveId, 6);
            await SeedQualificationOnlyAsync(store, saveId, ids);

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                List<CareerPerformanceScorer.PlacementEvent> typeEvents =
                    await CareerPerformanceLoader.LoadTypeTeamEventsAsync(
                        context, null, null, CancellationToken.None);
                typeEvents.ShouldBeEmpty();
            }

            GetHallOfFameHandler handler = new(store);
            GetHallOfFameResponse response = await handler.HandleAsync(saveId, 100, 8, 8);
            response.Leaders.ShouldBeEmpty();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task SeedQualificationOnlyAsync(SaveStore store, Guid saveId, List<int> ids)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season8 = new() { SeasonNumber = 8, HasSuperleague = true, IsComplete = true };
        context.Seasons.Add(season8);
        await context.SaveChangesAsync().ConfigureAwait(false);

        string[] teams = ["Alpha", "Beta", "Gamma", "Delta"];
        for (int i = 0; i < 4; i++)
        {
            context.TypeCupTeamStandings.Add(new TypeCupTeamStandingEntity
            {
                SourceSeasonId = season8.Id,
                SourceSeasonNumber = 8,
                TournamentPhase = 1,
                QualificationGroup = 1,
                CreatureType = teams[i],
                TeamRank = i + 1,
                TeamScoreThousandths = 0,
                TeamBaseThousandths = 0,
                GroupWins = 0,
                RoundWins = 0,
                GroupPlaceCountsJson = "[]",
                RoundPlaceCountsJson = "[]",
                Medal = 0,
            });
            context.TypeCupTeamGroupStandings.Add(new TypeCupTeamGroupStandingEntity
            {
                SourceSeasonId = season8.Id,
                SourceSeasonNumber = 8,
                TournamentPhase = 1,
                QualificationGroup = 1,
                GroupNumber = 1,
                SaveAthleteId = ids[i],
                GroupRank = i + 1,
                GroupScoreThousandths = 0,
                BaseScoreThousandths = 0,
                RoundWins = 0,
                RoundPlaceCountsJson = "[]",
                CreatureType = teams[i],
                SelectionRank = 1,
            });
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task AssertPeriodBehaviorAsync(SaveStore store, Guid saveId, List<int> ids)
    {
        GetHallOfFameHandler handler = new(store);

        GetHallOfFameResponse all = await handler.HandleAsync(saveId).ConfigureAwait(false);
        all.FromSeason.ShouldBeNull();
        all.ToSeason.ShouldBeNull();

        GetHallOfFameResponse season7 = await handler.HandleAsync(saveId, 100, 7, 7).ConfigureAwait(false);
        season7.FromSeason.ShouldBe(7);
        GetHallOfFameResponse season8 = await handler.HandleAsync(saveId, 100, 8, 8).ConfigureAwait(false);
        GetHallOfFameResponse range = await handler.HandleAsync(saveId, 100, 7, 8).ConfigureAwait(false);
        GetHallOfFameResponse empty = await handler.HandleAsync(saveId, 100, 50, 60).ConfigureAwait(false);
        empty.Leaders.ShouldBeEmpty();

        AssertCupCredits(season7, season8, ids);
        AssertRangeSums(range, season7, season8, ids);
        await AssertRetiredStillRankedAsync(store, saveId, ids).ConfigureAwait(false);
        await AssertIncompleteIgnoredAsync(handler, saveId, all, ids).ConfigureAwait(false);
        AssertDescending(range);
    }

    private static void AssertCupCredits(GetHallOfFameResponse season7, GetHallOfFameResponse season8, List<int> ids)
    {
        HallOfFameEntry finalist = season8.Leaders.Single(e => e.AthleteId == ids[0]);
        HallOfFameEntry eliminated = season8.Leaders.Single(e => e.AthleteId == ids[2]);
        finalist.TypeCupTeamPoints.ShouldBeGreaterThan(eliminated.TypeCupTeamPoints);

        HallOfFameEntry superChamp = season7.Leaders.Single(e => e.AthleteId == ids[0]);
        HallOfFameEntry feederChamp = season7.Leaders.Single(e => e.AthleteId == ids[4]);
        superChamp.SuperleagueTitles.ShouldBe(1);
        feederChamp.TotalTitles.ShouldBe(1);
        superChamp.CareerPoints.ShouldBeGreaterThan(feederChamp.CareerPoints);
    }

    private static void AssertRangeSums(
        GetHallOfFameResponse range,
        GetHallOfFameResponse season7,
        GetHallOfFameResponse season8,
        List<int> ids)
    {
        // Every event is counted exactly once: the range total equals the sum
        // of the per-season totals, for points and for event counts.
        HallOfFameEntry rangeSuper = range.Leaders.Single(e => e.AthleteId == ids[0]);
        rangeSuper.CareerPoints.ShouldBe(
            season7.Leaders.Single(e => e.AthleteId == ids[0]).CareerPoints +
            season8.Leaders.Single(e => e.AthleteId == ids[0]).CareerPoints);
        rangeSuper.ScoredEvents.ShouldBe(
            season7.Leaders.Single(e => e.AthleteId == ids[0]).ScoredEvents +
            season8.Leaders.Single(e => e.AthleteId == ids[0]).ScoredEvents);
        HallOfFameEntry rangeElim = range.Leaders.Single(e => e.AthleteId == ids[2]);
        rangeElim.ScoredEvents.ShouldBe(
            season7.Leaders.Single(e => e.AthleteId == ids[2]).ScoredEvents +
            season8.Leaders.Single(e => e.AthleteId == ids[2]).ScoredEvents);
    }

    private static async Task AssertRetiredStillRankedAsync(SaveStore store, Guid saveId, List<int> ids)
    {
        await MarkInactiveAsync(store, saveId, ids[4]).ConfigureAwait(false);
        GetHallOfFameHandler handler = new(store);
        GetHallOfFameResponse afterRetire = await handler.HandleAsync(saveId, 100, 7, 7).ConfigureAwait(false);
        afterRetire.Leaders.Any(e => e.AthleteId == ids[4]).ShouldBeTrue();
    }

    private static async Task AssertIncompleteIgnoredAsync(
        GetHallOfFameHandler handler, Guid saveId, GetHallOfFameResponse all, List<int> ids)
    {
        GetHallOfFameResponse withIncomplete = await handler.HandleAsync(saveId).ConfigureAwait(false);
        withIncomplete.Leaders.Single(e => e.AthleteId == ids[0]).CareerPoints
            .ShouldBe(all.Leaders.Single(e => e.AthleteId == ids[0]).CareerPoints);
    }

    private static void AssertDescending(GetHallOfFameResponse range)
    {
        for (int i = 1; i < range.Leaders.Count; i++)
        {
            range.Leaders[i - 1].CareerPoints.ShouldBeGreaterThanOrEqualTo(range.Leaders[i].CareerPoints);
        }
    }

    private static async Task<List<int>> TakeAthletesAsync(SaveStore store, Guid saveId, int count)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Take(count)
            .Select(e => e.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task MarkInactiveAsync(SaveStore store, Guid saveId, int athleteId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        AthleteCareerEntity? career = await context.AthleteCareers
            .SingleOrDefaultAsync(e => e.SaveAthleteId == athleteId).ConfigureAwait(false);
        if (career is not null)
        {
            career.IsActive = false;
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    private static async Task SeedTwoSeasonsAsync(SaveStore store, Guid saveId, List<int> ids)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season7 = new() { SeasonNumber = 7, HasSuperleague = true, IsComplete = true };
        SeasonEntity season8 = new() { SeasonNumber = 8, HasSuperleague = true, IsComplete = true };
        SeasonEntity season9 = new() { SeasonNumber = 9, HasSuperleague = true, IsComplete = false };
        context.Seasons.AddRange(season7, season8, season9);
        await context.SaveChangesAsync().ConfigureAwait(false);

        LeagueEntity sl7 = League(season7.Id, (int)LeagueKind.Superleague, 0, "Superleague S7");
        LeagueEntity f1_7 = League(season7.Id, (int)LeagueKind.Feeder, 1, "Feeder 1 S7");
        LeagueEntity sl8 = League(season8.Id, (int)LeagueKind.Superleague, 0, "Superleague S8");
        LeagueEntity f1_8 = League(season8.Id, (int)LeagueKind.Feeder, 1, "Feeder 1 S8");
        LeagueEntity sl9 = League(season9.Id, (int)LeagueKind.Superleague, 0, "Superleague S9");
        context.Leagues.AddRange(sl7, f1_7, sl8, f1_8, sl9);
        await context.SaveChangesAsync().ConfigureAwait(false);

        SeedLeagueStandings(context, season7.Id, sl7.Id, f1_7.Id, season8.Id, sl8.Id, f1_8.Id, season9.Id, sl9.Id, ids);
        SeedColorCups(context, season7, ids);
        SeedTypeCup(context, season8, ids);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static LeagueEntity League(int seasonId, int kind, int division, string name) => new()
    {
        SeasonId = seasonId,
        SportingColor = 0,
        Kind = kind,
        FeederDivision = division,
        Name = name,
    };

    private static void SeedLeagueStandings(
        SaveDbContext context,
        int season7Id, int sl7Id, int f1_7Id,
        int season8Id, int sl8Id, int f1_8Id,
        int season9Id, int sl9Id,
        List<int> ids)
    {
        for (int i = 0; i < 4; i++)
        {
            context.SeasonStandings.Add(Standing(season7Id, sl7Id, ids[i], i + 1));
            context.SeasonStandings.Add(Standing(season7Id, f1_7Id, ids[4 + i], i + 1));
            context.SeasonStandings.Add(Standing(season8Id, sl8Id, ids[4 + i], i + 1));
            context.SeasonStandings.Add(Standing(season8Id, f1_8Id, ids[i], i + 1));
            context.SeasonStandings.Add(Standing(season9Id, sl9Id, ids[8 + i], i + 1));
        }
    }

    private static void SeedColorCups(SaveDbContext context, SeasonEntity season7, List<int> ids)
    {
        for (int i = 0; i < 8; i++)
        {
            context.ColorCupIndividualStandings.Add(new ColorCupIndividualStandingEntity
            {
                SourceSeasonId = season7.Id,
                SourceSeasonNumber = 7,
                SaveAthleteId = ids[i],
                CupRank = i + 1,
                CupScoreThousandths = 0,
                BaseScoreThousandths = 0,
                RoundWins = 0,
                RoundPlaceCountsJson = "[]",
                Medal = 0,
                SportingColor = 0,
                SelectionRank = 1,
            });
        }

        context.ColorCupTeamStandings.Add(TeamStanding(season7.Id, 0, 1, 1));
        context.ColorCupTeamStandings.Add(TeamStanding(season7.Id, 1, 2, 2));
        context.ColorCupTeamGroupStandings.Add(TeamLeg(season7.Id, ids[0], 0, 1));
        context.ColorCupTeamGroupStandings.Add(TeamLeg(season7.Id, ids[4], 1, 2));
    }

    private static void SeedTypeCup(SaveDbContext context, SeasonEntity season8, List<int> ids)
    {
        SeedTypeQualifiers(context, season8, ids);
        SeedTypeFinal(context, season8, ids);
    }

    private static void SeedTypeQualifiers(SaveDbContext context, SeasonEntity season8, List<int> ids)
    {
        string[] teams = ["Alpha", "Beta", "Gamma", "Delta", "Eps", "Zeta"];
        int[] qualRanks = [1, 2, 3, 1, 2, 3];
        int[] qualGroups = [1, 1, 1, 2, 2, 2];
        for (int i = 0; i < 6; i++)
        {
            context.TypeCupTeamStandings.Add(new TypeCupTeamStandingEntity
            {
                SourceSeasonId = season8.Id,
                SourceSeasonNumber = 8,
                TournamentPhase = 1,
                QualificationGroup = qualGroups[i],
                CreatureType = teams[i],
                TeamRank = qualRanks[i],
                TeamScoreThousandths = 0,
                TeamBaseThousandths = 0,
                GroupWins = 0,
                RoundWins = 0,
                GroupPlaceCountsJson = "[]",
                RoundPlaceCountsJson = "[]",
                Medal = 0,
            });
            context.TypeCupTeamGroupStandings.Add(new TypeCupTeamGroupStandingEntity
            {
                SourceSeasonId = season8.Id,
                SourceSeasonNumber = 8,
                TournamentPhase = 1,
                QualificationGroup = qualGroups[i],
                GroupNumber = 1,
                SaveAthleteId = ids[i],
                GroupRank = qualRanks[i],
                GroupScoreThousandths = 0,
                BaseScoreThousandths = 0,
                RoundWins = 0,
                RoundPlaceCountsJson = "[]",
                CreatureType = teams[i],
                SelectionRank = 1,
            });
        }
    }

    private static void SeedTypeFinal(SaveDbContext context, SeasonEntity season8, List<int> ids)
    {
        string[] finalTeams = ["Alpha", "Delta", "Beta", "Eps"];
        int[] finalRanks = [1, 2, 3, 4];
        int[] finalAthletes = [ids[0], ids[3], ids[1], ids[4]];
        for (int i = 0; i < 4; i++)
        {
            int rank = finalRanks[i];
            context.TypeCupTeamStandings.Add(TypeFinalStanding(season8.Id, finalTeams[i], rank));
            context.TypeCupTeamGroupStandings.Add(TypeFinalLeg(season8.Id, finalAthletes[i], finalTeams[i], rank));
        }
    }

    private static TypeCupTeamStandingEntity TypeFinalStanding(int seasonId, string team, int rank) => new()
    {
        SourceSeasonId = seasonId,
        SourceSeasonNumber = 8,
        TournamentPhase = 2,
        QualificationGroup = 0,
        CreatureType = team,
        TeamRank = rank,
        TeamScoreThousandths = 0,
        TeamBaseThousandths = 0,
        GroupWins = 0,
        RoundWins = 0,
        GroupPlaceCountsJson = "[]",
        RoundPlaceCountsJson = "[]",
        Medal = rank <= 3 ? rank : 0,
    };

    private static TypeCupTeamGroupStandingEntity TypeFinalLeg(int seasonId, int athleteId, string team, int rank) => new()
    {
        SourceSeasonId = seasonId,
        SourceSeasonNumber = 8,
        TournamentPhase = 2,
        QualificationGroup = 0,
        GroupNumber = 1,
        SaveAthleteId = athleteId,
        GroupRank = rank,
        GroupScoreThousandths = 0,
        BaseScoreThousandths = 0,
        RoundWins = 0,
        RoundPlaceCountsJson = "[]",
        CreatureType = team,
        SelectionRank = 1,
    };

    private static ColorCupTeamStandingEntity TeamStanding(int seasonId, int color, int rank, int medal) => new()
    {
        SourceSeasonId = seasonId,
        SourceSeasonNumber = 7,
        SportingColor = color,
        TeamRank = rank,
        TeamScoreThousandths = 0,
        TeamBaseThousandths = 0,
        GroupWins = 0,
        RoundWins = 0,
        GroupPlaceCountsJson = "[]",
        RoundPlaceCountsJson = "[]",
        Medal = medal,
    };

    private static ColorCupTeamGroupStandingEntity TeamLeg(int seasonId, int athleteId, int color, int rank) => new()
    {
        SourceSeasonId = seasonId,
        SourceSeasonNumber = 7,
        GroupNumber = 1,
        SaveAthleteId = athleteId,
        GroupRank = rank,
        GroupScoreThousandths = 0,
        BaseScoreThousandths = 0,
        RoundWins = 0,
        RoundPlaceCountsJson = "[]",
        SportingColor = color,
        SelectionRank = 1,
    };

    private static SeasonStandingEntity Standing(int seasonId, int leagueId, int athleteId, int rank) => new()
    {
        SeasonId = seasonId,
        LeagueId = leagueId,
        SaveAthleteId = athleteId,
        SeasonRank = rank,
        TotalChampionshipPointsThousandths = 0,
        TotalStageScoreThousandths = 0,
        TotalBaseScoreThousandths = 0,
        StageWins = 0,
        RoundWins = 0,
        StagePlaceCountsJson = "[]",
        RoundPlaceCountsJson = "[]",
        IsChampion = rank == 1,
    };
}
