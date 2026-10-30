using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupSelection;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-097: Color Cup squad eligibility is source-season active-only, matching the Type Cup.
/// Pool athletes cannot qualify even with high bonus or career prestige.
/// </summary>
public sealed class ColorCupActiveEligibilityTests
{
    [Fact]
    public async Task PoolHighBonusAndPrestige_NeverSelected()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-colorcup-active-");
        try
        {
            Guid saveId = await SetupPoolFixtureAsync(store);
            SelectColorCupTeamsResponse response = await SelectAsync(store, saveId);
            await AssertPoolExcludedAsync(store, saveId, response);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task F3Contender_EligibleAndBeatsLowF1()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-colorcup-f3-");
        try
        {
            Guid saveId = await SetupF3FixtureAsync(store);
            SelectColorCupTeamsResponse response = await SelectAsync(store, saveId);
            AssertF3BeatsF1(response);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DuplicateActiveMemberships_Abort()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-colorcup-dup-");
        try
        {
            Guid saveId = await SetupDuplicateFixtureAsync(store);
            SelectColorCupTeamsHandler handler = new(store);
            InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(
                () => handler.HandleAsync(saveId, sourceSeasonNumber: 3));
            ex.Message.ShouldContain("duplicate active memberships");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task HistoricalSelectionWithPool_RemainsReadable()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-colorcup-hist-");
        try
        {
            Guid saveId = await SetupHistoricalFixtureAsync(store);
            GetColorCupSelectionResponse summary = await new GetColorCupSelectionHandler(store)
                .HandleAsync(saveId, sourceSeasonNumber: 3);
            summary.TotalSelected.ShouldBe(32);
            int poolCount = CountPoolInSummary(summary);
            poolCount.ShouldBeGreaterThan(0);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task<Guid> SetupPoolFixtureAsync(SaveStore store)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            "Color Active Pool", 424211UL, 777001UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        Dictionary<int, List<int>> active = await TakeActiveByColorAsync(store, saveId, 4).ConfigureAwait(false);
        Dictionary<int, List<int>> pool = await TakePoolByColorAsync(store, saveId, active, 1).ConfigureAwait(false);
        List<int> allPool = pool.Values.SelectMany(v => v).ToList();
        await MarkSeasonOnePoolPrestigeAsync(store, saveId, allPool).ConfigureAwait(false);
        int sourceId = await CreateOddSeasonAsync(store, saveId, 3, active, pool,
            new Dictionary<int, int>(), 500_000).ConfigureAwait(false);
        _ = sourceId;
        ColorCupActiveFixtures.Remember(saveId, active, pool);
        return saveId;
    }

    private static async Task AssertPoolExcludedAsync(SaveStore store, Guid saveId, SelectColorCupTeamsResponse response)
    {
        response.SourceSeasonNumber.ShouldBe(3);
        response.TotalSelected.ShouldBe(32);
        response.Teams.Count.ShouldBe(8);
        (Dictionary<int, List<int>> active, Dictionary<int, List<int>> pool) = ColorCupActiveFixtures.Lookup(saveId);
        List<int> allPool = pool.Values.SelectMany(v => v).ToList();
        HashSet<int> selected = response.Teams.SelectMany(t => t.Members).Select(m => m.SaveAthleteId).ToHashSet();
        selected.Count.ShouldBe(32);
        foreach (int poolId in allPool)
        {
            selected.Contains(poolId).ShouldBeFalse($"pool athlete {poolId} must never qualify");
        }

        foreach (ColorCupTeamResult team in response.Teams)
        {
            team.Members.Count.ShouldBe(4);
        }

        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking()
            .SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);
        List<ColorCupSelectionEntity> rows = await context.ColorCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        rows.Count.ShouldBe(32);
        foreach (int poolId in allPool)
        {
            rows.Any(r => r.SaveAthleteId == poolId).ShouldBeFalse();
        }

        _ = active;
    }

    private static async Task<Guid> SetupF3FixtureAsync(SaveStore store)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            "Color F3 Eligible", 424212UL, 777002UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        Dictionary<int, List<int>> active = await TakeActiveByColorAsync(store, saveId, 4).ConfigureAwait(false);
        List<int> whiteExtra = await TakeExtraWhiteAsync(store, saveId, active).ConfigureAwait(false);
        active[(int)SportingColor.White].AddRange(whiteExtra);
        List<int> whiteActives = active[(int)SportingColor.White];
        List<int> whiteLow = whiteActives[..2];
        List<int> whiteF3High = whiteActives[2..6];
        Dictionary<int, int> highPoints = BuildF3Points(active, whiteF3High);
        Dictionary<int, string> tiers = BuildF3Tiers(active, whiteF3High);
        _ = await CreateOddSeasonWithTiersAsync(store, saveId, 3, active, tiers, highPoints).ConfigureAwait(false);
        ColorCupActiveFixtures.RememberF3(saveId, whiteLow, whiteF3High);
        return saveId;
    }

    private static async Task<List<int>> TakeExtraWhiteAsync(
        SaveStore store, Guid saveId, Dictionary<int, List<int>> active)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<SaveAthleteEntity> athletes = await context.SaveAthletes.AsNoTracking()
            .OrderBy(e => e.Id).ToListAsync().ConfigureAwait(false);
        HashSet<int> taken = active.Values.SelectMany(v => v).ToHashSet();
        return athletes.Where(e => e.SportingColor == (int)SportingColor.White && !taken.Contains(e.Id))
            .Take(2).Select(e => e.Id).ToList();
    }

