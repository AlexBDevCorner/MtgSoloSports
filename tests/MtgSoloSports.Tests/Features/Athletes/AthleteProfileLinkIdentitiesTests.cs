using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Leagues.Profiles;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Athletes;

/// <summary>
/// MSS-138: athlete profiles carry stable link identities (league keys and
/// cup/team keys) so every named league, color team, Type Cup team and Cup
/// edition can link without parsing display names. Covered on a tiered save
/// with real Cups, movements and honours.
/// </summary>
public sealed class AthleteProfileLinkIdentitiesTests
{
    [Fact]
    public async Task FeederAthleteWithCups_ExposesLinkIdentities()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-profile-links-");
        try
        {
            GetAthleteProfileHandler handler = new(store);
            GetAthleteProfileResponse profile = await FindFeederProfileWithCupsAsync(store, saveId, handler);
            AssertCardIdentity(profile);
            AssertSeasonIdentities(profile);
            AssertHonourIdentities(profile);
            AssertMovementIdentities(profile);
            AssertSelectionIdentities(profile);
            AssertCupHistoryIdentities(profile);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PoolAthlete_RendersPlainTextWithoutKeys()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-profile-links-pool-");
        try
        {
            GetAthleteProfileHandler handler = new(store);
            GetAthleteProfileResponse profile = await FindPoolProfileAsync(store, saveId, handler);
            profile.Career.IsActive.ShouldBeFalse();
            profile.Career.CurrentLeagueKey.ShouldBeNull();
            foreach (AthleteSeasonDto season in profile.Seasons.Where(s => !s.WasActive))
            {
                season.LeagueKey.ShouldBeNull();
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static void AssertCardIdentity(GetAthleteProfileResponse profile)
    {
        profile.Card.SportingColorTeamKey.ShouldNotBeNullOrWhiteSpace();
        profile.Card.SportingColorTeamKey.ShouldBe(CupTeamKeys.ColorKey((SportingColor)profile.Card.SportingColor));
        profile.Career.IsActive.ShouldBeTrue();
        profile.Career.CurrentLeagueId.ShouldNotBeNull();
        profile.Career.CurrentLeagueKey.ShouldNotBeNullOrWhiteSpace();
        LeagueKey.TryParse(profile.Career.CurrentLeagueKey, out _, out _, out _).ShouldBeTrue();
    }

    private static void AssertSeasonIdentities(GetAthleteProfileResponse profile)
    {
        profile.Seasons.Count.ShouldBeGreaterThan(0);
        foreach (AthleteSeasonDto season in profile.Seasons)
        {
            if (season.WasActive)
            {
                season.LeagueId.ShouldNotBeNull();
                season.LeagueKey.ShouldNotBeNullOrWhiteSpace();
                LeagueKey.TryParse(season.LeagueKey, out _, out _, out _).ShouldBeTrue();
            }
            else
            {
                season.LeagueKey.ShouldBeNull();
            }
        }

        profile.Seasons.Any(s => s.WasActive && s.LeagueKey != null).ShouldBeTrue();
    }

    private static void AssertHonourIdentities(GetAthleteProfileResponse profile)
    {
        profile.Honours.Count.ShouldBeGreaterThan(0);
        foreach (AthleteHonourDto honour in profile.Honours)
        {
            bool isLeague = honour.HonourKind.StartsWith("Feeder", StringComparison.Ordinal)
                || honour.HonourKind.StartsWith("Superleague", StringComparison.Ordinal);
            if (isLeague)
            {
                honour.LeagueKey.ShouldNotBeNullOrWhiteSpace();
                honour.Cup.ShouldBeNull();
                LeagueKey.TryParse(honour.LeagueKey, out _, out _, out _).ShouldBeTrue();
            }
            else
            {
                honour.Cup.ShouldBeOneOf("color", "type", "chaos");
                honour.LeagueKey.ShouldBeNull();
            }
        }
    }

    private static void AssertMovementIdentities(GetAthleteProfileResponse profile)
    {
        profile.Movements.Count.ShouldBeGreaterThan(0);
        foreach (AthleteMovementDto movement in profile.Movements)
        {
            if (string.Equals(movement.FromLeagueName, "Common pool", StringComparison.Ordinal))
            {
                movement.FromLeagueKey.ShouldBeNull();
            }
            else
            {
                movement.FromLeagueKey.ShouldNotBeNullOrWhiteSpace();
            }

            if (string.Equals(movement.ToLeagueName, "Common pool", StringComparison.Ordinal))
            {
                movement.ToLeagueKey.ShouldBeNull();
            }
            else
            {
                movement.ToLeagueKey.ShouldNotBeNullOrWhiteSpace();
            }
        }
    }

    private static void AssertSelectionIdentities(GetAthleteProfileResponse profile)
    {
        profile.CupSelections.Count.ShouldBeGreaterThan(0);
        foreach (AthleteCupSelectionDto selection in profile.CupSelections)
        {
            selection.Cup.ShouldBeOneOf("color", "type");
            selection.TeamKey.ShouldNotBeNullOrWhiteSpace();
            if (string.Equals(selection.CupKind, "ColorCup", StringComparison.Ordinal))
            {
                selection.Cup.ShouldBe("color");
                selection.TeamKey.ShouldBe(selection.Team.ToLowerInvariant());
            }
            else
            {
                selection.Cup.ShouldBe("type");
                selection.TeamKey.ShouldBe(selection.Team);
            }
        }
    }

    private static void AssertCupHistoryIdentities(GetAthleteProfileResponse profile)
    {
        profile.CupHistory.Count.ShouldBeGreaterThan(0);
        foreach (AthleteCupHistoryDto entry in profile.CupHistory)
        {
            entry.TeamKey.ShouldNotBeNullOrWhiteSpace();
            entry.SourceSeasonNumber.ShouldBeGreaterThan(0);
        }
    }

    private static async Task<GetAthleteProfileResponse> FindFeederProfileWithCupsAsync(
        SaveStore store, Guid saveId, GetAthleteProfileHandler handler)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<int> colorAthletes = await context.ColorCupTeamGroupStandings
            .AsNoTracking()
            .Select(e => e.SaveAthleteId)
            .Distinct()
            .ToListAsync().ConfigureAwait(false);
        List<int> typeAthletes = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Select(e => e.SaveAthleteId)
            .Distinct()
            .ToListAsync().ConfigureAwait(false);
        HashSet<int> cupAthletes = new(colorAthletes);
        cupAthletes.UnionWith(typeAthletes);
        cupAthletes.Count.ShouldBeGreaterThan(0);

        foreach (int athleteId in cupAthletes.OrderBy(id => id))
        {
            GetAthleteProfileResponse profile = await handler.HandleAsync(saveId, athleteId).ConfigureAwait(false);
            if (!profile.Career.IsActive || profile.CupHistory.Count == 0 || profile.CupSelections.Count == 0)
            {
                continue;
            }

            if (profile.Seasons.Any(s => s.WasActive && s.LeagueKey != null)
                && profile.Honours.Count > 0
                && profile.Movements.Count > 0)
            {
                return profile;
            }
        }

        throw new InvalidOperationException("Tiered template has no feeder athlete with Cups, honours and movements.");
    }

    private static async Task<GetAthleteProfileResponse> FindPoolProfileAsync(
        SaveStore store, Guid saveId, GetAthleteProfileHandler handler)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<int> poolIds = await context.AthleteCareers
            .AsNoTracking()
            .Where(e => !e.IsActive)
            .OrderBy(e => e.SaveAthleteId)
            .Select(e => e.SaveAthleteId)
            .Take(20)
            .ToListAsync().ConfigureAwait(false);
        poolIds.Count.ShouldBeGreaterThan(0);

        foreach (int athleteId in poolIds)
        {
            GetAthleteProfileResponse profile = await handler.HandleAsync(saveId, athleteId).ConfigureAwait(false);
            if (!profile.Career.IsActive && profile.Seasons.Any(s => !s.WasActive))
            {
                return profile;
            }
        }

        throw new InvalidOperationException("Tiered template has no pool athlete.");
    }
}
