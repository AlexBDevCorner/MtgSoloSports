using MtgSoloSports.Features.Athletes.SearchAthletes;
using MtgSoloSports.Features.Leagues.Profiles;
using MtgSoloSports.Features.Leagues.SeasonProgress;
using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.ListHonours;
using MtgSoloSports.Features.Superleague.GetAutomaticMovement;
using MtgSoloSports.Features.Superleague.GetRebalanceResult;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Leagues;

/// <summary>
/// MSS-139: list/detail slices carry stable link identities (league keys and
/// sporting colors) so every named league, color team and Cup team can link
/// from Dashboard, Records, Standings, Live, History, search, Qualifiers and
/// the reveal boards without parsing display names. Missing or legacy
/// identity stays null so the frontend renders plain text.
/// </summary>
public sealed class EntityLinkIdentitiesTests
{
    [Fact]
    public async Task Honours_LeagueEntriesCarryParseableKeys_CupEntriesStayNull()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-entity-links-honours-");
        try
        {
            var handler = new ListHonoursHandler(store);
            var response = await handler.HandleAsync(saveId);
            response.Honours.Count.ShouldBeGreaterThan(0);

            bool sawLeague = false;
            foreach (var honour in response.Honours)
            {
                var kind = Enum.Parse<HonourKind>(honour.HonourKind);
                if (HonourKindMapper.IsLeagueHonourKind((int)kind))
                {
                    sawLeague = true;
                    honour.LeagueId.ShouldBeGreaterThan(0);
                    honour.LeagueKey.ShouldNotBeNullOrWhiteSpace();
                    LeagueKey.TryParse(honour.LeagueKey, out _, out _, out _).ShouldBeTrue();
                }
                else
                {
                    honour.LeagueKey.ShouldBeNull();
                }
            }

            sawLeague.ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SeasonProgress_LeaguesCarrySportingColors()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-entity-links-progress-");
        try
        {
            var handler = new GetSeasonProgressHandler(store);
            var response = await handler.HandleAsync(saveId, 1);
            response.Leagues.Count.ShouldBeGreaterThan(0);
            foreach (var league in response.Leagues)
            {
                int kind = string.Equals(league.LeagueKind, "Superleague", StringComparison.Ordinal) ? 1 : 0;
                string key = LeagueKey.Build(
                    kind,
                    league.FeederDivision,
                    league.SportingColor);
                LeagueKey.TryParse(key, out _, out _, out _).ShouldBeTrue();
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Search_ActiveAthletesCarryCurrentLeagueKeys_PoolAthletesStayNull()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-entity-links-search-");
        try
        {
            var handler = new SearchAthletesHandler(store);
            var response = await handler.HandleAsync(saveId, SearchAthletesQuery.Empty);
            response.Results.Count.ShouldBeGreaterThan(0);

            bool sawActive = false;
            bool sawPool = false;
            foreach (var result in response.Results)
            {
                if (result.IsActive)
                {
                    sawActive = true;
                    result.CurrentLeagueKey.ShouldNotBeNullOrWhiteSpace();
                    LeagueKey.TryParse(result.CurrentLeagueKey, out _, out _, out _).ShouldBeTrue();
                }
                else
                {
                    sawPool = true;
                    result.CurrentLeagueKey.ShouldBeNull();
                }
            }

            sawActive.ShouldBeTrue();
            sawPool.ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Qualifiers_MembersCarryFromLeagueKeys()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierResolvedAsync("mtgsolosports-entity-links-qual-");
        try
        {
            // The template stops with movement resolved; resolve the qualifiers
            // on this isolated fork like the qualifier tests do.
            await new RunAllQualifiersHandler(store).HandleAsync(saveId);
            var handler = new GetQualifierListHandler(store);
            var response = await handler.HandleAsync(saveId);
            response.Events.Count.ShouldBeGreaterThan(0);
            bool sawMember = false;
            foreach (var qualifier in response.Events)
            {
                foreach (var member in qualifier.Standings)
                {
                    sawMember = true;
                    member.FromLeagueKey.ShouldNotBeNullOrWhiteSpace();
                    LeagueKey.TryParse(member.FromLeagueKey, out _, out _, out _).ShouldBeTrue();
                }
            }

            sawMember.ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AutomaticMovement_MembersCarryLeagueKeys()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierResolvedAsync("mtgsolosports-entity-links-move-");
        try
        {
            var handler = new GetAutomaticMovementHandler(store);
            var response = await handler.HandleAsync(saveId);
            response.MovementCount.ShouldBeGreaterThan(0);
            bool sawMember = false;
            foreach (var member in response.Promoted.Concat(response.Relegated))
            {
                sawMember = true;
                member.FromLeagueKey.ShouldNotBeNullOrWhiteSpace();
                member.ToLeagueKey.ShouldNotBeNullOrWhiteSpace();
                LeagueKey.TryParse(member.FromLeagueKey, out _, out _, out _).ShouldBeTrue();
                LeagueKey.TryParse(member.ToLeagueKey, out _, out _, out _).ShouldBeTrue();
            }

            sawMember.ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Rebalance_MembersCarryLeagueKeys_PoolSidesStayNull()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-entity-links-rebal-");
        try
        {
            var handler = new GetRebalanceResultHandler(store);
            var response = await handler.HandleAsync(saveId);
            response.MovementCount.ShouldBeGreaterThan(0);

            bool sawMember = false;
            foreach (var member in response.Departed
                         .Concat(response.Returned)
                         .Concat(response.Draws)
                         .Concat(response.Displaced))
            {
                sawMember = true;
                if (member.FromLeagueId == 0)
                {
                    member.FromLeagueKey.ShouldBeNull();
                }
                else
                {
                    member.FromLeagueKey.ShouldNotBeNullOrWhiteSpace();
                    LeagueKey.TryParse(member.FromLeagueKey, out _, out _, out _).ShouldBeTrue();
                }

                if (member.ToLeagueId == 0)
                {
                    member.ToLeagueKey.ShouldBeNull();
                }
                else
                {
                    member.ToLeagueKey.ShouldNotBeNullOrWhiteSpace();
                    LeagueKey.TryParse(member.ToLeagueKey, out _, out _, out _).ShouldBeTrue();
                }
            }

            sawMember.ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }
}