    private static Dictionary<int, int> BuildF3Points(Dictionary<int, List<int>> active, List<int> whiteF3High)
    {
        HashSet<int> high = whiteF3High.ToHashSet();
        Dictionary<int, int> points = new();
        foreach (var kv in active)
        {
            foreach (int id in kv.Value)
            {
                points[id] = high.Contains(id) ? 100_000 : 0;
            }
        }

        return points;
    }

    private static Dictionary<int, string> BuildF3Tiers(Dictionary<int, List<int>> active, List<int> whiteF3High)
    {
        HashSet<int> high = whiteF3High.ToHashSet();
        Dictionary<int, string> tiers = new();
        foreach (var kv in active)
        {
            foreach (int id in kv.Value)
            {
                tiers[id] = high.Contains(id) ? "F3" : "F1";
            }
        }

        return tiers;
    }

    private static void AssertF3BeatsF1(SelectColorCupTeamsResponse response)
    {
        (List<int> low, List<int> high) = ColorCupActiveFixtures.LookupF3(response.SaveId);
        ColorCupTeamResult white = response.Teams.Single(t => t.SportingColor == (int)SportingColor.White);
        HashSet<int> selected = white.Members.Select(m => m.SaveAthleteId).ToHashSet();
        foreach (int id in high)
        {
            selected.Contains(id).ShouldBeTrue($"F3 contender {id} must be eligible");
        }

        foreach (int id in low)
        {
            selected.Contains(id).ShouldBeFalse("low F1 must lose to high F3");
        }
    }

    private static async Task<SelectColorCupTeamsResponse> SelectAsync(SaveStore store, Guid saveId)
    {
        return await new SelectColorCupTeamsHandler(store).HandleAsync(saveId, sourceSeasonNumber: 3).ConfigureAwait(false);
    }

