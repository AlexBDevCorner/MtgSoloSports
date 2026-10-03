using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.GetColorCupTeamHistory;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.PlayColorCupTeamRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

[Collection(CupHistoryGroup.Name)]
public sealed class ColorCupTeamHistoryApiTests
{
    private readonly CupHistoryFixture _fixture;

    public ColorCupTeamHistoryApiTests(CupHistoryFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task History_EveryColor_MatchesStoredSelectionsAndResults()
    {
        GetColorCupTeamHistoryHandler handler = new(_fixture.Store);

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            CupTeamHistoryResponse history = await handler.HandleAsync(_fixture.SaveId, CupTeamKeys.ColorKey(color));

            history.Cup.ShouldBe("Color");
            history.TeamKey.ShouldBe(CupTeamKeys.ColorKey(color));
            history.TeamName.ShouldBe(color.ToString());
            history.Seasons.Select(s => s.SourceSeasonNumber).ShouldBe([3, 1]);
            history.Seasons.ShouldAllBe(s => string.Equals(s.State, "Completed", StringComparison.Ordinal) && s.TeamCount == 8);
            history.Honours.Editions.ShouldBe(2);

            foreach (CupTeamHistoryResponse.Season season in history.Seasons)
            {
                await AssertSquadMatchesSelectionAsync(color, season);
                await AssertTeamResultAsync(color, season);
                await AssertIndividualResultsAsync(season);
            }

            history.Honours.TotalScoreThousandths.ShouldBe(history.Seasons.Sum(s => (long)s.TeamScoreThousandths!.Value));
            history.Honours.GroupWins.ShouldBe(history.Seasons.Sum(s => s.GroupWins!.Value));
            history.Honours.BestRank.ShouldBe(history.Seasons.Min(s => s.TeamRank));
            history.Roster.Sum(r => r.Caps).ShouldBe(8);
            history.Roster.Select(r => r.Caps).ShouldBe(history.Roster.Select(r => r.Caps).OrderByDescending(c => c));
        }
    }

