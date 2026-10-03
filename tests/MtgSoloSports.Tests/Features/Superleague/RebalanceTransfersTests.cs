using MtgSoloSports.Features.Superleague.GetRebalanceResult;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Superleague;

public sealed class RebalanceTransfersTests
{
    [Fact]
    public void Map_DeparturesAndReturns_StayInOwnLeagueContext()
    {
        LeagueEntity whiteSource = Feeder(1, SportingColor.White, "White League");
        LeagueEntity blueSource = Feeder(2, SportingColor.Blue, "Blue League");
        LeagueEntity superSource = Super(3);
        LeagueEntity whiteNext = Feeder(11, SportingColor.White, "White League");
        LeagueEntity blueNext = Feeder(12, SportingColor.Blue, "Blue League");
        LeagueEntity superNext = Super(19, "Superleague");

        List<SeasonMembershipEntity> source = [
            Member(101, whiteSource.Id, SportingColor.White),
            Member(102, whiteSource.Id, SportingColor.White),
            Member(103, blueSource.Id, SportingColor.Blue),
            Member(201, superSource.Id, SportingColor.White),
            Member(202, superSource.Id, SportingColor.Blue),
            Member(301, null, SportingColor.White),
        ];
        List<SeasonMembershipEntity> next = [
            Member(101, superNext.Id, SportingColor.White),
            Member(102, whiteNext.Id, SportingColor.White),
            Member(103, blueNext.Id, SportingColor.Blue),
            Member(201, whiteNext.Id, SportingColor.White),
            Member(202, blueNext.Id, SportingColor.Blue),
            Member(301, null, SportingColor.White),
        ];
        Dictionary<int, int> ranks = new()
        {
            [101] = 1,
            [102] = 5,
            [103] = 2,
            [201] = 25,
            [202] = 26,
        };
        Dictionary<int, string> names = source.ToDictionary(m => m.SaveAthleteId, m => $"Card {m.SaveAthleteId}");
        Dictionary<int, string?> images = source.ToDictionary(m => m.SaveAthleteId, _ => (string?)null);
        Dictionary<int, LeagueEntity> leagues = new List<LeagueEntity>
            { whiteSource, blueSource, superSource, whiteNext, blueNext, superNext }.ToDictionary(l => l.Id);

        (IReadOnlyList<RebalanceMovementMember> departed, IReadOnlyList<RebalanceMovementMember> returned) =
            RebalanceSuperleagueTransfers.Map(
                [whiteSource, blueSource], superSource, [whiteNext, blueNext], superNext,
                source, next, ranks, names, images, leagues);

        departed.Select(m => m.AthleteId).ShouldBe([101]);
        departed.Single().Kind.ShouldBe(RebalanceSuperleagueTransfers.DepartureKind);
        departed.Single().FromLeagueName.ShouldBe("White League");
        departed.Single().ToLeagueName.ShouldBe("Superleague");
        departed.Single().FromSeasonRank.ShouldBe(1);

        returned.Select(m => m.AthleteId).OrderBy(id => id).ShouldBe([201, 202]);
        foreach (RebalanceMovementMember member in returned)
        {
            member.Kind.ShouldBe(RebalanceSuperleagueTransfers.ReturnKind);
        }

        // Challenger loser (103) and retained incumbent path never count as transfers.
        departed.Select(m => m.AthleteId).ShouldNotContain(103);
    }

    [Fact]
    public void Map_Inaugural_HasDeparturesOnly()
    {
        LeagueEntity whiteSource = Feeder(1, SportingColor.White, "White League");
        LeagueEntity whiteNext = Feeder(11, SportingColor.White, "White League");
        LeagueEntity superNext = Super(19, "Superleague");
        List<SeasonMembershipEntity> source = [Member(101, whiteSource.Id, SportingColor.White)];
        List<SeasonMembershipEntity> next = [Member(101, superNext.Id, SportingColor.White)];
        Dictionary<int, int> ranks = new() { [101] = 4 };
        Dictionary<int, string> names = new() { [101] = "White Four" };
        Dictionary<int, string?> images = new() { [101] = "https://img.test/w.jpg" };
        Dictionary<int, LeagueEntity> leagues = new List<LeagueEntity> { whiteSource, whiteNext, superNext }.ToDictionary(l => l.Id);

        (IReadOnlyList<RebalanceMovementMember> departed, IReadOnlyList<RebalanceMovementMember> returned) =
            RebalanceSuperleagueTransfers.Map(
                [whiteSource], null, [whiteNext], superNext, source, next, ranks, names, images, leagues);

        departed.Count.ShouldBe(1);
        returned.Count.ShouldBe(0);
        departed.Single().ImageUrl.ShouldBe("https://img.test/w.jpg");
    }

