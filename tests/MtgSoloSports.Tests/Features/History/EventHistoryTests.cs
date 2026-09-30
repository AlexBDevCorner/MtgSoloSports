using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.PlayColorCupTeamRound;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.History.GetEventRound;
using MtgSoloSports.Features.History.GetEventTeamStandings;
using MtgSoloSports.Features.History.ListEventRounds;
using MtgSoloSports.Features.History.ListEvents;
using MtgSoloSports.Features.Superleague.PlayQualifierRound;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.History;

public sealed class EventHistoryTests
{
    [Fact]
    public async Task ListEvents_MidColorCupTeam_ListsIndividualCompleteAndTeamInProgress()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(4545UL, 5656UL);
        try
        {
            await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            await PlayTeamRoundsAsync(store, saveId, 10);

            ListHistoryEventsResponse events = await new ListHistoryEventsHandler(store).HandleAsync(saveId, 1);
            events.Events.Select(e => $"{e.Event}:{e.RoundsPlayed}/{e.TotalRounds}:{e.IsComplete}")
                .ShouldBe(["color-cup-individual:16/16:True", "color-cup-team:10/32:False"]);
            events.Events[1].GroupCount.ShouldBe(4);
            events.Events[1].RoundsPerGroup.ShouldBe(8);
            events.Events[1].Title.ShouldBe("Color Cup — team");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ListEventRounds_ReturnsGroupAndRoundInOrder()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(4646UL, 5757UL);
        try
        {
            await PlayTeamRoundsAsync(store, saveId, 10);
            ListHistoryEventRoundsResponse rounds = await new ListHistoryEventRoundsHandler(store).HandleAsync(saveId, 1, "color-cup-team");
            rounds.Rounds.Select(r => $"{r.Group}.{r.Round}")
                .ShouldBe(["1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7", "1.8", "2.1", "2.2"]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task GetEventRound_MatchesStoredPayload()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(4747UL, 5858UL);
        try
        {
            await PlayTeamRoundsAsync(store, saveId, 10);
            EventRoundView view = await new GetHistoryEventRoundHandler(store).HandleAsync(saveId, 1, "color-cup-team", 2, 2);
            view.Group.ShouldBe(2);
            view.RoundNumber.ShouldBe(2);
            view.Event.ShouldBe("color-cup-team");
            using SaveDbContext context = store.OpenDbContext(saveId);
            ColorCupTeamRoundEntity row = await context.ColorCupTeamRounds.SingleAsync(e => e.GroupNumber == 2 && e.RoundNumber == 2);
            ColorCupTeamRoundPayloadDocument stored = ColorCupTeamRoundPayloadDocument.FromStored(row.PayloadJson);
            view.PayloadChecksum.ShouldBe(row.PayloadChecksum);
            view.Placements.Select(p => $"{p.AthleteId}:{p.Position}:{p.FinalThousandths}")
                .ShouldBe(stored.Placements.OrderBy(p => p.Position).Select(p => $"{p.AthleteId}:{p.Position}:{p.FinalThousandths}"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task GetEventRound_ValidatesGroupAndRound()
    {
        var (cupStore, cupRoot, cupId) = await PrepareColorCupAsync(4848UL, 5959UL);
        var (qualStore, qualRoot, qualId) = await PrepareQualifierAsync(4949UL, 6060UL);
        try
        {
            await PlayTeamRoundsAsync(cupStore, cupId, 1);
            PlayQualifierRoundResponse qualifier = await new PlayQualifierRoundHandler(qualStore).HandleAsync(qualId);
            int qualifierSeason = qualifier.Round.SeasonNumber;

            GetHistoryEventRoundHandler cupRounds = new(cupStore);
            await Should.ThrowAsync<ArgumentException>(() => cupRounds.HandleAsync(cupId, 1, "color-cup-team", 1, null));
            await Should.ThrowAsync<ArgumentException>(() => cupRounds.HandleAsync(cupId, 1, "color-cup-team", 1, 5));
            await Should.ThrowAsync<HistoryNotFoundException>(() => cupRounds.HandleAsync(cupId, 1, "color-cup-team", 2, 1));
            await Should.ThrowAsync<ArgumentException>(() => cupRounds.HandleAsync(cupId, 1, "league", 1, null));

            GetHistoryEventRoundHandler qualRounds = new(qualStore);
            await Should.ThrowAsync<ArgumentException>(() => qualRounds.HandleAsync(qualId, qualifierSeason, "qualifier", 1, 1));
            EventRoundView first = await qualRounds.HandleAsync(qualId, qualifierSeason, "qualifier", 1, null);
            first.Group.ShouldBeNull();
            first.Placements.Count.ShouldBe(32);
        }
        finally
        {
            DeleteRoot(cupRoot);
            DeleteRoot(qualRoot);
        }
    }

    [Fact]
    public async Task TeamStandings_ProvisionalThenFinal()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(5050UL, 6161UL);
        try
        {
            await PlayTeamRoundsAsync(store, saveId, 8);
            GetHistoryEventTeamStandingsHandler standings = new(store);
            HistoryEventTeamStandingsResponse provisional = await standings.HandleAsync(saveId, 1, "color-cup-team");
            provisional.IsFinal.ShouldBeFalse();
            provisional.GroupsCompleted.ShouldBe(1);
            provisional.Teams.Count.ShouldBe(8);
            provisional.Teams.ShouldAllBe(t => t.Rank == null);
            provisional.Teams.Select(t => t.ScoreThousandths).ShouldBe(provisional.Teams.Select(t => t.ScoreThousandths).OrderByDescending(v => v));

            await new RunColorCupTeamHandler(store).HandleAsync(saveId);
            HistoryEventTeamStandingsResponse final = await standings.HandleAsync(saveId, 1, "color-cup-team");
            final.IsFinal.ShouldBeTrue();
            final.GroupsCompleted.ShouldBe(4);
            final.Teams.Select(t => t.Rank).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8]);
            await Should.ThrowAsync<ArgumentException>(() => standings.HandleAsync(saveId, 1, "color-cup-individual"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task PlayTeamRoundsAsync(SaveStore store, Guid saveId, int count)
    {
        PlayColorCupTeamRoundHandler step = new(store);
        for (int round = 0; round < count; round++)
        {
            await step.HandleAsync(saveId).ConfigureAwait(false);
        }
    }
}
