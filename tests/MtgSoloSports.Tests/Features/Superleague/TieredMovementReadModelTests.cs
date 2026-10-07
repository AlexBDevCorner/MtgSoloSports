using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Athletes.SearchAthletes;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.GetAutomaticMovement;
using MtgSoloSports.Features.Superleague.GetFeederMovements;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Superleague;

/// <summary>
/// MSS-060 acceptance tests: tiered movement read models carry adjacent-tier
/// identity without league-name parsing, and athlete surfaces expose the
/// feeder division for every season/movement.
/// </summary>
public sealed class TieredMovementReadModelTests
{
    [Fact]
    public async Task FeederMovements_TieredTransition_Returns512WithBoundaryIdentity()
    {
        // MSS-067: shared pre-resolve qualifier template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierPreResolveAsync("mtgsolosports-tiered-");
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);
            GetFeederMovementsResponse response = await new GetFeederMovementsHandler(store).HandleAsync(saveId, 2);

            response.FromSeasonNumber.ShouldBe(2);
            response.ToSeasonNumber.ShouldBe(3);
            response.MovementCount.ShouldBe(512);
            response.Movements.Count.ShouldBe(512);
            AssertBoundaryTotals(response);
            AssertAdjacentTiers(response);

            // Spot-check White F2 champion: promoted out of Feeder2 into the F1↔F2 boundary.
            FeederMovementMember whitePromotion = response.Movements.Single(m =>
                string.Equals(m.SportingColorName, nameof(SportingColor.White), StringComparison.Ordinal)
                && string.Equals(m.MovementKind, nameof(MovementKind.FeederAutomaticPromotion), StringComparison.Ordinal)
                && string.Equals(m.Boundary, QualifierBoundary.Feeder1Feeder2.ToString(), StringComparison.Ordinal)
                && m.FromSeasonRank == 1);
            whitePromotion.FromLeagueLevel.ShouldBe(nameof(LeagueLevel.Feeder2));
            whitePromotion.ToLeagueLevel.ShouldBe(nameof(LeagueLevel.Feeder1));

            // Latest-transition default matches the explicit fromSeason read.
            GetFeederMovementsResponse latest = await new GetFeederMovementsHandler(store).HandleAsync(saveId);
            latest.FromSeasonNumber.ShouldBe(2);
            latest.MovementCount.ShouldBe(512);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AutomaticMovement_TieredTransition_CarriesTierLevels()
    {
        // MSS-067: shared pre-resolve qualifier template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierPreResolveAsync("mtgsolosports-tiered-");
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);
            GetAutomaticMovementResponse movement = await new GetAutomaticMovementHandler(store).HandleAsync(saveId, 2);

            movement.Promoted.Count.ShouldBe(8);
            foreach (AutomaticMovementMember member in movement.Promoted)
            {
                member.FromLeagueLevel.ShouldBe(nameof(LeagueLevel.Feeder1));
                member.ToLeagueLevel.ShouldBe(nameof(LeagueLevel.Superleague));
            }

            foreach (AutomaticMovementMember member in movement.Relegated)
            {
                member.FromLeagueLevel.ShouldBe(nameof(LeagueLevel.Superleague));
                member.ToLeagueLevel.ShouldBe(nameof(LeagueLevel.Feeder1));
            }