    [Fact]
    public void Map_UnchangedLeague_ProducesNoTransfers()
    {
        LeagueEntity whiteSource = Feeder(1, SportingColor.White, "White League");
        LeagueEntity whiteNext = Feeder(11, SportingColor.White, "White League");
        LeagueEntity superSource = Super(3);
        LeagueEntity superNext = Super(19, "Superleague");
        List<SeasonMembershipEntity> source = [
            Member(101, whiteSource.Id, SportingColor.White),
            Member(201, superSource.Id, SportingColor.White),
        ];
        List<SeasonMembershipEntity> next = [
            Member(101, whiteNext.Id, SportingColor.White),
            Member(201, superNext.Id, SportingColor.White),
        ];
        Dictionary<int, int> ranks = new() { [101] = 10, [201] = 5 };
        Dictionary<int, string> names = new() { [101] = "A", [201] = "B" };
        Dictionary<int, string?> images = new() { [101] = null, [201] = null };
        Dictionary<int, LeagueEntity> leagues = new List<LeagueEntity> { whiteSource, superSource, whiteNext, superNext }.ToDictionary(l => l.Id);

        (IReadOnlyList<RebalanceMovementMember> departed, IReadOnlyList<RebalanceMovementMember> returned) =
            RebalanceSuperleagueTransfers.Map(
                [whiteSource], superSource, [whiteNext], superNext, source, next, ranks, names, images, leagues);

        departed.Count.ShouldBe(0);
        returned.Count.ShouldBe(0);
    }

    [Fact]
    public void CountsByColor_GroupsMultipleLeaguesWithoutMixing()
    {
        List<RebalanceMovementMember> departed =
        [
            Transfer(1, "White", RebalanceSuperleagueTransfers.DepartureKind),
            Transfer(2, "White", RebalanceSuperleagueTransfers.DepartureKind),
            Transfer(3, "Blue", RebalanceSuperleagueTransfers.DepartureKind),
        ];
        List<RebalanceMovementMember> returned =
        [
            Transfer(4, "Blue", RebalanceSuperleagueTransfers.ReturnKind),
        ];
        IReadOnlyDictionary<string, (int Departed, int Returned)> counts =
            RebalanceSuperleagueTransfers.CountsByColor(departed, returned);
        counts["White"].ShouldBe((2, 0));
        counts["Blue"].ShouldBe((1, 1));
    }

    [Fact]
    public void ProvisionalMath_ExactlyBalanced_NeedsNoPoolAdjustment()
    {
        List<RebalanceMovementMember> departed = [Transfer(1, "White", RebalanceSuperleagueTransfers.DepartureKind)];
        List<RebalanceMovementMember> returned = [Transfer(2, "White", RebalanceSuperleagueTransfers.ReturnKind)];
        IReadOnlyDictionary<string, (int Departed, int Returned)> counts =
            RebalanceSuperleagueTransfers.CountsByColor(departed, returned);

        // Exactly-balanced: one departure, one return -> provisional stays 32.
        counts["White"].ShouldBe((1, 1));
        int provisional = 32 - counts["White"].Departed + counts["White"].Returned;
        provisional.ShouldBe(32);

        // Unchanged league has no transfers at all.
        counts.ContainsKey("Blue").ShouldBeFalse();
    }

    [Fact]
    public void ProvisionalMath_UnderfilledAndOverfilled_DrivePoolStory()
    {
        List<RebalanceMovementMember> departed =
        [
            Transfer(1, "White", RebalanceSuperleagueTransfers.DepartureKind),
            Transfer(2, "White", RebalanceSuperleagueTransfers.DepartureKind),
            Transfer(3, "Blue", RebalanceSuperleagueTransfers.DepartureKind),
        ];
        List<RebalanceMovementMember> returned =
        [
            Transfer(4, "White", RebalanceSuperleagueTransfers.ReturnKind),
            Transfer(5, "Blue", RebalanceSuperleagueTransfers.ReturnKind),
            Transfer(6, "Blue", RebalanceSuperleagueTransfers.ReturnKind),
            Transfer(7, "Blue", RebalanceSuperleagueTransfers.ReturnKind),
        ];
        IReadOnlyDictionary<string, (int Departed, int Returned)> counts =
            RebalanceSuperleagueTransfers.CountsByColor(departed, returned);

        // White: 32 - 2 + 1 = 31 underfilled -> needs one pool draw.
        int whiteProvisional = 32 - counts["White"].Departed + counts["White"].Returned;
        whiteProvisional.ShouldBe(31);

        // Blue: 32 - 1 + 3 = 34 overfilled -> must displace two lowest-ranked.
        int blueProvisional = 32 - counts["Blue"].Departed + counts["Blue"].Returned;
        blueProvisional.ShouldBe(34);
    }

    private static LeagueEntity Feeder(int id, SportingColor color, string name)
    {
        return new LeagueEntity { Id = id, SeasonId = 1, SportingColor = (int)color, Kind = (int)LeagueKind.Feeder, Name = name };
    }

    private static LeagueEntity Super(int id, string name = "Superleague")
    {
        return new LeagueEntity { Id = id, SeasonId = 1, SportingColor = (int)SportingColor.White, Kind = (int)LeagueKind.Superleague, Name = name };
    }

    private static SeasonMembershipEntity Member(int athleteId, int? leagueId, SportingColor color)
    {
        return new SeasonMembershipEntity { SeasonId = 1, LeagueId = leagueId, SaveAthleteId = athleteId, SportingColor = (int)color, DrawIndex = 0 };
    }

    private static RebalanceMovementMember Transfer(int athleteId, string color, string kind)
    {
        return new RebalanceMovementMember(
            athleteId, $"Card {athleteId}", color, 1, "From", 2, "To", kind, 1, null);
    }
}
