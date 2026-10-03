using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.GetTypeCupTeamHistory;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class TypeCupTeamHistoryApiTests
{
    [Fact]
    public async Task History_TeamInBothEditions_MatchesStoredRowsAndAggregates()
    {
        var (store, root, saveId) = await PrepareTwoTypeCupsAsync(4141UL, 5252UL);
        try
        {
            CupTeamHistoryResponse history = await new GetTypeCupTeamHistoryHandler(store).HandleAsync(saveId, "Elf");

            history.Cup.ShouldBe("Type");
            history.TeamKey.ShouldBe("Elf");
            history.TeamName.ShouldBe("Elf");
            history.Seasons.Select(s => $"{s.SourceSeasonNumber}:{s.State}:{s.TeamCount}")
                .ShouldBe(["4:Completed:3", "2:Completed:2"]);
            history.IndividualMedals.ShouldBeEmpty();
            history.Seasons.SelectMany(s => s.Squad).ShouldAllBe(m => m.Individual == null);

            foreach (CupTeamHistoryResponse.Season season in history.Seasons)
            {
                await AssertSeasonMatchesStoredRowsAsync(store, saveId, season);
            }

            history.Seasons[1].Squad.ShouldAllBe(m => string.Equals(m.Reason, "OnlyType", StringComparison.Ordinal));
            history.Seasons[0].Squad.ShouldAllBe(m => string.Equals(m.Reason, "Capped", StringComparison.Ordinal));
            history.Honours.Editions.ShouldBe(2);
            history.Honours.TotalScoreThousandths.ShouldBe(history.Seasons.Sum(s => (long)s.TeamScoreThousandths!.Value));
            history.Honours.BestRank.ShouldBe(history.Seasons.Min(s => s.TeamRank));
            history.Roster.Count.ShouldBe(4);
            history.Roster.ShouldAllBe(r => r.Caps == 2 && r.FirstSeasonNumber == 2 && r.LastSeasonNumber == 4);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertSeasonMatchesStoredRowsAsync(SaveStore store, Guid saveId, CupTeamHistoryResponse.Season season)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<TypeCupSelectionEntity> seasonRows = await context.TypeCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonNumber == season.SourceSeasonNumber)
            .OrderBy(e => e.SelectionRank)
            .ToListAsync().ConfigureAwait(false);
        List<TypeCupSelectionEntity> stored = seasonRows
            .Where(e => string.Equals(e.CreatureType, "Elf", StringComparison.Ordinal))
            .ToList();
        season.Squad.Select(m => $"{m.AthleteId}:{m.SelectionRank}:{m.FinalRatingThousandths}")
            .ShouldBe(stored.Select(e => $"{e.SaveAthleteId}:{e.SelectionRank}:{e.FinalRatingThousandths}"));

        GetTypeCupTeamResultResponse team = await new GetTypeCupTeamResultHandler(store)
            .HandleAsync(saveId, season.SourceSeasonNumber).ConfigureAwait(false);
        GetTypeCupTeamMember result = team.Teams.Single(t => string.Equals(t.CreatureType, "Elf", StringComparison.Ordinal));
        season.TeamRank.ShouldBe(result.TeamRank);
        season.Medal.ShouldBe(result.Medal);
        season.TeamScoreThousandths.ShouldBe(result.TeamScoreThousandths);
        foreach (CupTeamHistoryResponse.SquadMember member in season.Squad)
        {
            GetTypeCupTeamLegMember leg = team.Legs.Single(l => l.AthleteId == member.AthleteId);
            member.Leg.ShouldNotBeNull();
            member.Leg.GroupRank.ShouldBe(leg.GroupRank);
            member.Leg.GroupScoreThousandths.ShouldBe(leg.GroupScoreThousandths);
            member.Leg.GroupSize.ShouldBe(season.TeamCount);
        }
    }

    [Fact]
    public async Task History_TeamWithOneAppearance_HasOneSeason()
    {
        var (store, root, saveId) = await PrepareTwoTypeCupsAsync(4242UL, 5353UL);
        try
        {
            CupTeamHistoryResponse history = await new GetTypeCupTeamHistoryHandler(store).HandleAsync(saveId, "Goblin");
            history.Seasons.Select(s => s.SourceSeasonNumber).ShouldBe([4]);
            history.Honours.Editions.ShouldBe(1);
            history.Roster.ShouldAllBe(r => r.Caps == 1);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task History_UnknownOrDifferentlyCasedType_IsNotFound()
    {
        var (store, root, saveId) = await PrepareTwoTypeCupsAsync(4343UL, 5454UL);
        try
        {
            GetTypeCupTeamHistoryHandler handler = new(store);
            await Should.ThrowAsync<CupTeamHistoryNotFoundException>(() => handler.HandleAsync(saveId, "Orc"));
            await Should.ThrowAsync<CupTeamHistoryNotFoundException>(() => handler.HandleAsync(saveId, "elf"));
            await Should.ThrowAsync<CupTeamHistoryNotFoundException>(() => handler.HandleAsync(saveId, " "));
            await Should.ThrowAsync<ArgumentException>(() => handler.HandleAsync(Guid.Empty, "Elf"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task History_WithoutStoredSelectionReports_StillLoadsWithoutReasons()
    {
        var (store, root, saveId) = await PrepareTwoTypeCupsAsync(4444UL, 5555UL);
        try
        {
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                context.CupSelectionReports.RemoveRange(await context.CupSelectionReports.ToListAsync());
                await context.SaveChangesAsync();
            }

            CupTeamHistoryResponse history = await new GetTypeCupTeamHistoryHandler(store).HandleAsync(saveId, "Dwarf");
            history.Seasons.Count.ShouldBe(2);
            history.Seasons.SelectMany(s => s.Squad).Count().ShouldBe(8);
            history.Seasons.SelectMany(s => s.Squad).ShouldAllBe(m => m.Reason == null && m.FinalRatingThousandths >= 0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task History_SelectedButNotPlayed_HasNoResult()
    {
        var (store, root, saveId) = await PrepareTypeCupAsync(4545UL, 5656UL);
        try
        {
            CupTeamHistoryResponse history = await new GetTypeCupTeamHistoryHandler(store).HandleAsync(saveId, "Elf");
            history.Seasons.Single().State.ShouldBe("Selected");
            history.Seasons.Single().TeamRank.ShouldBeNull();
            history.Seasons.Single().Squad.ShouldAllBe(m => m.Leg == null);
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