    private static async Task<Guid> SetupDuplicateFixtureAsync(SaveStore store)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            "Color Duplicate", 424213UL, 777003UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        Dictionary<int, List<int>> active = await TakeActiveByColorAsync(store, saveId, 4).ConfigureAwait(false);
        Dictionary<int, List<int>> pool = await TakePoolByColorAsync(store, saveId, active, 0).ConfigureAwait(false);
        int sourceId = await CreateOddSeasonAsync(store, saveId, 3, active, pool,
            new Dictionary<int, int>(), 0).ConfigureAwait(false);
        await InsertDuplicateMembershipAsync(store, saveId, sourceId, active).ConfigureAwait(false);
        return saveId;
    }

    private static async Task InsertDuplicateMembershipAsync(
        SaveStore store, Guid saveId, int sourceId, Dictionary<int, List<int>> active)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int victim = active[(int)SportingColor.White][0];
        SeasonMembershipEntity existing = await context.SeasonMemberships
            .SingleAsync(e => e.SeasonId == sourceId && e.SaveAthleteId == victim).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync(
            "DROP INDEX IF EXISTS \"IX_SeasonMemberships_SeasonId_SaveAthleteId\";").ConfigureAwait(false);
        context.SeasonMemberships.Add(new SeasonMembershipEntity
        {
            SeasonId = sourceId,
            LeagueId = existing.LeagueId,
            SaveAthleteId = victim,
            SportingColor = existing.SportingColor,
            DrawIndex = 999,
        });
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<Guid> SetupHistoricalFixtureAsync(SaveStore store)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            "Color Historical", 424214UL, 777004UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        Dictionary<int, List<int>> active = await TakeActiveByColorAsync(store, saveId, 4).ConfigureAwait(false);
        Dictionary<int, List<int>> pool = await TakePoolByColorAsync(store, saveId, active, 1).ConfigureAwait(false);
        int sourceId = await CreateOddSeasonAsync(store, saveId, 3, active, pool,
            new Dictionary<int, int>(), 0).ConfigureAwait(false);
        await PersistHistoricalRowsAsync(store, saveId, sourceId, active, pool).ConfigureAwait(false);
        return saveId;
    }

    private static int CountPoolInSummary(GetColorCupSelectionResponse summary)
    {
        return summary.Teams.SelectMany(t => t.Members).Count(m => ColorCupActiveFixtures.IsHistoricalPool(m.SaveAthleteId));
    }

    private static async Task<Dictionary<int, List<int>>> TakeActiveByColorAsync(SaveStore store, Guid saveId, int perColor)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<SaveAthleteEntity> athletes = await context.SaveAthletes.AsNoTracking()
            .OrderBy(e => e.Id).ToListAsync().ConfigureAwait(false);
        Dictionary<int, List<int>> map = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            map[(int)color] = athletes.Where(e => e.SportingColor == (int)color)
                .Take(perColor).Select(e => e.Id).ToList();
            map[(int)color].Count.ShouldBe(perColor);
        }

        return map;
    }

    private static async Task<Dictionary<int, List<int>>> TakePoolByColorAsync(
        SaveStore store, Guid saveId, Dictionary<int, List<int>> activeByColor, int perColor)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<SaveAthleteEntity> athletes = await context.SaveAthletes.AsNoTracking()
            .OrderBy(e => e.Id).ToListAsync().ConfigureAwait(false);
        Dictionary<int, List<int>> map = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            HashSet<int> active = activeByColor[(int)color].ToHashSet();
            map[(int)color] = athletes.Where(e => e.SportingColor == (int)color && !active.Contains(e.Id))
                .Take(perColor).Select(e => e.Id).ToList();
            map[(int)color].Count.ShouldBe(perColor);
        }

        return map;
    }

    private static async Task MarkSeasonOnePoolPrestigeAsync(SaveStore store, Guid saveId, List<int> poolIds)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonOne = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        seasonOne.IsComplete = true;
        Dictionary<int, List<LeagueEntity>> byColor = (await context.Leagues.AsNoTracking()
            .Where(e => e.SeasonId == seasonOne.Id).ToListAsync().ConfigureAwait(false))
            .GroupBy(e => e.SportingColor).ToDictionary(g => g.Key, g => g.ToList());
        int rank = 1;
        foreach (int poolId in poolIds)
        {
            SaveAthleteEntity athlete = await context.SaveAthletes.SingleAsync(e => e.Id == poolId).ConfigureAwait(false);
            LeagueEntity league = byColor[athlete.SportingColor][0];
            context.SeasonStandings.Add(SeasonStanding(poolId, seasonOne.Id, league.Id, rank));
            rank++;
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static SeasonStandingEntity SeasonStanding(int athleteId, int seasonId, int leagueId, int rank)
    {
        return new SeasonStandingEntity
        {
            SeasonId = seasonId,
            LeagueId = leagueId,
            SaveAthleteId = athleteId,
            SeasonRank = rank,
            TotalChampionshipPointsThousandths = 100_000,
            TotalStageScoreThousandths = 0,
            TotalBaseScoreThousandths = 0,
            StageWins = 1,
            RoundWins = 0,
            StagePlaceCountsJson = "[]",
            RoundPlaceCountsJson = "[]",
            IsChampion = rank == 1,
        };
    }

    private static async Task<int> CreateOddSeasonAsync(
        SaveStore store, Guid saveId, int seasonNumber,
        Dictionary<int, List<int>> activeByColor, Dictionary<int, List<int>> poolByColor,
        Dictionary<int, int> highPointsByAthlete, int poolBonusThousandths)
    {
        Dictionary<int, string> tiers = ActiveTiers(activeByColor);
        Dictionary<int, int> points = ActivePoints(activeByColor, highPointsByAthlete);
        return await CreateOddSeasonWithTiersAsync(
            store, saveId, seasonNumber, activeByColor, tiers, points, poolByColor, poolBonusThousandths).ConfigureAwait(false);
    }

    private static Dictionary<int, string> ActiveTiers(Dictionary<int, List<int>> activeByColor)
    {
        Dictionary<int, string> tiers = new();
        foreach (var kv in activeByColor)
        {
            foreach (int id in kv.Value)
            {
                tiers[id] = "F1";
            }
        }

        return tiers;
    }

    private static Dictionary<int, int> ActivePoints(
        Dictionary<int, List<int>> activeByColor, Dictionary<int, int> highPointsByAthlete)
    {
        Dictionary<int, int> points = new();
        foreach (var kv in activeByColor)
        {
            foreach (int id in kv.Value)
            {
                points[id] = highPointsByAthlete.TryGetValue(id, out int v) ? v : 0;
            }
        }

        return points;
    }

    private static async Task<int> CreateOddSeasonWithTiersAsync(
        SaveStore store, Guid saveId, int seasonNumber,
        Dictionary<int, List<int>> activeByColor, Dictionary<int, string> tierByAthlete,
        Dictionary<int, int> pointsByAthlete)
    {
        return await CreateOddSeasonWithTiersAsync(
            store, saveId, seasonNumber, activeByColor, tierByAthlete, pointsByAthlete,
            new Dictionary<int, List<int>>(), 0).ConfigureAwait(false);
    }

    private static async Task<int> CreateOddSeasonWithTiersAsync(
        SaveStore store, Guid saveId, int seasonNumber,
        Dictionary<int, List<int>> activeByColor, Dictionary<int, string> tierByAthlete,
        Dictionary<int, int> pointsByAthlete,
        Dictionary<int, List<int>> poolByColor, int poolBonusThousandths)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = new() { SeasonNumber = seasonNumber, HasSuperleague = false, IsComplete = true };
        context.Seasons.Add(season);
        await context.SaveChangesAsync().ConfigureAwait(false);
        Dictionary<(int Color, string Tier), LeagueEntity> leagues = await CreateTierLeaguesAsync(
            context, season, activeByColor, tierByAthlete).ConfigureAwait(false);
        Dictionary<int, int> leagueByAthlete = await AddMembershipsAsync(
            context, season, activeByColor, poolByColor, tierByAthlete, leagues).ConfigureAwait(false);
        await AddSeasonStandingsAsync(context, season, activeByColor, pointsByAthlete, leagueByAthlete).ConfigureAwait(false);
        await AddStageStandingsAsync(context, season, activeByColor, pointsByAthlete, leagueByAthlete, poolByColor, poolBonusThousandths, leagueByAthlete).ConfigureAwait(false);
        return season.Id;
    }

    private static async Task<Dictionary<(int Color, string Tier), LeagueEntity>> CreateTierLeaguesAsync(
        SaveDbContext context, SeasonEntity season,
        Dictionary<int, List<int>> activeByColor, Dictionary<int, string> tierByAthlete)
    {
        Dictionary<(int Color, string Tier), LeagueEntity> leagues = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            List<string> tiers = activeByColor[(int)color]
                .Select(id => tierByAthlete.TryGetValue(id, out string? t) ? t : "F1")
                .Distinct(StringComparer.Ordinal).ToList();
            foreach (string tier in tiers)
            {
                LeagueEntity league = TierLeague(season.Id, (int)color, color, tier, season.SeasonNumber);
                context.Leagues.Add(league);
                await context.SaveChangesAsync().ConfigureAwait(false);
                leagues[((int)color, tier)] = league;
            }
        }

        return leagues;
    }

    private static LeagueEntity TierLeague(int seasonId, int color, SportingColor name, string tier, int seasonNumber)
    {
        return new LeagueEntity
        {
            SeasonId = seasonId,
            SportingColor = color,
            Kind = (int)LeagueKind.Feeder,
            FeederDivision = tier switch
            {
                "F2" => (int)FeederDivision.Second,
                "F3" => (int)FeederDivision.Third,
                _ => (int)FeederDivision.First,
            },
            Name = $"{name} {tier} S{seasonNumber}",
        };
    }

    private static async Task<Dictionary<int, int>> AddMembershipsAsync(
        SaveDbContext context, SeasonEntity season,
        Dictionary<int, List<int>> activeByColor, Dictionary<int, List<int>> poolByColor,
        Dictionary<int, string> tierByAthlete, Dictionary<(int Color, string Tier), LeagueEntity> leagues)
    {
        Dictionary<int, int> leagueByAthlete = new();
        foreach (var kv in activeByColor)
        {
            foreach (int athleteId in kv.Value)
            {
                string tier = tierByAthlete.TryGetValue(athleteId, out string? t) ? t : "F1";
                LeagueEntity league = leagues[(kv.Key, tier)];
                leagueByAthlete[athleteId] = league.Id;
                context.SeasonMemberships.Add(new SeasonMembershipEntity
                {
                    SeasonId = season.Id,
                    LeagueId = league.Id,
                    SaveAthleteId = athleteId,
                    SportingColor = kv.Key,
                    DrawIndex = 0,
                });
            }
        }

        foreach (var kv in poolByColor)
        {
            foreach (int athleteId in kv.Value)
            {
                context.SeasonMemberships.Add(new SeasonMembershipEntity
                {
                    SeasonId = season.Id,
                    LeagueId = null,
                    SaveAthleteId = athleteId,
                    SportingColor = kv.Key,
                    DrawIndex = 0,
                });
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
        return leagueByAthlete;
    }

    private static async Task AddSeasonStandingsAsync(
        SaveDbContext context, SeasonEntity season,
        Dictionary<int, List<int>> activeByColor, Dictionary<int, int> pointsByAthlete,
        Dictionary<int, int> leagueByAthlete)
    {
        Dictionary<int, int> rankByLeague = new();
        foreach (var kv in activeByColor)
        {
            foreach (int athleteId in kv.Value)
            {
                int points = pointsByAthlete.TryGetValue(athleteId, out int v) ? v : 0;
                int leagueId = leagueByAthlete[athleteId];
                rankByLeague.TryGetValue(leagueId, out int used);
                int rank = used + 1;
                rankByLeague[leagueId] = rank;
                context.SeasonStandings.Add(new SeasonStandingEntity
                {
                    SeasonId = season.Id,
                    LeagueId = leagueId,
                    SaveAthleteId = athleteId,
                    SeasonRank = rank,
                    TotalChampionshipPointsThousandths = points,
                    TotalStageScoreThousandths = 0,
                    TotalBaseScoreThousandths = 0,
                    StageWins = 0,
                    RoundWins = 0,
                    StagePlaceCountsJson = "[]",
                    RoundPlaceCountsJson = "[]",
                    IsChampion = rank == 1,
                });
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task AddStageStandingsAsync(
        SaveDbContext context, SeasonEntity season,
        Dictionary<int, List<int>> activeByColor, Dictionary<int, int> pointsByAthlete,
        Dictionary<int, int> leagueByAthlete,
        Dictionary<int, List<int>> poolByColor, int poolBonusThousandths,
        Dictionary<int, int> leagueFallback)
    {
        foreach (var kv in activeByColor)
        {
            foreach (int athleteId in kv.Value)
            {
                int points = pointsByAthlete.TryGetValue(athleteId, out int v) ? v : 0;
                int perStage = points == 0 ? 0 : 1000;
                for (int stage = 23; stage <= 32; stage++)
                {
                    context.StageStandings.Add(ActiveStage(season.Id, leagueByAthlete[athleteId], stage, athleteId, perStage));
                }
            }
        }

        if (poolBonusThousandths > 0)
        {
            int anyLeague = leagueFallback.Values.First();
            foreach (var kv in poolByColor)
            {
                foreach (int athleteId in kv.Value)
                {
                    for (int stage = 23; stage <= 32; stage++)
                    {
                        context.StageStandings.Add(PoolStage(season.Id, anyLeague, stage, athleteId, poolBonusThousandths));
                    }
                }
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static StageStandingEntity ActiveStage(int seasonId, int leagueId, int stage, int athleteId, int perStage)
    {
        return new StageStandingEntity
        {
            SeasonId = seasonId,
            LeagueId = leagueId,
            StageId = stage,
            StageNumber = stage,
            SaveAthleteId = athleteId,
            StageRank = 10,
            StageScoreThousandths = 0,
            BaseScoreThousandths = 0,
            ChampionshipPointsThousandths = perStage,
            RoundWins = 0,
            RoundPlaceCountsJson = "[]",
            EarnedBonusThousandths = 0,
        };
    }

    private static StageStandingEntity PoolStage(int seasonId, int leagueId, int stage, int athleteId, int bonus)
    {
        return new StageStandingEntity
        {
            SeasonId = seasonId,
            LeagueId = leagueId,
            StageId = stage,
            StageNumber = stage,
            SaveAthleteId = athleteId,
            StageRank = 1,
            StageScoreThousandths = 0,
            BaseScoreThousandths = 0,
            ChampionshipPointsThousandths = 0,
            RoundWins = 0,
            RoundPlaceCountsJson = "[]",
            EarnedBonusThousandths = bonus,
        };
    }

    private static async Task PersistHistoricalRowsAsync(
        SaveStore store, Guid saveId, int sourceId,
        Dictionary<int, List<int>> activeByColor, Dictionary<int, List<int>> poolByColor)
    {
        RulesV1 rules;
        SeasonEntity source;
        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            source = await context.Seasons.SingleAsync(e => e.Id == sourceId).ConfigureAwait(false);
            RulesSnapshotEntity snapshot = await context.RulesSnapshots.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
            rules = (RulesV1)MtgSoloSports.Persistence.Saves.RulesSnapshotCodec.Decode(snapshot.RulesJson);
        }

        List<ColorCupSelectionEntity> historical = BuildHistoricalRows(source, rules, activeByColor, poolByColor);
        ColorCupActiveFixtures.RememberHistorical(historical.Where(r => poolByColor.Values.SelectMany(v => v).Contains(r.SaveAthleteId)).Select(r => r.SaveAthleteId).ToList());
        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            context.ColorCupSelections.AddRange(historical);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            List<ColorCupSelectionEntity> persisted = await context.ColorCupSelections
                .Where(e => e.SourceSeasonId == sourceId).ToListAsync().ConfigureAwait(false);
            ColorCupSelectionInvariants.ValidatePersisted(source, persisted, rules);
            persisted.Count.ShouldBe(32);
        }
    }

    private static List<ColorCupSelectionEntity> BuildHistoricalRows(
        SeasonEntity source, RulesV1 rules,
        Dictionary<int, List<int>> activeByColor, Dictionary<int, List<int>> poolByColor)
    {
        List<ColorCupSelectionEntity> rows = new(32);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            List<int> actives = activeByColor[(int)color];
            List<int> pools = poolByColor.TryGetValue((int)color, out List<int>? p) ? p : [];
            List<int> squad = [.. actives.Take(3)];
            squad.Add(pools.Count > 0 ? pools[0] : actives[3]);
            for (int i = 0; i < 4; i++)
            {
                rows.Add(new ColorCupSelectionEntity
                {
                    SourceSeasonId = source.Id,
                    SourceSeasonNumber = source.SeasonNumber,
                    SportingColor = (int)color,
                    SaveAthleteId = squad[i],
                    SelectionRank = i + 1,
                    FinalRatingThousandths = 1000 - (i * 10),
                    BonusNormThousandths = 1000 - (i * 10),
                    PerformanceNormThousandths = 1000 - (i * 10),
                    FormNormThousandths = 1000 - (i * 10),
                    PrestigeNormThousandths = 1000 - (i * 10),
                    BonusRawThousandths = 0,
                    PerformanceRawThousandths = 0,
                    FormRaw = 0,
                    PrestigeRaw = 0,
                    RulesVersion = rules.Version,
                });
            }
        }

        return rows;
    }
}
