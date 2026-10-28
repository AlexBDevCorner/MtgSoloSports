using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Reports.CupEditionReport;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

public sealed class CupEditionWildcardReportTests
{
    [Fact]
    public async Task WildcardTournament_EditionReportShowsCutLineAndFinal()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 84, 84000UL, 84100UL);
            await SelectAsync(store, saveId);
            await DrawAsync(store, saveId);
            await RunAsync(store, saveId);

            GetCupEditionReportHandler handler = new(store);
            GetCupEditionReportHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, "type", 2, "json");
            jsonFile.ContentType.ShouldBe("application/json; charset=utf-8");
            string jsonBody = System.Text.Encoding.UTF8.GetString(jsonFile.Content);
            using JsonDocument doc = JsonDocument.Parse(jsonBody);
            doc.RootElement.GetProperty("reportType").GetString().ShouldBe("cup-edition");
            doc.RootElement.GetProperty("snapshot").GetProperty("state").GetString().ShouldBe("Completed");
            doc.RootElement.TryGetProperty("typeTournament", out JsonElement tournament).ShouldBeTrue();
            tournament.GetProperty("qualificationPolicyVersion").GetInt32().ShouldBe(2);
            tournament.GetProperty("wildcardCount").GetInt32().ShouldBe(2);
            tournament.GetProperty("qualificationGroups").GetArrayLength().ShouldBe(3);
            tournament.TryGetProperty("wildcards", out JsonElement wildcards).ShouldBeTrue();
            wildcards.GetArrayLength().ShouldBe(2);

            GetCupEditionReportHandler.ReportFile mdFile = await handler.HandleAsync(saveId, "TYPE", 2, "md");
            string mdBody = System.Text.Encoding.UTF8.GetString(mdFile.Content);
            mdBody.ShouldContain("# Type Cup edition report");
            mdBody.ShouldContain("Qualification groups");
            mdBody.ShouldContain("not titles");
            mdBody.ShouldContain("Wildcards");
            mdBody.ShouldContain("official standings");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IncompleteTournament_EditionReportOmitsUnknownFinal()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 35, 91000UL, 91100UL);
            await SelectAsync(store, saveId);
            await DrawAsync(store, saveId);

            GetCupEditionReportHandler handler = new(store);
            GetCupEditionReportHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, "type", 2, "json");
            string jsonBody = System.Text.Encoding.UTF8.GetString(jsonFile.Content);
            using JsonDocument doc = JsonDocument.Parse(jsonBody);
            string? state = doc.RootElement.GetProperty("snapshot").GetProperty("state").GetString();
            string.Equals(state, "Completed", StringComparison.Ordinal).ShouldBeFalse();
            doc.RootElement.TryGetProperty("typeTournament", out JsonElement tournament).ShouldBeTrue();
            tournament.ValueKind.ShouldBe(JsonValueKind.Null);

            GetCupEditionReportHandler.ReportFile mdFile = await handler.HandleAsync(saveId, "type", 2, "md");
            string mdBody = System.Text.Encoding.UTF8.GetString(mdFile.Content);
            mdBody.ShouldContain("pending");
            mdBody.ShouldContain("Qualifier success is not a title");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SelectTypeCupTeamsResponse> SelectAsync(SaveStore store, Guid saveId)
    {
        SelectTypeCupTeamsHandler select = new(store);
        return await select.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
    }

    private static async Task<DrawTypeCupQualificationGroupsResponse> DrawAsync(SaveStore store, Guid saveId)
    {
        DrawTypeCupQualificationGroupsHandler draw = new(store);
        return await draw.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
    }

    private static async Task<RunTypeCupTeamResponse> RunAsync(SaveStore store, Guid saveId)
    {
        RunTypeCupTeamHandler handler = new(store);
        return await handler.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
    }

    private static async Task<Guid> SetupFieldAsync(SaveStore store, int teamCount, ulong seed, ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            $"Wildcard Report {teamCount} {seed}", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        List<int> activeIds = await TakeAthletesAsync(store, saveId, teamCount * 4).ConfigureAwait(false);
        await SetDistinctTypesAsync(store, saveId, activeIds, teamCount).ConfigureAwait(false);
        await CreateEvenSeasonAsync(store, saveId, 2, activeIds).ConfigureAwait(false);
        return saveId;
    }

    private static async Task SetDistinctTypesAsync(SaveStore store, Guid saveId, List<int> activeIds, int teamCount)
    {
        activeIds.Count.ShouldBe(teamCount * 4);
        for (int team = 0; team < teamCount; team++)
        {
            List<int> slice = activeIds.Skip(team * 4).Take(4).ToList();
            string typeName = $"MSS087-Type-{team:D3}";
            await SetTypesAsync(store, saveId, slice, [typeName]).ConfigureAwait(false);
        }
    }

    private static async Task<List<int>> TakeAthletesAsync(SaveStore store, Guid saveId, int count)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Take(count)
            .Select(e => e.Id)
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task SetTypesAsync(SaveStore store, Guid saveId, List<int> ids, string[] types)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<SaveAthleteEntity> athletes = await context.SaveAthletes
            .Where(e => ids.Contains(e.Id))
            .ToListAsync().ConfigureAwait(false);
        athletes.Count.ShouldBe(ids.Count);
        string json = JsonSerializer.Serialize(types);
        foreach (SaveAthleteEntity athlete in athletes)
        {
            athlete.CreatureTypesJson = json;
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task CreateEvenSeasonAsync(SaveStore store, Guid saveId, int seasonNumber, List<int> activeIds)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity? existing = await context.Seasons.SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        SeasonEntity season;
        if (existing is not null)
        {
            existing.IsComplete = true;
            await context.SaveChangesAsync().ConfigureAwait(false);
            season = existing;
        }
        else
        {
            season = new SeasonEntity { SeasonNumber = seasonNumber, HasSuperleague = false, IsComplete = true };
            context.Seasons.Add(season);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        LeagueEntity? league = await context.Leagues.SingleOrDefaultAsync(e => e.SeasonId == season.Id).ConfigureAwait(false);
        if (league is null)
        {
            league = new LeagueEntity
            {
                SeasonId = season.Id,
                SportingColor = 0,
                Kind = (int)LeagueKind.Feeder,
                Name = "White League",
            };
            context.Leagues.Add(league);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        await AddMembershipsAsync(context, season, league, activeIds).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task AddMembershipsAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, List<int> activeIds)
    {
        foreach (int id in activeIds)
        {
            bool exists = await context.SeasonMemberships.AnyAsync(e => e.SeasonId == season.Id && e.SaveAthleteId == id).ConfigureAwait(false);
            if (!exists)
            {
                context.SeasonMemberships.Add(new SeasonMembershipEntity
                {
                    SeasonId = season.Id,
                    LeagueId = league.Id,
                    SaveAthleteId = id,
                    SportingColor = 0,
                    DrawIndex = 0,
                });
            }
        }

        await AddZeroStandingsAsync(context, season, league, activeIds).ConfigureAwait(false);
    }

    private static async Task AddZeroStandingsAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, List<int> activeIds)
    {
        for (int i = 0; i < activeIds.Count; i++)
        {
            context.SeasonStandings.Add(new SeasonStandingEntity
            {
                SeasonId = season.Id,
                LeagueId = league.Id,
                SaveAthleteId = activeIds[i],
                SeasonRank = i + 1,
                TotalChampionshipPointsThousandths = 0,
                TotalStageScoreThousandths = 0,
                TotalBaseScoreThousandths = 0,
                StageWins = 0,
                RoundWins = 0,
                StagePlaceCountsJson = "[]",
                RoundPlaceCountsJson = "[]",
                IsChampion = i == 0,
            });
        }

        foreach (int id in activeIds)
        {
            for (int stage = 23; stage <= 32; stage++)
            {
                context.StageStandings.Add(new StageStandingEntity
                {
                    SeasonId = season.Id,
                    LeagueId = league.Id,
                    StageId = stage,
                    StageNumber = stage,
                    SaveAthleteId = id,
                    StageRank = 10,
                    StageScoreThousandths = 0,
                    BaseScoreThousandths = 0,
                    ChampionshipPointsThousandths = 0,
                    RoundWins = 0,
                    RoundPlaceCountsJson = "[]",
                    EarnedBonusThousandths = 0,
                });
            }
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        return TestSaveStores.CreateStore("mtgsolosports-cupreport-wild-");
    }
}
