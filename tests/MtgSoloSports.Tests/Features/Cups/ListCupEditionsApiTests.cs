using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Features.Cups.ListCupEditions;
using MtgSoloSports.Features.Cups.PlayColorCupTeamRound;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

[Collection(CupHistoryGroup.Name)]
public sealed class ListCupEditionsApiTests
{
    private readonly CupHistoryFixture _fixture;

    public ListCupEditionsApiTests(CupHistoryFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task List_ThreeSeasons_ListsEditionsNewestFirst_AllCompleted()
    {
        ListCupEditionsResponse response = await new ListCupEditionsHandler(_fixture.Store).HandleAsync(_fixture.SaveId);

        response.SaveId.ShouldBe(_fixture.SaveId);
        response.Editions.Select(e => $"{e.Cup}:{e.SourceSeasonNumber}:{e.State}")
            .ShouldBe(["Color:3:Completed", "Type:2:Completed", "Color:1:Completed"]);
        response.Editions.Where(e => string.Equals(e.Cup, "Color", StringComparison.Ordinal)).ShouldAllBe(e => e.TeamCount == 8 && e.IndividualChampion != null);
        response.Editions.Single(e => string.Equals(e.Cup, "Type", StringComparison.Ordinal)).IndividualChampion.ShouldBeNull();
    }

    [Fact]
    public async Task List_PodiumAndChampion_MatchPerEditionResults()
    {
        ListCupEditionsResponse response = await new ListCupEditionsHandler(_fixture.Store).HandleAsync(_fixture.SaveId);

        foreach (ListCupEditionsResponse.Edition edition in response.Editions.Where(e => string.Equals(e.Cup, "Color", StringComparison.Ordinal)))
        {
            GetColorCupTeamResultResponse team = await new GetColorCupTeamResultHandler(_fixture.Store)
                .HandleAsync(_fixture.SaveId, edition.SourceSeasonNumber);
            edition.Podium.Select(p => $"{p.TeamRank}:{p.TeamName}:{p.Medal}:{p.TeamScoreThousandths}")
                .ShouldBe(team.Teams.Where(t => t.TeamRank <= 3)
                    .Select(t => $"{t.TeamRank}:{t.TeamName}:{t.Medal}:{t.TeamScoreThousandths}"));
            edition.Podium.Select(p => p.Medal).ShouldBe(["Gold", "Silver", "Bronze"]);
            edition.Podium.ShouldAllBe(p => KeyNamesTeam(p.TeamKey, p.TeamName));

            GetColorCupIndividualResultResponse individual = await new GetColorCupIndividualResultHandler(_fixture.Store)
                .HandleAsync(_fixture.SaveId, edition.SourceSeasonNumber);
            edition.IndividualChampion!.AthleteId.ShouldBe(individual.ChampionAthleteId);
            edition.IndividualChampion.Name.ShouldBe(individual.ChampionName);
        }

        ListCupEditionsResponse.Edition type = response.Editions.Single(e => string.Equals(e.Cup, "Type", StringComparison.Ordinal));
        GetTypeCupTeamResultResponse typeTeam = await new GetTypeCupTeamResultHandler(_fixture.Store).HandleAsync(_fixture.SaveId, 2);
        type.TeamCount.ShouldBe(typeTeam.TeamCount);
        type.Podium.Select(p => $"{p.TeamRank}:{p.TeamKey}")
            .ShouldBe(typeTeam.Teams.Where(t => t.TeamRank <= 3).Select(t => $"{t.TeamRank}:{t.CreatureType}"));
    }

    private static bool KeyNamesTeam(string teamKey, string teamName) =>
        CupTeamKeys.TryParseColorKey(teamKey, out SportingColor color)
        && string.Equals(color.ToString(), teamName, StringComparison.Ordinal);

    [Fact]
    public async Task List_TeamTables_AggregateMedalsAcrossEditions()
    {
        ListCupEditionsResponse response = await new ListCupEditionsHandler(_fixture.Store).HandleAsync(_fixture.SaveId);

        response.ColorTeams.Count.ShouldBe(8);
        response.ColorTeams.ShouldAllBe(t => t.Editions == 2 && t.LastSeasonNumber == 3 && t.BestRank >= 1 && t.BestRank <= 8);
        response.ColorTeams.Sum(t => t.Gold).ShouldBe(2);
        response.ColorTeams.Sum(t => t.Silver).ShouldBe(2);
        response.ColorTeams.Sum(t => t.Bronze).ShouldBe(2);
        response.ColorTeams.Select(t => (t.Gold, t.Silver, t.Bronze))
            .ShouldBe(response.ColorTeams.Select(t => (t.Gold, t.Silver, t.Bronze)).OrderByDescending(m => m));

        GetTypeCupTeamResultResponse typeTeam = await new GetTypeCupTeamResultHandler(_fixture.Store).HandleAsync(_fixture.SaveId, 2);
        response.TypeTeams.Select(t => t.TeamKey).OrderBy(k => k, StringComparer.Ordinal)
            .ShouldBe(typeTeam.Teams.Select(t => t.CreatureType).OrderBy(k => k, StringComparer.Ordinal));
        response.TypeTeams.ShouldAllBe(t => t.Editions == 1 && t.LastSeasonNumber == 2);
    }

    [Fact]
    public async Task List_FreshSave_ReturnsEmptyLists()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("No Cups", 1UL, 2UL, UniverseTestCatalog.Build());
            ListCupEditionsResponse response = await new ListCupEditionsHandler(store).HandleAsync(created.Detail.SaveId);
            response.Editions.ShouldBeEmpty();
            response.ColorTeams.ShouldBeEmpty();
            response.TypeTeams.ShouldBeEmpty();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task List_TracksStateThroughOneEdition()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(8181UL, 9292UL);
        try
        {
            ListCupEditionsHandler handler = new(store);

            ListCupEditionsResponse.Edition selected = (await handler.HandleAsync(saveId)).Editions.Single();
            selected.State.ShouldBe("Selected");
            selected.TeamCount.ShouldBe(8);
            selected.Podium.ShouldBeEmpty();
            selected.IndividualChampion.ShouldBeNull();
            (await handler.HandleAsync(saveId)).ColorTeams.ShouldAllBe(t => t.Editions == 1 && t.BestRank == null && t.Gold == 0);

            PlayColorCupTeamRoundHandler step = new(store);
            for (int round = 0; round < 3; round++)
            {
                await step.HandleAsync(saveId);
            }

            ListCupEditionsResponse.Edition playing = (await handler.HandleAsync(saveId)).Editions.Single();
            playing.State.ShouldBe("InProgress");
            playing.Podium.ShouldBeEmpty();

            // Team event finished, individual event not played yet: podium known, edition still open.
            await new RunColorCupTeamHandler(store).HandleAsync(saveId);
            ListCupEditionsResponse.Edition teamDone = (await handler.HandleAsync(saveId)).Editions.Single();
            teamDone.State.ShouldBe("InProgress");
            teamDone.Podium.Count.ShouldBe(3);
            teamDone.IndividualChampion.ShouldBeNull();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task List_EmptySaveId_Throws()
    {
        await Should.ThrowAsync<ArgumentException>(() => new ListCupEditionsHandler(_fixture.Store).HandleAsync(Guid.Empty));
    }
}