            // Unknown seasons still fail closed.
            await Should.ThrowAsync<AutomaticMovementNotFoundException>(
                () => new GetAutomaticMovementHandler(store).HandleAsync(saveId, 9));
            await Should.ThrowAsync<FeederMovementsNotFoundException>(
                () => new GetFeederMovementsHandler(store).HandleAsync(saveId, 9));
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FeederMovements_V1Save_ReturnsNotFound()
    {
        var (store, root) = PostseasonTestSaves.CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("MSS060 V1", 111UL, 222UL, UniverseTestCatalog.Build());
            await Should.ThrowAsync<FeederMovementsNotFoundException>(
                () => new GetFeederMovementsHandler(store).HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteProfile_TieredSave_SeasonsAndMovementsCarryLevels()
    {
        // MSS-067: shared pre-resolve qualifier template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierPreResolveAsync("mtgsolosports-tiered-");
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);

            // F1 White champion: rank 1 in the F1 White synthetic standings.
            int championId = await FindRankAthleteAsync(store, saveId, 2, SportingColor.White, FeederDivision.First, 1);
            GetAthleteProfileResponse profile = await new GetAthleteProfileHandler(store).HandleAsync(saveId, championId);

            profile.Seasons.Count.ShouldBeGreaterThan(0);
            profile.Seasons.All(s => !s.WasActive || (s.LeagueLevel is not null && s.FeederDivision.HasValue)).ShouldBeTrue();

            AthleteMovementDto promotion = profile.Movements.Single(m =>
                string.Equals(m.Kind, nameof(MovementKind.AutomaticPromotion), StringComparison.Ordinal));
            promotion.FromLeagueLevel.ShouldBe(nameof(LeagueLevel.Feeder1));
            promotion.ToLeagueLevel.ShouldBe(nameof(LeagueLevel.Superleague));
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteSearch_TieredSave_OptionsAndResultsCarryLevels()
    {
        // MSS-067: shared pre-resolve qualifier template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierPreResolveAsync("mtgsolosports-tiered-");
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);
            SearchAthletesHandler handler = new(store);

            SearchAthletesOptionsResponse options = await handler.HandleOptionsAsync(saveId);
            AthleteSearchLeagueOption? white = options.CurrentLeagues.FirstOrDefault(o => string.Equals(o.Name, "White League", StringComparison.Ordinal));
            white.ShouldNotBeNull();
            white!.LeagueLevel.ShouldBe(nameof(LeagueLevel.Feeder1));
            white.FeederDivision.ShouldBe((int)FeederDivision.First);
            AthleteSearchLeagueOption? pool = options.CurrentLeagues.FirstOrDefault(o => o.IsPool);
            if (pool is not null)
            {
                pool.LeagueLevel.ShouldBeNull();
            }

            SearchAthletesResponse page = await handler.HandleAsync(
                saveId, SearchAthletesQuery.Empty with { CurrentLeagues = new[] { "White League" } });
            page.TotalCount.ShouldBeGreaterThan(0);
            page.Results.All(r => string.Equals(r.CurrentLeagueLevel, nameof(LeagueLevel.Feeder1), StringComparison.Ordinal)).ShouldBeTrue();
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    private static void AssertBoundaryTotals(GetFeederMovementsResponse response)
    {
        // 256 per boundary, 8 athletes per color per kind per boundary.
        response.Movements.Count(m => m.BoundaryId == (int)QualifierBoundary.Feeder1Feeder2).ShouldBe(256);
        response.Movements.Count(m => m.BoundaryId == (int)QualifierBoundary.Feeder2Feeder3).ShouldBe(256);
        foreach (QualifierBoundary boundary in new[] { QualifierBoundary.Feeder1Feeder2, QualifierBoundary.Feeder2Feeder3 })
        {
            foreach (SportingColor color in Enum.GetValues<SportingColor>())
            {
                foreach (string kind in new[]
                    {
                        nameof(MovementKind.FeederAutomaticPromotion),
                        nameof(MovementKind.FeederAutomaticRelegation),
                        nameof(MovementKind.FeederQualifierIncumbent),
                        nameof(MovementKind.FeederQualifierChallenger),
                    })
                {
                    response.Movements.Count(m =>
                        string.Equals(m.Boundary, boundary.ToString(), StringComparison.Ordinal)
                        && m.SportingColor == (int)color
                        && string.Equals(m.MovementKind, kind, StringComparison.Ordinal)).ShouldBe(8);
                }
            }
        }
    }

    private static void AssertAdjacentTiers(GetFeederMovementsResponse response)
    {
        // Adjacent tiers only, never skipping a level.
        foreach (FeederMovementMember member in response.Movements)
        {
            member.FromLeagueLevel.ShouldNotBeNull();
            member.ToLeagueLevel.ShouldNotBeNull();
            Math.Abs(LevelOrder(member.FromLeagueLevel!) - LevelOrder(member.ToLeagueLevel!)).ShouldBeLessThanOrEqualTo(1);
        }
    }

    private static int LevelOrder(string level)
    {
        if (string.Equals(level, nameof(LeagueLevel.Superleague), StringComparison.Ordinal))
        {
            return 0;
        }

        if (string.Equals(level, nameof(LeagueLevel.Feeder1), StringComparison.Ordinal))
        {
            return 1;
        }

        if (string.Equals(level, nameof(LeagueLevel.Feeder2), StringComparison.Ordinal))
        {
            return 2;
        }

        if (string.Equals(level, nameof(LeagueLevel.Feeder3), StringComparison.Ordinal))
        {
            return 3;
        }

        throw new InvalidOperationException($"Unknown league level '{level}'.");
    }

    private static async Task<int> FindRankAthleteAsync(
        SaveStore store, Guid saveId, int seasonNumber, SportingColor color, FeederDivision division, int rank)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        LeagueEntity league = await context.Leagues.AsNoTracking().SingleAsync(e =>
            e.SeasonId == season.Id && e.SportingColor == (int)color && e.FeederDivision == (int)division).ConfigureAwait(false);
        SeasonStandingEntity row = await context.SeasonStandings.AsNoTracking().SingleAsync(e =>
            e.SeasonId == season.Id && e.LeagueId == league.Id && e.SeasonRank == rank).ConfigureAwait(false);
        return row.SaveAthleteId;
    }
}
