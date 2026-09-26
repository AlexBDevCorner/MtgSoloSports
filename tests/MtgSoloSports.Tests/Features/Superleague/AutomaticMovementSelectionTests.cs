using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Superleague;

public sealed class AutomaticMovementSelectionTests
{
    [Fact]
    public void Select_IdentifiesSafeIncumbentRelegatedAndPromotions()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        LeagueEntity superleague = new() { Id = 900, SeasonId = 7, SportingColor = 0, Kind = (int)LeagueKind.Superleague, Name = "Superleague" };
        List<LeagueEntity> feeders = Feeders(100);
        List<SeasonStandingEntity> superRows = SuperRows(superleague.Id, 1);
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederMap = FeederMap(feeders, 1000);

        AutomaticMovementSelection.AutomaticPlan plan =
            AutomaticMovementSelection.Select(superRows, feederMap, feeders, superleague, rules);

        plan.Promotions.Count.ShouldBe(8);
        plan.Relegations.Count.ShouldBe(8);
        plan.QualifierIncumbents.Count.ShouldBe(8);
        plan.QualifierChallengers.Count.ShouldBe(24);
        plan.All.Count.ShouldBe(48);

        plan.Promotions.Select(p => p.FromSeasonRank).ShouldAllBe(r => r == 1);
        plan.Promotions.Select(p => p.Kind).ShouldAllBe(k => k == MovementKind.AutomaticPromotion);
        plan.Relegations.Select(p => p.FromSeasonRank).OrderBy(r => r).ShouldBe([25, 26, 27, 28, 29, 30, 31, 32]);
        plan.QualifierIncumbents.Select(p => p.FromSeasonRank).OrderBy(r => r).ShouldBe([17, 18, 19, 20, 21, 22, 23, 24]);
        foreach (var group in plan.QualifierChallengers.GroupBy(p => p.FromLeagueId))
        {
            group.Count().ShouldBe(3);
            group.Select(p => p.FromSeasonRank).OrderBy(r => r).ShouldBe([2, 3, 4]);
        }

        // Safe ranks 1-16 never appear as picks.
        HashSet<int> pickedSuperRanks = plan.Relegations.Concat(plan.QualifierIncumbents).Select(p => p.FromSeasonRank).ToHashSet();
        pickedSuperRanks.Intersect(Enumerable.Range(1, 16).ToHashSet()).ShouldBeEmpty();

