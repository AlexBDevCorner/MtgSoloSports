using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-100: frozen 1024-entrant field plus balanced 32x32 first draw.
/// Covers the ordinary 800-active season, Season 1 768-active and a skewed
/// Superleague composition; proves 4/color/group, all actives included,
/// same seed/state determinism and idempotent retries without RNG reroll.
/// </summary>
public sealed class ChaosCupFieldTests
{
    [Fact]
    public async Task Season1_768Active_BalancedDraw_AllActivesIncluded()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-field-s1-");
        try
        {
            SelectChaosCupFieldHandler handler = new(store);
            SelectChaosCupFieldResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 1);

            response.SourceSeasonNumber.ShouldBe(1);
            response.FieldSize.ShouldBe(ChaosCupFormat.FieldSize);
            response.ActiveEntrants.ShouldBe(768);
            response.PoolInvites.ShouldBe(256);
            foreach (SelectChaosCupFieldResponse.PerColorQuota quota in response.PerColor)
            {
                quota.Active.ShouldBe(96);
                quota.Invited.ShouldBe(32);
            }

            await AssertFieldValidAsync(store, saveId, 1, 768, response);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task OrdinarySeason_800Active_BalancedDraw()
    {
        var (store, root, saveId) = await PrepareSeasonTwoAsync("mtgsolosports-chaos-field-800-");
        try
        {
            SelectChaosCupFieldHandler handler = new(store);
            SelectChaosCupFieldResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);

            response.SourceSeasonNumber.ShouldBe(2);
            response.FieldSize.ShouldBe(ChaosCupFormat.FieldSize);
            response.ActiveEntrants.ShouldBe(800);
            response.PoolInvites.ShouldBe(224);
            response.PerColor.Sum(q => q.Active).ShouldBe(800);
            foreach (SelectChaosCupFieldResponse.PerColorQuota quota in response.PerColor)
            {
                (quota.Active + quota.Invited).ShouldBe(ChaosCupFormat.EntrantsPerColor);
            }

            await AssertFieldValidAsync(store, saveId, 2, 800, response);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SkewedSuperleague_StillBalancesFourPerColor()
    {
        var (store, root, saveId) = await PrepareSeasonTwoAsync("mtgsolosports-chaos-field-skew-");
        try
        {
            await SkewSuperleagueAsync(store, saveId);
            SelectChaosCupFieldHandler handler = new(store);
            SelectChaosCupFieldResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);

            response.FieldSize.ShouldBe(ChaosCupFormat.FieldSize);
            response.ActiveEntrants.ShouldBe(800);
            Dictionary<SportingColor, int> activeByColor = response.PerColor
                .ToDictionary(q => (SportingColor)q.SportingColor, q => q.Active);
            activeByColor.Values.Sum().ShouldBe(800);
            activeByColor[SportingColor.White].ShouldBe(106);
            activeByColor[SportingColor.Green].ShouldBe(96);
            foreach (SelectChaosCupFieldResponse.PerColorQuota quota in response.PerColor)
            {
                (quota.Active + quota.Invited).ShouldBe(ChaosCupFormat.EntrantsPerColor);
            }

            await AssertFieldValidAsync(store, saveId, 2, 800, response);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SameSeed_SameDraw_RetryIdempotentWithoutReroll()
    {
        var (storeA, rootA, saveA) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-field-det-a-");
        var (storeB, rootB, saveB) = await CloneSaveAsync(storeA, saveA, "mtgsolosports-chaos-field-det-b-");
        try
        {
            SelectChaosCupFieldHandler handlerA = new(storeA);
            SelectChaosCupFieldHandler handlerB = new(storeB);
            SelectChaosCupFieldResponse firstA = await handlerA.HandleAsync(saveA, sourceSeasonNumber: 1);
            SelectChaosCupFieldResponse firstB = await handlerB.HandleAsync(saveB, sourceSeasonNumber: 1);

            firstA.EditionChecksum.ShouldBe(firstB.EditionChecksum);
            firstA.DrawChecksum.ShouldBe(firstB.DrawChecksum);
            firstA.Groups.Count.ShouldBe(32);
            for (int i = 0; i < 32; i++)
            {
                firstA.Groups[i].AthleteIds.ShouldBe(firstB.Groups[i].AthleteIds.ToList());
            }

            (long stateBefore, long streamBefore) = await LoadRngAsync(storeA, saveA);
            SelectChaosCupFieldResponse retry = await handlerA.HandleAsync(saveA, sourceSeasonNumber: 1);
            (long stateAfter, long streamAfter) = await LoadRngAsync(storeA, saveA);

            retry.EditionChecksum.ShouldBe(firstA.EditionChecksum);
            retry.DrawChecksum.ShouldBe(firstA.DrawChecksum);
            stateAfter.ShouldBe(stateBefore);
            streamAfter.ShouldBe(streamBefore);

            SelectChaosCupFieldResponse queried = await handlerA.GetAsync(saveA, sourceSeasonNumber: 1);
            queried.EditionChecksum.ShouldBe(firstA.EditionChecksum);

            await AssertSingleEditionAsync(storeA, saveA);
        }
        finally
        {
            TestSaveStores.DeleteRoot(rootA);
            TestSaveStores.DeleteRoot(rootB);
        }
    }

    [Fact]
    public async Task FrozenBonuses_MatchEffectiveContext_AndSurviveProgression()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-field-bonus-");
        try
        {
            SelectChaosCupFieldHandler handler = new(store);
            SelectChaosCupFieldResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 1);
            response.FieldSize.ShouldBe(ChaosCupFormat.FieldSize);

            Dictionary<int, int> frozen = await LoadFrozenBonusesAsync(store, saveId, 1);
            frozen.Count.ShouldBe(ChaosCupFormat.FieldSize);
            foreach (int thousandths in frozen.Values)
            {
                thousandths.ShouldBeGreaterThanOrEqualTo(0);
            }

            frozen.Values.Any(v => v > 0).ShouldBeTrue("edition-time effective bonuses must be frozen, not all zero");
            Dictionary<int, int> expected = await ComputeExpectedBonusesAsync(store, saveId, 1);
            foreach ((int athleteId, int want) in expected)
            {
                frozen[athleteId].ShouldBe(want, $"frozen bonus for athlete {athleteId} must match edition-time effective bonus");
            }

            // Subsequent season progression (inaugural + feeders + synthetic standings)
            // must not rewrite the frozen edition-time bonus context.
            await new CreateInauguralSuperleagueHandler(store).HandleAsync(saveId);
            await PostseasonTestSaves.FillSeasonTwoFeedersAsync(store, saveId);
            await PostseasonTestSaves.InsertSyntheticSeasonTwoStandingsAsync(store, saveId);

            Dictionary<int, int> reloaded = await LoadFrozenBonusesAsync(store, saveId, 1);
            foreach ((int athleteId, int want) in frozen)
            {
                reloaded[athleteId].ShouldBe(want, $"frozen bonus for athlete {athleteId} must survive season progression");
            }

            SelectChaosCupFieldResponse retry = await handler.HandleAsync(saveId, sourceSeasonNumber: 1);
            retry.EditionChecksum.ShouldBe(response.EditionChecksum);
            Dictionary<int, int> afterRetry = await LoadFrozenBonusesAsync(store, saveId, 1);
            foreach ((int athleteId, int want) in frozen)
            {
                afterRetry[athleteId].ShouldBe(want);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareSeasonTwoAsync(string prefix)
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync(prefix).ConfigureAwait(false);
        await new CreateInauguralSuperleagueHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        await PostseasonTestSaves.FillSeasonTwoFeedersAsync(store, saveId).ConfigureAwait(false);
        await PostseasonTestSaves.InsertSyntheticSeasonTwoStandingsAsync(store, saveId).ConfigureAwait(false);
        return (store, root, saveId);
    }

    private static async Task SkewSuperleagueAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        LeagueEntity superleague = await context.Leagues.SingleAsync(
            e => e.SeasonId == seasonTwo.Id && e.Kind == (int)LeagueKind.Superleague).ConfigureAwait(false);
        Dictionary<SportingColor, int> target = new()
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
        List<SeasonMembershipEntity> current = await context.SeasonMemberships
            .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == superleague.Id)
            .ToListAsync().ConfigureAwait(false);
        Dictionary<SportingColor, List<SeasonMembershipEntity>> byColor = current
            .GroupBy(m => (SportingColor)m.SportingColor)
            .ToDictionary(g => g.Key, g => g.ToList());
        List<SeasonMembershipEntity> pool = await context.SeasonMemberships
            .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == null)
            .ToListAsync().ConfigureAwait(false);
        Dictionary<SportingColor, List<SeasonMembershipEntity>> poolByColor = pool
            .GroupBy(m => (SportingColor)m.SportingColor)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.SaveAthleteId).ToList());
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            await RebalanceColorAsync(byColor, poolByColor, color, target[color]).ConfigureAwait(false);
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static Task RebalanceColorAsync(
        Dictionary<SportingColor, List<SeasonMembershipEntity>> byColor,
        Dictionary<SportingColor, List<SeasonMembershipEntity>> poolByColor,
        SportingColor color,
        int want)
    {
        int have = byColor.TryGetValue(color, out List<SeasonMembershipEntity>? list) ? list.Count : 0;
        if (want > have)
        {
            List<SeasonMembershipEntity> candidates = poolByColor[color].Take(want - have).ToList();
            candidates.Count.ShouldBe(want - have);
            foreach (SeasonMembershipEntity membership in candidates)
            {
                membership.LeagueId = byColor.Values.SelectMany(v => v).First().LeagueId;
            }
        }
        else if (want < have)
        {
            List<SeasonMembershipEntity> demoted = byColor[color]
                .OrderBy(m => m.SaveAthleteId).Take(have - want).ToList();
            foreach (SeasonMembershipEntity membership in demoted)
            {
                membership.LeagueId = null;
            }
        }

        return Task.CompletedTask;
    }

    private static async Task AssertFieldValidAsync(
        SaveStore store,
        Guid saveId,
        int sourceSeasonNumber,
        int expectedActive,
        SelectChaosCupFieldResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == sourceSeasonNumber).ConfigureAwait(false);
        ChaosCupEditionEntity edition = await context.ChaosCupEditions.AsNoTracking().SingleAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false);
        List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == 0).ToListAsync().ConfigureAwait(false);
        await AssertEntrantsAsync(context, source, expectedActive, entrants).ConfigureAwait(false);
        AssertDraw(response, entrants, draws, edition);
    }

    private static async Task AssertEntrantsAsync(
        SaveDbContext context,
        SeasonEntity source,
        int expectedActive,
        List<ChaosCupEntrantEntity> entrants)
    {
        var rules = MtgSoloSports.SimulationKernel.Rules.RulesV3.CreateDefault();
        entrants.Count.ShouldBe(ChaosCupFormat.FieldSize);
        entrants.Select(e => e.SaveAthleteId).Distinct().Count().ShouldBe(ChaosCupFormat.FieldSize);
        entrants.Count(e => !e.IsPoolInvite).ShouldBe(expectedActive);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            entrants.Count(e => e.SportingColor == (int)color).ShouldBe(ChaosCupFormat.EntrantsPerColor);
        }

        HashSet<int> activeIds = (await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId != null)
            .Select(e => e.SaveAthleteId).ToListAsync().ConfigureAwait(false)).ToHashSet();
        activeIds.Count.ShouldBe(expectedActive);
        foreach (int id in activeIds)
        {
            entrants.Any(e => e.SaveAthleteId == id).ShouldBeTrue($"active athlete {id} must be frozen");
        }

        HashSet<int> poolIds = (await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == null)
            .Select(e => e.SaveAthleteId).ToListAsync().ConfigureAwait(false)).ToHashSet();
        foreach (ChaosCupEntrantEntity entrant in entrants.Where(e => e.IsPoolInvite))
        {
            poolIds.ShouldContain(entrant.SaveAthleteId);
            entrant.SourceLeagueId.ShouldBeNull();
        }

        ChaosCupEditionEntity edition = await context.ChaosCupEditions.AsNoTracking().SingleAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false);
        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == 0).ToListAsync().ConfigureAwait(false);
        ChaosCupInvariants.ValidateEdition(edition, rules);
        ChaosCupInvariants.ValidateEntrants(source, edition, entrants);
        ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, draws, 0);
    }

    private static void AssertDraw(
        SelectChaosCupFieldResponse response,
        List<ChaosCupEntrantEntity> entrants,
        List<ChaosCupDrawEntity> draws,
        ChaosCupEditionEntity edition)
    {
        draws.Count.ShouldBe(ChaosCupFormat.FieldSize);
        response.Groups.Count.ShouldBe(32);
        Dictionary<int, SportingColor> colorByAthlete = entrants.ToDictionary(e => e.SaveAthleteId, e => (SportingColor)e.SportingColor);
        foreach (SelectChaosCupFieldResponse.DrawGroup group in response.Groups)
        {
            group.AthleteIds.Count.ShouldBe(32);
            Dictionary<SportingColor, int> perColor = new();
            foreach (int id in group.AthleteIds)
            {
                SportingColor color = colorByAthlete[id];
                perColor.TryGetValue(color, out int current);
                perColor[color] = current + 1;
            }

            foreach (SportingColor color in Enum.GetValues<SportingColor>())
            {
                perColor.ShouldContainKey(color);
                perColor[color].ShouldBe(ChaosCupFormat.FirstStagePerColorPerGroup);
            }
        }

        edition.EditionChecksum.ShouldBe(response.EditionChecksum);
        draws[0].DrawChecksum.ShouldBe(response.DrawChecksum);
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> CloneSaveAsync(SaveStore source, Guid saveId, string prefix)
    {
        return await TestSaveStores.ForkAsync(source, saveId, prefix).ConfigureAwait(false);
    }

    private static async Task<(long State, long Stream)> LoadRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }

    private static async Task AssertSingleEditionAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        (await context.ChaosCupEditions.CountAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false)).ShouldBe(1);
        (await context.ChaosCupEntrants.CountAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false)).ShouldBe(1024);
        (await context.ChaosCupDraws.CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == 0).ConfigureAwait(false)).ShouldBe(1024);
    }

    private static async Task<Dictionary<int, int>> LoadFrozenBonusesAsync(SaveStore store, Guid saveId, int sourceSeasonNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == sourceSeasonNumber).ConfigureAwait(false);
        return await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, e => e.EffectiveBonusThousandths)
            .ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, int>> ComputeExpectedBonusesAsync(SaveStore store, Guid saveId, int sourceSeasonNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == sourceSeasonNumber).ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, default).ConfigureAwait(false);
        Dictionary<int, int> seasonNumbers = await context.Seasons
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber)
            .ConfigureAwait(false);
        int cupSeason = checked(seasonNumbers[source.Id] + 1);
        IReadOnlySet<int> relevant = AdvanceRoundHandler.SelectBonusSeasonIds(seasonNumbers, cupSeason);
        List<int> entrantIds = await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .Select(e => e.SaveAthleteId)
            .ToListAsync()
            .ConfigureAwait(false);
        HashSet<int> rosterIds = entrantIds.ToHashSet();
        List<StageStandingEntity> standings = await context.StageStandings
            .AsNoTracking()
            .Where(e => rosterIds.Contains(e.SaveAthleteId) && relevant.Contains(e.SeasonId))
            .ToListAsync()
            .ConfigureAwait(false);
        Dictionary<int, List<BonusContribution>> byAthlete = rosterIds.ToDictionary(id => id, _ => new List<BonusContribution>());
        foreach (StageStandingEntity row in standings)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int earnedSeason))
            {
                continue;
            }

            byAthlete[row.SaveAthleteId].Add(new BonusContribution(
                earnedSeason,
                row.StageNumber,
                Bonus.FromThousandths(row.EarnedBonusThousandths)));
        }

        Dictionary<int, int> expected = new(rosterIds.Count);
        foreach (int athleteId in rosterIds)
        {
            Bonus bonus = BonusCalculator.EffectiveBonus(byAthlete[athleteId], cupSeason, 1, rules);
            expected[athleteId] = bonus.Thousandths;
        }

        return expected;
    }
}
