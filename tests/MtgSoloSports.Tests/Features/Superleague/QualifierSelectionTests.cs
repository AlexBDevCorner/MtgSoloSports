using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Superleague;

public sealed class QualifierSelectionTests
{
    [Fact]
    public void Select_BuildsExactly32_FromBands()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        LeagueEntity superleague = BuildSuperleague();
        List<LeagueEntity> feeders = BuildFeeders();
        List<SeasonStandingEntity> superStandings = BuildSuperStandings(superleague);
        (Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederMap, Dictionary<int, SeasonMembershipEntity> memberships, Dictionary<int, string> names) =
            BuildFeederData(feeders, superStandings, superleague);

        QualifierFieldSelection.QualifierField field = QualifierFieldSelection.Select(
            superStandings, feederMap, feeders, superleague, memberships, names, rules);

        AssertField(field);
        QualifierInvariants.ValidateField(field, superStandings, feederMap, feeders, superleague, rules);
    }

    [Fact]
    public void Select_WrongSuperleagueSize_Throws()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        LeagueEntity superleague = new() { Id = 1, SeasonId = 1, Kind = (int)LeagueKind.Superleague, Name = "Superleague", SportingColor = 0 };
        List<LeagueEntity> feeders = [];
        for (int i = 0; i < 8; i++)
        {
            feeders.Add(new LeagueEntity { Id = 10 + i, SeasonId = 1, Kind = (int)LeagueKind.Feeder, Name = $"F{i}", SportingColor = i });
        }

        List<SeasonStandingEntity> superStandings = [];
        for (int rank = 1; rank <= 31; rank++)
        {
            superStandings.Add(BuildStanding(rank, 1, 1, rank));
        }

        Should.Throw<InvalidOperationException>(() => QualifierFieldSelection.Select(
            superStandings, new Dictionary<int, IReadOnlyList<SeasonStandingEntity>>(), feeders, superleague,
            new Dictionary<int, SeasonMembershipEntity>(), new Dictionary<int, string>(), rules));
    }

    private static LeagueEntity BuildSuperleague()
    {
        return new() { Id = 900, SeasonId = 1, Kind = (int)LeagueKind.Superleague, Name = "Superleague", SportingColor = 0 };
    }

    private static List<LeagueEntity> BuildFeeders()
    {
        List<LeagueEntity> feeders = [];
        for (int i = 0; i < 8; i++)
        {
            feeders.Add(new LeagueEntity { Id = 100 + i, SeasonId = 1, Kind = (int)LeagueKind.Feeder, Name = $"Feeder {i}", SportingColor = i });
        }

        return feeders;
    }

    private static List<SeasonStandingEntity> BuildSuperStandings(LeagueEntity superleague)
    {
        List<SeasonStandingEntity> standings = [];
        for (int rank = 1; rank <= 32; rank++)
        {
            standings.Add(BuildStanding(rank, 1, superleague.Id, 1000 + rank, rank));
        }

        return standings;
    }

    private static SeasonStandingEntity BuildStanding(int id, int seasonId, int leagueId, int athleteId)
    {
        return BuildStanding(id, seasonId, leagueId, athleteId, id);
    }

    private static SeasonStandingEntity BuildStanding(int id, int seasonId, int leagueId, int athleteId, int rank)
    {
        return new SeasonStandingEntity
        {
            Id = id,
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

    private static (Dictionary<int, IReadOnlyList<SeasonStandingEntity>> Map, Dictionary<int, SeasonMembershipEntity> Memberships, Dictionary<int, string> Names) BuildFeederData(
        List<LeagueEntity> feeders,
        List<SeasonStandingEntity> superStandings,
        LeagueEntity superleague)
    {
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederMap = new();
        Dictionary<int, SeasonMembershipEntity> memberships = new();
        Dictionary<int, string> names = new();
        foreach (LeagueEntity feeder in feeders)
        {
            List<SeasonStandingEntity> rows = BuildFeederRows(feeder, memberships, names);
            feederMap[feeder.Id] = rows;
        }

        foreach (SeasonStandingEntity row in superStandings)
        {
            memberships[row.SaveAthleteId] = new SeasonMembershipEntity
            {
                Id = row.SaveAthleteId,
                SeasonId = 1,
                LeagueId = superleague.Id,
                SaveAthleteId = row.SaveAthleteId,
                SportingColor = 0,
                DrawIndex = 0,
            };
            names[row.SaveAthleteId] = $"Athlete {row.SaveAthleteId}";
        }

        return (feederMap, memberships, names);
    }

    private static List<SeasonStandingEntity> BuildFeederRows(
        LeagueEntity feeder,
        Dictionary<int, SeasonMembershipEntity> memberships,
        Dictionary<int, string> names)
    {
        List<SeasonStandingEntity> rows = [];
        for (int rank = 1; rank <= 32; rank++)
        {
            int athleteId = feeder.Id * 1000 + rank;
            rows.Add(BuildStanding(athleteId, 1, feeder.Id, athleteId, rank));
            memberships[athleteId] = new SeasonMembershipEntity
            {
                Id = athleteId,
                SeasonId = 1,
                LeagueId = feeder.Id,
                SaveAthleteId = athleteId,
                SportingColor = feeder.SportingColor,
                DrawIndex = rank,
            };
            names[athleteId] = $"Athlete {athleteId}";
        }

        return rows;
    }

    private static void AssertField(QualifierFieldSelection.QualifierField field)
    {
        field.Incumbents.Count.ShouldBe(8);
        field.Challengers.Count.ShouldBe(24);
        field.All.Count.ShouldBe(32);
        field.Incumbents.Select(p => p.FromSeasonRank).OrderBy(r => r).ShouldBe(Enumerable.Range(17, 8).ToList());
        foreach (var group in field.Challengers.GroupBy(p => p.FromLeagueId))
        {
            group.Count().ShouldBe(3);
            group.Select(p => p.FromSeasonRank).OrderBy(r => r).ShouldBe(new[] { 2, 3, 4 });
        }
    }
}