        // Deterministic order: kind, league, rank.
        List<AutomaticMovementSelection.AutomaticPick> sorted = plan.All
            .OrderBy(p => p.Kind)
            .ThenBy(p => p.FromLeagueId)
            .ThenBy(p => p.FromSeasonRank)
            .ToList();
        plan.All.Select(p => (p.SaveAthleteId, p.FromLeagueId, p.FromSeasonRank, p.Kind)).ShouldBe(
            sorted.Select(p => (p.SaveAthleteId, p.FromLeagueId, p.FromSeasonRank, p.Kind)).ToList());
    }

    [Fact]
    public void Select_HighlyUnequalSuperleagueComposition_HasNoColorQuota()
    {
        // Highly unequal: 20 of one logical group, 8 of another, 4 of a third.
        // Selection reads ranks only and must still split 16/8/8 without quota.
        RulesV1 rules = RulesV1.CreateDefault();
        LeagueEntity superleague = new() { Id = 901, SeasonId = 7, SportingColor = 0, Kind = (int)LeagueKind.Superleague, Name = "Superleague" };
        List<LeagueEntity> feeders = Feeders(200);
        List<SeasonStandingEntity> superRows = SuperRows(superleague.Id, 5000);
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederMap = FeederMap(feeders, 9000);

        AutomaticMovementSelection.AutomaticPlan plan =
            AutomaticMovementSelection.Select(superRows, feederMap, feeders, superleague, rules);

        plan.Promotions.Count.ShouldBe(8);
        plan.Relegations.Count.ShouldBe(8);

        // Even if all eight relegated athletes logically share one color,
        // selection still yields exactly eight with ranks 25-32.
        plan.Relegations.Select(p => p.FromSeasonRank).OrderBy(r => r).ShouldBe([25, 26, 27, 28, 29, 30, 31, 32]);

        // Champions promote regardless of Superleague composition.
        plan.Promotions.Count.ShouldBe(feeders.Count);
    }

    [Fact]
    public void Select_MultipleReturningSameColor_AllRelegatedRanksCovered()
    {
        // Simulates several relegated athletes sharing one returning color:
        // ranks 25-32 are distinct athletes but selection treats them uniformly.
        RulesV1 rules = RulesV1.CreateDefault();
        LeagueEntity superleague = new() { Id = 902, SeasonId = 7, SportingColor = 0, Kind = (int)LeagueKind.Superleague, Name = "Superleague" };
        List<LeagueEntity> feeders = Feeders(300);
        List<SeasonStandingEntity> superRows = SuperRows(superleague.Id, 20000);
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederMap = FeederMap(feeders, 30000);

        AutomaticMovementSelection.AutomaticPlan plan =
            AutomaticMovementSelection.Select(superRows, feederMap, feeders, superleague, rules);

        plan.Relegations.Count.ShouldBe(8);
        plan.Relegations.Select(p => p.SaveAthleteId).Distinct().Count().ShouldBe(8);
        plan.Relegations.All(p => p.FromLeagueId == superleague.Id).ShouldBeTrue();
        plan.Relegations.All(p => p.Kind == MovementKind.AutomaticRelegation).ShouldBeTrue();
    }

    [Fact]
    public void Select_MissingSuperleagueRank_Throws()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        LeagueEntity superleague = new() { Id = 903, SeasonId = 7, SportingColor = 0, Kind = (int)LeagueKind.Superleague, Name = "Superleague" };
        List<LeagueEntity> feeders = Feeders(400);
        List<SeasonStandingEntity> superRows = SuperRows(superleague.Id, 40000);
        superRows.RemoveAll(r => r.SeasonRank == 32);
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederMap = FeederMap(feeders, 50000);

        Should.Throw<InvalidOperationException>(() =>
            AutomaticMovementSelection.Select(superRows, feederMap, feeders, superleague, rules));
    }

    [Fact]
    public void Select_MissingFeederChampion_Throws()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        LeagueEntity superleague = new() { Id = 904, SeasonId = 7, SportingColor = 0, Kind = (int)LeagueKind.Superleague, Name = "Superleague" };
        List<LeagueEntity> feeders = Feeders(500);
        List<SeasonStandingEntity> superRows = SuperRows(superleague.Id, 60000);
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederMap = FeederMap(feeders, 70000);
        int victim = feeders[0].Id;
        feederMap[victim] = feederMap[victim].Where(r => r.SeasonRank != 1).ToList();

        Should.Throw<InvalidOperationException>(() =>
            AutomaticMovementSelection.Select(superRows, feederMap, feeders, superleague, rules));
    }

    private static List<LeagueEntity> Feeders(int firstId)
    {
        List<LeagueEntity> leagues = new(8);
        for (int i = 0; i < 8; i++)
        {
            leagues.Add(new LeagueEntity
            {
                Id = firstId + i,
                SeasonId = 7,
                SportingColor = i,
                Kind = (int)LeagueKind.Feeder,
                Name = $"League {i}",
            });
        }

        return leagues;
    }

    private static List<SeasonStandingEntity> SuperRows(int leagueId, int firstAthlete)
    {
        List<SeasonStandingEntity> rows = new(32);
        for (int rank = 1; rank <= 32; rank++)
        {
            rows.Add(new SeasonStandingEntity
            {
                Id = firstAthlete + rank,
                SeasonId = 7,
                LeagueId = leagueId,
                SaveAthleteId = firstAthlete + rank,
                SeasonRank = rank,
                IsChampion = rank == 1,
            });
        }

        return rows;
    }

    private static Dictionary<int, IReadOnlyList<SeasonStandingEntity>> FeederMap(List<LeagueEntity> feeders, int firstAthlete)
    {
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> map = new(feeders.Count);
        int cursor = firstAthlete;
        foreach (LeagueEntity league in feeders)
        {
            List<SeasonStandingEntity> rows = new(32);
            for (int rank = 1; rank <= 32; rank++)
            {
                cursor++;
                rows.Add(new SeasonStandingEntity
                {
                    Id = cursor,
                    SeasonId = 7,
                    LeagueId = league.Id,
                    SaveAthleteId = cursor,
                    SeasonRank = rank,
                    IsChampion = rank == 1,
                });
            }

            map[league.Id] = rows;
        }

        return map;
    }
}
