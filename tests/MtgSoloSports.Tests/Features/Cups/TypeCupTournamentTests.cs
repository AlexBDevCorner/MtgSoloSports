using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Features.Cups.GetTypeCupTournament;
using MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-062: Type Cup qualification groups plus the 32-team Final as resumable
/// postseason events. Covers direct Finals (32) and tournaments
/// (33, 35, 64, 65, 97): fixed qual order, exact quotas, 32-team Finals from
/// zero, independent scoring/ranking, separate persistence, nationality for
/// actual participants, Final-only honours, one-shot/step equivalence and
/// reload resume. Old single-field saves remain readable (legacy path).
/// </summary>
public sealed class TypeCupTournamentTests
{
    public static TheoryData<int, int[], int[]> TournamentShapes()
    {
        var data = new TheoryData<int, int[], int[]>();
        data.Add(33, [17, 16], [16, 16]);
        data.Add(35, [18, 17], [16, 16]);
        data.Add(64, [32, 32], [16, 16]);
        data.Add(65, [22, 22, 21], [11, 11, 10]);
        data.Add(97, [25, 24, 24, 24], [8, 8, 8, 8]);
        return data;
    }

    [Fact]
    public async Task DirectFinal_32_BehavesAsSingleFinal()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 32, 50000UL, 50100UL);
            await SelectAsync(store, saveId);
            RunTypeCupTeamResponse response = await RunAsync(store, saveId);
            response.TeamCount.ShouldBe(32);
            response.GroupCount.ShouldBe(4);
            response.GroupRounds.ShouldBe(8);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).ToListAsync();
            rounds.Count.ShouldBe(32);
            rounds.All(r => r.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final).ShouldBeTrue();
            rounds.All(r => r.QualificationGroup == 0).ShouldBeTrue();
            foreach (TypeCupTeamRoundEntity round in rounds)
            {
                TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
                document.Placements.Count.ShouldBe(32);
            }

            List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).ToListAsync();
            teams.Count.ShouldBe(32);
            teams.Single(t => t.TeamRank == 1).Medal.ShouldBe((int)TypeCupMedal.Gold);

            GetTypeCupTournamentHandler tournament = new(store);
            GetTypeCupTournamentResponse summary = await tournament.HandleAsync(saveId, sourceSeasonNumber: 2);
            summary.IsDirectFinal.ShouldBeTrue();
            summary.TeamCount.ShouldBe(32);
            summary.QualificationGroups.Count.ShouldBe(0);
            summary.Finalists.Count.ShouldBe(32);
            summary.Final.ShouldNotBeNull();
            summary.Final!.TeamCount.ShouldBe(32);
            summary.ChampionCreatureType.ShouldBe(response.ChampionCreatureType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(TournamentShapes))]
    public async Task Tournament_RunsAllQualGroups_BeforeFinal(int teamCount, int[] sizes, int[] quotas)
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, teamCount, 51000UL + (ulong)teamCount, 51100UL + (ulong)teamCount);
            await SelectAsync(store, saveId);
            await DrawAsync(store, saveId);
            RunTypeCupTeamResponse response = await RunAsync(store, saveId);
            response.TeamCount.ShouldBe(32);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            await AssertQualGroupsAsync(context, source, sizes, quotas);
            await AssertFinalAsync(context, source, response);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(35)]
    [InlineData(65)]
    public async Task OneShot_EqualsStepByStep(int teamCount)
    {
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareTournamentAsync(teamCount, 52000UL + (ulong)teamCount, 52100UL + (ulong)teamCount);
        var (stepStore, stepRoot, stepId) = await PrepareTournamentAsync(teamCount, 52000UL + (ulong)teamCount, 52100UL + (ulong)teamCount);
        try
        {
            await new RunTypeCupTeamHandler(oneShotStore).HandleAsync(oneShotId, sourceSeasonNumber: 2);
            PlayTypeCupTeamRoundHandler step = new(stepStore);
            int total = TotalRounds(teamCount);
            for (int index = 0; index < total; index++)
            {
                PlayTypeCupTeamRoundResponse played = await step.HandleAsync(stepId, sourceSeasonNumber: 2);
                played.RoundsPlayed.ShouldBe(index + 1);
                played.TotalRounds.ShouldBe(total);
                played.IsComplete.ShouldBe(index == total - 1);
            }

            (await SnapshotAsync(stepStore, stepId)).ShouldBe(await SnapshotAsync(oneShotStore, oneShotId));
        }
        finally
        {
            DeleteRoot(oneShotRoot);
            DeleteRoot(stepRoot);
        }
    }

    [Fact]
    public async Task Resume_AfterPartialProgress_ContinuesExactly()
    {
        // Stop/reopen after qual group 1 round 3; after one full qual group;
        // after all quals but before Final; Final group #3 round 4.
        const int teamCount = 35;
        int perStage = 32;
        var (fullStore, fullRoot, fullId) = await PrepareTournamentAsync(teamCount, 53000UL, 53100UL);
        try
        {
            await new RunTypeCupTeamHandler(fullStore).HandleAsync(fullId, sourceSeasonNumber: 2);
            List<string> full = await SnapshotAsync(fullStore, fullId);

            await AssertResumeAsync(teamCount, 3, full, 53000UL, 53100UL);
            await AssertResumeAsync(teamCount, perStage, full, 53000UL, 53100UL);
            await AssertResumeAsync(teamCount, perStage * 2, full, 53000UL, 53100UL);
            await AssertResumeAsync(teamCount, (perStage * 2) + (2 * 8) + 4, full, 53000UL, 53100UL);
        }
        finally
        {
            DeleteRoot(fullRoot);
        }
    }

    [Fact]
    public async Task QualScores_AbsentFromFinalTotals()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 35, 54000UL, 54100UL);
            await SelectAsync(store, saveId);
            await DrawAsync(store, saveId);
            await RunAsync(store, saveId);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            List<TypeCupTeamStandingEntity> qualTeams = await context.TypeCupTeamStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
                .ToListAsync();
            List<TypeCupTeamStandingEntity> finalTeams = await context.TypeCupTeamStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final)
                .ToListAsync();
            qualTeams.Count.ShouldBe(35);
            finalTeams.Count.ShouldBe(32);

            List<TypeCupTeamGroupStandingEntity> finalLegs = await context.TypeCupTeamGroupStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final)
                .ToListAsync();
            foreach (TypeCupTeamStandingEntity team in finalTeams)
            {
                int expected = finalLegs
                    .Where(l => string.Equals(l.CreatureType, team.CreatureType, StringComparison.Ordinal))
                    .Sum(l => l.GroupScoreThousandths);
                team.TeamScoreThousandths.ShouldBe(expected);
            }

            // Final round 1 cumulative starts from zero (no carryover).
            List<TypeCupTeamRoundEntity> finalRounds = await context.TypeCupTeamRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final)
                .ToListAsync();
            foreach (TypeCupTeamRoundEntity round in finalRounds.Where(r => r.GroupNumber == 1 && r.RoundNumber == 1))
            {
                TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
                foreach (var placement in document.Placements)
                {
                    placement.CumulativeBeforeThousandths.ShouldBe(0);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Eliminated_RetainNationality_WithoutPodiumHonour()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 35, 55000UL, 55100UL);
            await SelectAsync(store, saveId);
            await DrawAsync(store, saveId);
            await RunAsync(store, saveId);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            List<TypeCupTeamStandingEntity> qualTeams = await context.TypeCupTeamStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
                .ToListAsync();
            List<TypeCupTeamStandingEntity> finalTeams = await context.TypeCupTeamStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final)
                .ToListAsync();
            HashSet<string> finalists = finalTeams.Select(t => t.CreatureType).ToHashSet(StringComparer.Ordinal);
            List<string> eliminated = qualTeams.Select(t => t.CreatureType).Where(t => !finalists.Contains(t)).ToList();
            eliminated.Count.ShouldBe(3);

            // Eliminated qualification teams have no medals.
            foreach (TypeCupTeamStandingEntity team in qualTeams)
            {
                team.Medal.ShouldBe((int)TypeCupMedal.None);
            }

            // Only Final top 3 hold official podium honours (four members each).
            List<HonourEntity> honours = await context.Honours.AsNoTracking()
                .Where(e => e.SeasonId == source.Id && (e.Kind == (int)HonourKind.TypeCupTeamChampion
                    || e.Kind == (int)HonourKind.TypeCupTeamRunnerUp
                    || e.Kind == (int)HonourKind.TypeCupTeamThirdPlace)).ToListAsync();
            honours.Count.ShouldBe(12);
            HashSet<int> honourAthletes = honours.Select(h => h.SaveAthleteId).ToHashSet();
            List<TypeCupTeamGroupStandingEntity> qualLegs = await context.TypeCupTeamGroupStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
                .ToListAsync();
            List<TypeCupTeamGroupStandingEntity> eliminatedLegs = qualLegs.Where(l => eliminated.Contains(l.CreatureType, StringComparer.Ordinal)).ToList();
            eliminatedLegs.Count.ShouldBe(3 * 4);
            foreach (TypeCupTeamGroupStandingEntity leg in eliminatedLegs)
            {
                honourAthletes.Contains(leg.SaveAthleteId).ShouldBeFalse();
            }

            await AssertEliminatedNationalityAsync(context, source, eliminatedLegs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertEliminatedNationalityAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<TypeCupTeamGroupStandingEntity> eliminatedLegs)
    {
        Dictionary<int, string> allocated = (await context.TypeCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false))
            .ToDictionary(e => e.SaveAthleteId, e => e.CreatureType);
        List<int> eliminatedIds = eliminatedLegs.Select(l => l.SaveAthleteId).ToList();
        List<SaveAthleteEntity> participants = await context.SaveAthletes
            .Where(e => eliminatedIds.Contains(e.Id)).ToListAsync().ConfigureAwait(false);
        participants.Count.ShouldBe(12);
        foreach (SaveAthleteEntity athlete in participants)
        {
            athlete.TypeCupNationality.ShouldBe(allocated[athlete.Id]);
        }
    }

    [Fact]
    public async Task TournamentSummary_ExposesDrawQualFinalistsFinalAndChampion()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await SetupFieldAsync(store, 35, 56000UL, 56100UL);
            await SelectAsync(store, saveId);
            DrawTypeCupQualificationGroupsResponse draw = await DrawAsync(store, saveId);
            RunTypeCupTeamResponse run = await RunAsync(store, saveId);

            GetTypeCupTournamentHandler handler = new(store);
            GetTypeCupTournamentResponse summary = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);
            summary.IsDirectFinal.ShouldBeFalse();
            summary.TeamCount.ShouldBe(35);
            summary.QualificationGroupCount.ShouldBe(2);
            summary.GroupSizes.ShouldBe(draw.GroupSizes);
            summary.FinalPlacesPerGroup.ShouldBe(draw.FinalPlacesPerGroup);
            summary.DrawChecksum.ShouldBe(draw.DrawChecksum);
            summary.QualificationGroups.Count.ShouldBe(2);
            summary.Finalists.Count.ShouldBe(32);
            summary.Final.ShouldNotBeNull();
            summary.Final!.TeamCount.ShouldBe(32);
            summary.Final.Teams.Count.ShouldBe(32);
            summary.Final.Legs.Count.ShouldBe(128);
            summary.ChampionCreatureType.ShouldBe(run.ChampionCreatureType);
            summary.TournamentChecksum.ShouldBe(run.Checksum);

            GetTypeCupTeamResultHandler result = new(store);
            GetTypeCupTeamResultResponse final = await result.HandleAsync(saveId, sourceSeasonNumber: 2);
            final.TeamCount.ShouldBe(32);
            final.Checksum.ShouldBe(run.Checksum);
            final.ChampionCreatureType.ShouldBe(run.ChampionCreatureType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int TotalRounds(int teamCount)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        int groups = TypeCupTournamentFormat.QualificationGroupCount(teamCount, rules);
        return groups == 0 ? 32 : (groups * 32) + 32;
    }

    private static async Task AssertResumeAsync(int teamCount, int prefixRounds, List<string> full, ulong seed, ulong stream)
    {
        var (store, root, saveId) = await PrepareTournamentAsync(teamCount, seed, stream).ConfigureAwait(false);
        try
        {
            PlayTypeCupTeamRoundHandler step = new(store);
            for (int i = 0; i < prefixRounds; i++)
            {
                await step.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
            }

            // Reopen the save (new store over the same files) and finish.
            (SaveStore reopened, _) = OpenStore(root);
            PlayTypeCupTeamRoundHandler resumed = new(reopened);
            int total = TotalRounds(teamCount);
            for (int i = prefixRounds; i < total; i++)
            {
                await resumed.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
            }

            (await SnapshotAsync(reopened, saveId).ConfigureAwait(false)).ShouldBe(full);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertQualGroupsAsync(SaveDbContext context, SeasonEntity source, int[] sizes, int[] quotas)
    {
        int qualPhase = (int)TypeCupTournamentFormat.TournamentPhase.Qualification;
        for (int g = 1; g <= sizes.Length; g++)
        {
            List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == qualPhase && e.QualificationGroup == g).ToListAsync().ConfigureAwait(false);
            rounds.Count.ShouldBe(32);
            foreach (TypeCupTeamRoundEntity round in rounds)
            {
                TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
                document.Placements.Count.ShouldBe(sizes[g - 1]);
                document.Placements.Count.ShouldBeLessThanOrEqualTo(32);
            }

            List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == qualPhase && e.QualificationGroup == g)
                .OrderBy(e => e.TeamRank).ToListAsync().ConfigureAwait(false);
            teams.Count.ShouldBe(sizes[g - 1]);
            teams.Select(t => t.TeamRank).ShouldBe(Enumerable.Range(1, sizes[g - 1]).ToList());
            teams.All(t => t.Medal == (int)TypeCupMedal.None).ShouldBeTrue();
        }

        List<TypeCupTournamentDrawEntity> draws = await context.TypeCupTournamentDraws.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        draws.Count.ShouldBe(sizes.Sum());
        for (int g = 1; g <= sizes.Length; g++)
        {
            draws.Count(d => d.QualificationGroup == g).ShouldBe(sizes[g - 1]);
            draws.First(d => d.QualificationGroup == g).FinalPlacesForGroup.ShouldBe(quotas[g - 1]);
        }
    }

    private static async Task AssertFinalAsync(SaveDbContext context, SeasonEntity source, RunTypeCupTeamResponse response)
    {
        int finalPhase = (int)TypeCupTournamentFormat.TournamentPhase.Final;
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == finalPhase).ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(32);
        foreach (TypeCupTeamRoundEntity round in rounds)
        {
            TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson).Placements.Count.ShouldBe(32);
        }

        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.TournamentPhase == finalPhase)
            .OrderBy(e => e.TeamRank).ToListAsync().ConfigureAwait(false);
        teams.Count.ShouldBe(32);
        teams.Select(t => t.TeamRank).ShouldBe(Enumerable.Range(1, 32).ToList());
        teams.Single(t => t.TeamRank == 1).Medal.ShouldBe((int)TypeCupMedal.Gold);
        teams.Single(t => t.TeamRank == 2).Medal.ShouldBe((int)TypeCupMedal.Silver);
        teams.Single(t => t.TeamRank == 3).Medal.ShouldBe((int)TypeCupMedal.Bronze);
        response.ChampionCreatureType.ShouldBe(teams.Single(t => t.TeamRank == 1).CreatureType);
    }

    private static async Task<List<string>> SnapshotAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<string> lines = [];
        lines.AddRange(await context.TypeCupTeamRounds.OrderBy(e => e.TournamentPhase).ThenBy(e => e.QualificationGroup).ThenBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber)
            .Select(e => "round:" + e.TournamentPhase + ":" + e.QualificationGroup + ":" + e.PayloadJson).ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.TypeCupTeamGroupStandings.OrderBy(e => e.TournamentPhase).ThenBy(e => e.QualificationGroup).ThenBy(e => e.GroupNumber).ThenBy(e => e.GroupRank)
            .Select(e => $"leg:{e.TournamentPhase}:{e.QualificationGroup}:{e.GroupNumber}:{e.SaveAthleteId}:{e.GroupRank}:{e.GroupScoreThousandths}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.TypeCupTeamStandings.OrderBy(e => e.TournamentPhase).ThenBy(e => e.QualificationGroup).ThenBy(e => e.TeamRank)
            .Select(e => $"team:{e.TournamentPhase}:{e.QualificationGroup}:{e.CreatureType}:{e.TeamRank}:{e.TeamScoreThousandths}:{e.Medal}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.SaveAthletes.Where(e => e.TypeCupNationality != null).OrderBy(e => e.Id)
            .Select(e => $"nationality:{e.Id}:{e.TypeCupNationality}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.Honours.OrderBy(e => e.Id)
            .Select(e => $"honour:{e.Kind}:{e.SaveAthleteId}").ToListAsync().ConfigureAwait(false));
        lines.Add("stories:" + await context.StoryEvents.CountAsync().ConfigureAwait(false));
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        lines.Add($"rng:{rng.State}:{rng.Stream}");
        lines.Add("phase:" + (await context.SaveMetadata.SingleAsync().ConfigureAwait(false)).Phase);
        return lines;
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareTournamentAsync(int teamCount, ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        Guid saveId = await SetupFieldAsync(store, teamCount, seed, stream).ConfigureAwait(false);
        await SelectAsync(store, saveId).ConfigureAwait(false);
        await DrawAsync(store, saveId).ConfigureAwait(false);
        return (store, root, saveId);
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
            $"Type Tour {teamCount} {seed}", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
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
            string typeName = $"MSS062-Type-{team:D3}";
            await SetTypesAsync(store, saveId, slice, [typeName]).ConfigureAwait(false);
        }
    }

    private static async Task<List<int>> TakeAthletesAsync(SaveStore store, Guid saveId, int count, int skip = 0)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Skip(skip)
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

        await context.SaveChangesAsync().ConfigureAwait(false);
        AddZeroSelectionInputs(context, season, league, activeIds);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static void AddZeroSelectionInputs(
        SaveDbContext context, SeasonEntity season, LeagueEntity league, List<int> activeIds)
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-typecup-tour-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (OpenStore(root).Store, root);
    }

    private static (SaveStore Store, string Unused) OpenStore(string root)
    {
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
    }

    private static void DeleteRoot(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