    private async Task AssertSquadMatchesSelectionAsync(SportingColor color, CupTeamHistoryResponse.Season season)
    {
        using SaveDbContext context = _fixture.Store.OpenDbContext(_fixture.SaveId);
        List<ColorCupSelectionEntity> stored = await context.ColorCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonNumber == season.SourceSeasonNumber && e.SportingColor == (int)color)
            .OrderBy(e => e.SelectionRank)
            .ToListAsync().ConfigureAwait(false);
        season.Squad.Select(m => $"{m.AthleteId}:{m.SelectionRank}:{m.FinalRatingThousandths}")
            .ShouldBe(stored.Select(e => $"{e.SaveAthleteId}:{e.SelectionRank}:{e.FinalRatingThousandths}"));
        season.Squad.ShouldAllBe(m => m.Reason == null);
    }

    private async Task AssertTeamResultAsync(SportingColor color, CupTeamHistoryResponse.Season season)
    {
        GetColorCupTeamResultResponse team = await new GetColorCupTeamResultHandler(_fixture.Store)
            .HandleAsync(_fixture.SaveId, season.SourceSeasonNumber).ConfigureAwait(false);
        GetColorCupTeamMember result = team.Teams.Single(t => t.SportingColor == (int)color);
        season.TeamRank.ShouldBe(result.TeamRank);
        season.Medal.ShouldBe(result.Medal);
        season.TeamScoreThousandths.ShouldBe(result.TeamScoreThousandths);
        season.GroupWins.ShouldBe(result.GroupWins);
        season.RoundWins.ShouldBe(result.RoundWins);
        foreach (CupTeamHistoryResponse.SquadMember member in season.Squad)
        {
            GetColorCupTeamLegMember leg = team.Legs.Single(l => l.AthleteId == member.AthleteId);
            member.Leg.ShouldNotBeNull();
            member.Leg.GroupNumber.ShouldBe(member.SelectionRank);
            member.Leg.GroupRank.ShouldBe(leg.GroupRank);
            member.Leg.GroupScoreThousandths.ShouldBe(leg.GroupScoreThousandths);
            member.Leg.GroupSize.ShouldBe(8);
        }
    }

    private async Task AssertIndividualResultsAsync(CupTeamHistoryResponse.Season season)
    {
        GetColorCupIndividualResultResponse individual = await new GetColorCupIndividualResultHandler(_fixture.Store)
            .HandleAsync(_fixture.SaveId, season.SourceSeasonNumber).ConfigureAwait(false);
        foreach (CupTeamHistoryResponse.SquadMember member in season.Squad)
        {
            GetColorCupIndividualMember standing = individual.Standings.Single(s => s.AthleteId == member.AthleteId);
            member.Individual.ShouldNotBeNull();
            member.Individual.CupRank.ShouldBe(standing.CupRank);
            member.Individual.Medal.ShouldBe(standing.Medal);
        }
    }

    [Fact]
    public async Task History_IndividualMedals_CoverEveryMedalOfTheCup()
    {
        GetColorCupTeamHistoryHandler handler = new(_fixture.Store);
        List<string> medals = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            CupTeamHistoryResponse history = await handler.HandleAsync(_fixture.SaveId, CupTeamKeys.ColorKey(color));
            medals.AddRange(history.IndividualMedals.Select(m => $"{m.SourceSeasonNumber}:{m.Medal}"));
        }

        medals.OrderBy(m => m, StringComparer.Ordinal)
            .ShouldBe(["1:Bronze", "1:Gold", "1:Silver", "3:Bronze", "3:Gold", "3:Silver"]);
    }

    [Fact]
    public async Task History_KeyIsCaseInsensitive_ButNeverNumeric()
    {
        GetColorCupTeamHistoryHandler handler = new(_fixture.Store);

        (await handler.HandleAsync(_fixture.SaveId, "RED")).TeamKey.ShouldBe("red");
        await Should.ThrowAsync<CupTeamHistoryNotFoundException>(() => handler.HandleAsync(_fixture.SaveId, "3"));
        await Should.ThrowAsync<CupTeamHistoryNotFoundException>(() => handler.HandleAsync(_fixture.SaveId, "purple"));
        await Should.ThrowAsync<ArgumentException>(() => handler.HandleAsync(Guid.Empty, "red"));
    }

    [Fact]
    public async Task History_BeforeAndDuringTheFirstCup_ShowsSquadWithoutResult()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(5151UL, 6262UL);
        try
        {
            GetColorCupTeamHistoryHandler handler = new(store);

            CupTeamHistoryResponse selected = await handler.HandleAsync(saveId, "white");
            selected.Seasons.Single().State.ShouldBe("Selected");
            selected.Seasons.Single().TeamRank.ShouldBeNull();
            selected.Seasons.Single().Squad.Count.ShouldBe(4);
            selected.Seasons.Single().Squad.ShouldAllBe(m => m.Leg == null && m.Individual == null);
            selected.Honours.Editions.ShouldBe(1);
            selected.Honours.BestRank.ShouldBeNull();

            PlayColorCupTeamRoundHandler step = new(store);
            for (int round = 0; round < 3; round++)
            {
                await step.HandleAsync(saveId);
            }

            CupTeamHistoryResponse playing = await handler.HandleAsync(saveId, "white");
            playing.Seasons.Single().State.ShouldBe("InProgress");
            playing.Seasons.Single().TeamRank.ShouldBeNull();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task History_FreshSave_IsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("No Cups", 3UL, 4UL, MtgSoloSports.Tests.Features.Universe.UniverseTestCatalog.Build());
            await Should.ThrowAsync<CupTeamHistoryNotFoundException>(
                () => new GetColorCupTeamHistoryHandler(store).HandleAsync(created.Detail.SaveId, "red"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
