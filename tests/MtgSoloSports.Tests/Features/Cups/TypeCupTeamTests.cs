using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.GetTypeCupTeamResult;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.ListHonours;
using MtgSoloSports.Features.Stories;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;
using RunTeamMember = MtgSoloSports.Features.Cups.RunTypeCupTeam.TypeCupTeamMember;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class TypeCupTeamTests
{
    [Fact]
    public async Task Run_BeforeSelection_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Team Early", 1111UL, 2222UL, UniverseTestCatalog.Build());
            RunTypeCupTeamHandler handler = new(store);
            await Should.ThrowAsync<RunTypeCupTeamConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_OddSeason_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Team Odd", 3033UL, 4044UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> activeIds = await TakeAthletesAsync(store, saveId, 8);
            await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]);
            await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]);
            await CreateEvenSeasonAsync(store, saveId, 2, activeIds, []);
            SelectTypeCupTeamsHandler select = new(store);
            await select.HandleAsync(saveId, sourceSeasonNumber: 2);

            RunTypeCupTeamHandler handler = new(store);
            await Should.ThrowAsync<RunTypeCupTeamConflictException>(
                () => handler.HandleAsync(saveId, sourceSeasonNumber: 1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_SingleTeam_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Team Single", 5055UL, 6066UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> activeIds = await TakeAthletesAsync(store, saveId, 4);
            await SetTypesAsync(store, saveId, activeIds, ["Human", "Wizard"]);
            await CreateEvenSeasonAsync(store, saveId, 2, activeIds, []);

            // Four athletes sharing the same two types can field only one team.
            SelectTypeCupTeamsHandler select = new(store);
            SelectTypeCupTeamsResponse allocation = await select.HandleAsync(saveId, sourceSeasonNumber: 2);
            allocation.TeamCount.ShouldBe(1);

            RunTypeCupTeamHandler handler = new(store);
            await Should.ThrowAsync<InvalidOperationException>(
                () => handler.HandleAsync(saveId, sourceSeasonNumber: 2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Get_BeforeResolved_ReturnsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Team Missing", 7077UL, 8088UL, UniverseTestCatalog.Build());
            GetTypeCupTeamResultHandler query = new(store);
            await Should.ThrowAsync<TypeCupTeamResultNotFoundException>(
                () => query.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WrongRankGroup_Aborts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Team Wrong", 9091UL, 1011UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> activeIds = await TakeAthletesAsync(store, saveId, 8);
            await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]);
            await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]);
            await CreateEvenSeasonAsync(store, saveId, 2, activeIds, []);
            SelectTypeCupTeamsHandler select = new(store);
            await select.HandleAsync(saveId, sourceSeasonNumber: 2);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            List<TypeCupSelectionEntity> selection = await context.TypeCupSelections.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).ToListAsync();

            Dictionary<int, List<TypeCupSelectionEntity>> groups = BuildCorruptGroups(selection);
            var rules = MtgSoloSports.SimulationKernel.Rules.RulesV1.CreateDefault();
            Should.Throw<InvalidOperationException>(() => TypeCupTeamInvariants.ValidateGroups(groups, rules, 2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_AfterSelection_ProducesGroupsWithMedalsHonourNationalityAndPreservesHistory()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Team Full", 1213UL, 1415UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> activeIds = await TakeAthletesAsync(store, saveId, 8);
            await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]);
            await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]);
            List<int> poolIds = await TakeAthletesAsync(store, saveId, 4, skip: 8);
            int seasonTwoId = await CreateEvenSeasonAsync(store, saveId, 2, activeIds, poolIds);

            SelectTypeCupTeamsHandler select = new(store);
            SelectTypeCupTeamsResponse allocation = await select.HandleAsync(saveId, sourceSeasonNumber: 2);
            allocation.TeamCount.ShouldBe(2);
            allocation.TotalSelected.ShouldBe(8);

            (int stages, int seasons, int rounds, long lifetime, long effective, long championship, ulong rng) =
                await CapturePreservationAsync(store, saveId);

            RunTypeCupTeamHandler handler = new(store);
            RunTypeCupTeamResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);

            AssertResponseBasics(response, expectedTeams: 2);
            AssertGroupStructure(response, expectedTeams: 2);
            await AssertFullHistoryAsync(store, saveId, response, seasonTwoId, stages, seasons, rounds, lifetime, effective, championship, rng);

            await Should.ThrowAsync<RunTypeCupTeamConflictException>(
                () => handler.HandleAsync(saveId, sourceSeasonNumber: 2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_ThreeTeams_SupportsDynamicallyVaryingField()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Team Three", 1617UL, 1819UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> activeIds = await TakeAthletesAsync(store, saveId, 12);
            await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]);
            await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]);
            await SetTypesAsync(store, saveId, activeIds[8..12], ["Goblin"]);
            await CreateEvenSeasonAsync(store, saveId, 2, activeIds, []);

            SelectTypeCupTeamsHandler select = new(store);
            SelectTypeCupTeamsResponse allocation = await select.HandleAsync(saveId, sourceSeasonNumber: 2);
            allocation.TeamCount.ShouldBe(3);

            RunTypeCupTeamHandler handler = new(store);
            RunTypeCupTeamResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);

            AssertResponseBasics(response, expectedTeams: 3);
            AssertGroupStructure(response, expectedTeams: 3);
            response.Legs.Count.ShouldBe(12);
            response.Teams.Count.ShouldBe(3);
            response.Teams.Select(t => t.TeamRank).OrderBy(r => r).ShouldBe([1, 2, 3]);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).ToListAsync();
            rounds.Count.ShouldBe(32);
            foreach (TypeCupTeamRoundEntity round in rounds)
            {
                TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
                document.Placements.Count.ShouldBe(3);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_ParticipationCapsNationality_Atomically()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Team Cap", 2021UL, 2223UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> activeIds = await TakeAthletesAsync(store, saveId, 8);
            await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]);
            await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]);
            List<int> poolIds = await TakeAthletesAsync(store, saveId, 4, skip: 8);
            await CreateEvenSeasonAsync(store, saveId, 2, activeIds, poolIds);

            SelectTypeCupTeamsHandler select = new(store);
            SelectTypeCupTeamsResponse allocation = await select.HandleAsync(saveId, sourceSeasonNumber: 2);

            // Selection never caps.
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                List<SaveAthleteEntity> before = await context.SaveAthletes
                    .Where(e => activeIds.Contains(e.Id)).ToListAsync();
                foreach (SaveAthleteEntity athlete in before)
                {
                    athlete.TypeCupNationality.ShouldBeNull();
                }
            }

            RunTypeCupTeamHandler handler = new(store);
            RunTypeCupTeamResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2);
            response.TeamCount.ShouldBe(2);

            // Participation caps every selected athlete to its allocated type; pool stays uncapped.
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                Dictionary<int, string> allocated = allocation.Teams
                    .SelectMany(t => t.Members)
                    .ToDictionary(m => m.SaveAthleteId, m => m.CreatureType);
                List<SaveAthleteEntity> participants = await context.SaveAthletes
                    .Where(e => activeIds.Contains(e.Id)).ToListAsync();
                foreach (SaveAthleteEntity athlete in participants)
                {
                    athlete.TypeCupNationality.ShouldBe(allocated[athlete.Id]);
                }

                List<SaveAthleteEntity> pool = await context.SaveAthletes
                    .Where(e => poolIds.Contains(e.Id)).ToListAsync();
                foreach (SaveAthleteEntity athlete in pool)
                {
                    athlete.TypeCupNationality.ShouldBeNull();
                }
            }

            // Athlete profiles expose the permanent nationality.
            var profileHandler = new MtgSoloSports.Features.Athletes.GetProfile.GetAthleteProfileHandler(store);
            var profile = await profileHandler.HandleAsync(saveId, activeIds[0]);
            profile.Card.TypeCupNationality.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Nationality_CanNeverChange_InLaterTypeCups()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Team Immutable", 2425UL, 2627UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> activeIds = await TakeAthletesAsync(store, saveId, 8);
            await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]);
            await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]);
            await CreateEvenSeasonAsync(store, saveId, 2, activeIds, []);

            SelectTypeCupTeamsHandler select = new(store);
            await select.HandleAsync(saveId, sourceSeasonNumber: 2);
            RunTypeCupTeamHandler run = new(store);
            RunTypeCupTeamResponse first = await run.HandleAsync(saveId, sourceSeasonNumber: 2);
            first.TeamCount.ShouldBe(2);

            Dictionary<int, string> capped = await LoadNationalitiesAsync(store, saveId, activeIds);
            await RunLaterCupAndAssertImmutableAsync(store, saveId, activeIds, capped, select, run);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Dictionary<int, string>> LoadNationalitiesAsync(SaveStore store, Guid saveId, List<int> activeIds)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        Dictionary<int, string> capped = await context.SaveAthletes
            .Where(e => activeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.TypeCupNationality!).ConfigureAwait(false);
        foreach (string nationality in capped.Values)
        {
            nationality.ShouldNotBeNullOrWhiteSpace();
        }

        return capped;
    }

    private static async Task RunLaterCupAndAssertImmutableAsync(
        SaveStore store, Guid saveId, List<int> activeIds, Dictionary<int, string> capped,
        SelectTypeCupTeamsHandler select, RunTypeCupTeamHandler run)
    {
        await SetTypesAsync(store, saveId, activeIds, ["Elf", "Dwarf", "Goblin"]).ConfigureAwait(false);
        await CreateEvenSeasonAsync(store, saveId, 4, activeIds, []).ConfigureAwait(false);

        SelectTypeCupTeamsResponse later = await select.HandleAsync(saveId, sourceSeasonNumber: 4).ConfigureAwait(false);
        later.TeamCount.ShouldBeGreaterThanOrEqualTo(2);
        Dictionary<int, string> laterByAthlete = later.Teams
            .SelectMany(t => t.Members)
            .ToDictionary(m => m.SaveAthleteId, m => m.CreatureType);
        foreach (int id in activeIds.Where(id => laterByAthlete.ContainsKey(id)))
        {
            laterByAthlete[id].ShouldBe(capped[id]);
        }

        RunTypeCupTeamResponse second = await run.HandleAsync(saveId, sourceSeasonNumber: 4).ConfigureAwait(false);
        second.TeamCount.ShouldBe(later.TeamCount);

        using SaveDbContext context = store.OpenDbContext(saveId);
        Dictionary<int, string> after = await context.SaveAthletes
            .Where(e => activeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.TypeCupNationality!).ConfigureAwait(false);
        foreach (int id in activeIds)
        {
            after[id].ShouldBe(capped[id]);
        }
    }

    [Fact]
    public async Task CorruptNationalityChange_AbortsInsteadOfRepairing()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Type Team Corrupt", 2829UL, 3031UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            List<int> activeIds = await TakeAthletesAsync(store, saveId, 8);
            await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]);
            await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]);
            await CreateEvenSeasonAsync(store, saveId, 2, activeIds, []);

            SelectTypeCupTeamsHandler select = new(store);
            await select.HandleAsync(saveId, sourceSeasonNumber: 2);
            RunTypeCupTeamHandler run = new(store);
            await run.HandleAsync(saveId, sourceSeasonNumber: 2);

            string capped = await LoadSingleNationalityAsync(store, saveId, activeIds[0]);
            capped.ShouldBe("Elf");

            // New season: corrupt the athlete's capped nationality so it no longer
            // matches the fresh allocation. The run must abort rather than repair.
            await CreateEvenSeasonAsync(store, saveId, 4, activeIds, []);
            await select.HandleAsync(saveId, sourceSeasonNumber: 4);
            await CorruptNationalityAsync(store, saveId, activeIds[0], "Dragon");

            await Should.ThrowAsync<InvalidOperationException>(
                () => run.HandleAsync(saveId, sourceSeasonNumber: 4));

            await AssertCorruptRunRolledBackAsync(store, saveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> LoadSingleNationalityAsync(SaveStore store, Guid saveId, int athleteId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveAthleteEntity athlete = await context.SaveAthletes.SingleAsync(e => e.Id == athleteId).ConfigureAwait(false);
        return athlete.TypeCupNationality!;
    }

    private static async Task CorruptNationalityAsync(SaveStore store, Guid saveId, int athleteId, string wrong)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveAthleteEntity athlete = await context.SaveAthletes.SingleAsync(e => e.Id == athleteId).ConfigureAwait(false);
        athlete.TypeCupNationality = wrong;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task AssertCorruptRunRolledBackAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonFour = await context.Seasons.SingleAsync(e => e.SeasonNumber == 4).ConfigureAwait(false);
        bool hasTeams = await context.TypeCupTeamStandings.AnyAsync(e => e.SourceSeasonId == seasonFour.Id).ConfigureAwait(false);
        hasTeams.ShouldBeFalse();
        bool hasLegs = await context.TypeCupTeamGroupStandings.AnyAsync(e => e.SourceSeasonId == seasonFour.Id).ConfigureAwait(false);
        hasLegs.ShouldBeFalse();
        bool hasRounds = await context.TypeCupTeamRounds.AnyAsync(e => e.SourceSeasonId == seasonFour.Id).ConfigureAwait(false);
        hasRounds.ShouldBeFalse();
    }

    [Fact]
    public async Task Run_SameSeed_IsDeterministic()
    {
        (RunTypeCupTeamResponse first, string rootFirst) = await RunTeamForSeedAsync(42421UL, 7771UL);
        (RunTypeCupTeamResponse second, string rootSecond) = await RunTeamForSeedAsync(42421UL, 7771UL);
        try
        {
            first.Checksum.ShouldBe(second.Checksum);
            first.Teams.Select(t => t.CreatureType).ShouldBe(second.Teams.Select(t => t.CreatureType).ToList());
            first.Teams.Select(t => t.TeamScoreThousandths).ShouldBe(second.Teams.Select(t => t.TeamScoreThousandths).ToList());
            first.ChampionCreatureType.ShouldBe(second.ChampionCreatureType);
            first.RngBeforeState.ShouldBe(second.RngBeforeState);
            first.RngAfterState.ShouldBe(second.RngAfterState);
        }
        finally
        {
            Directory.Delete(rootFirst, recursive: true);
            Directory.Delete(rootSecond, recursive: true);
        }
    }

    private static void AssertResponseBasics(RunTypeCupTeamResponse response, int expectedTeams)
    {
        response.SourceSeasonNumber.ShouldBe(2);
        response.TeamCount.ShouldBe(expectedTeams);
        response.GroupCount.ShouldBe(4);
        response.GroupRounds.ShouldBe(8);
        response.Teams.Count.ShouldBe(expectedTeams);
        response.Legs.Count.ShouldBe(expectedTeams * 4);
        response.Teams.Select(t => t.TeamRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, expectedTeams).ToList());
        response.Checksum.ShouldNotBeNullOrWhiteSpace();

        RunTeamMember gold = response.Teams.Single(t => t.TeamRank == 1);
        gold.Medal.ShouldBe(nameof(TypeCupMedal.Gold));
        response.ChampionCreatureType.ShouldBe(gold.CreatureType);
        response.ChampionTeamName.ShouldBe(gold.TeamName);
        if (expectedTeams >= 2)
        {
            response.Teams.Single(t => t.TeamRank == 2).Medal.ShouldBe(nameof(TypeCupMedal.Silver));
        }

        if (expectedTeams >= 3)
        {
            response.Teams.Single(t => t.TeamRank == 3).Medal.ShouldBe(nameof(TypeCupMedal.Bronze));
        }

        response.Teams.Where(t => t.TeamRank > 3).All(t => string.Equals(t.Medal, nameof(TypeCupMedal.None), StringComparison.Ordinal)).ShouldBeTrue();
    }

    private static void AssertGroupStructure(RunTypeCupTeamResponse response, int expectedTeams)
    {
        foreach (int group in Enumerable.Range(1, 4))
        {
            List<TypeCupTeamLegMember> legs = response.Legs.Where(l => l.GroupNumber == group).ToList();
            legs.Count.ShouldBe(expectedTeams);
            legs.Select(l => l.GroupRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, expectedTeams).ToList());
            legs.All(l => l.SelectionRank == group).ShouldBeTrue();
            legs.Select(l => l.CreatureType).Distinct(StringComparer.Ordinal).Count().ShouldBe(expectedTeams);
        }
    }

    private static async Task AssertFullHistoryAsync(
        SaveStore store,
        Guid saveId,
        RunTypeCupTeamResponse response,
        int seasonTwoId,
        int stages,
        int seasons,
        int rounds,
        long lifetime,
        long effective,
        long championship,
        ulong rng)
    {
        await AssertTeamScoresAreSumsAsync(store, saveId, response).ConfigureAwait(false);
        await AssertNoWrongRankGroupAsync(store, saveId, response).ConfigureAwait(false);
        await AssertPersistedAsync(store, saveId, response).ConfigureAwait(false);
        await AssertHonourAsync(store, saveId, response).ConfigureAwait(false);
        await AssertStoriesAsync(store, saveId, response).ConfigureAwait(false);
        await AssertNationalityAsync(store, saveId, response).ConfigureAwait(false);
        await AssertPreservationAsync(store, saveId, stages, seasons, rounds, lifetime, effective, championship, rng).ConfigureAwait(false);
        await AssertQueryMatchesAsync(store, saveId, response).ConfigureAwait(false);
        await AssertReplayAsync(store, saveId, response).ConfigureAwait(false);
    }

    private static Dictionary<int, List<TypeCupSelectionEntity>> BuildCorruptGroups(List<TypeCupSelectionEntity> selection)
    {
        Dictionary<int, List<TypeCupSelectionEntity>> groups = new()
        {
            [1] = selection.Where(e => e.SelectionRank == 1).ToList(),
            [2] = selection.Where(e => e.SelectionRank == 2).ToList(),
            [3] = selection.Where(e => e.SelectionRank == 3).ToList(),
            [4] = selection.Where(e => e.SelectionRank == 4).ToList(),
        };
        TypeCupSelectionEntity intruder = groups[1][0];
        groups[1].RemoveAt(0);
        groups[2].Add(intruder);
        return groups;
    }

    private static async Task AssertTeamScoresAreSumsAsync(SaveStore store, Guid saveId, RunTypeCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        foreach (RunTeamMember team in response.Teams)
        {
            int expected = legs.Where(l => string.Equals(l.CreatureType, team.CreatureType, StringComparison.Ordinal)).Sum(l => l.GroupScoreThousandths);
            team.TeamScoreThousandths.ShouldBe(expected);
            int expectedBase = legs.Where(l => string.Equals(l.CreatureType, team.CreatureType, StringComparison.Ordinal)).Sum(l => l.BaseScoreThousandths);
            team.TeamBaseThousandths.ShouldBe(expectedBase);
            legs.Count(l => string.Equals(l.CreatureType, team.CreatureType, StringComparison.Ordinal)).ShouldBe(4);
        }
    }

    private static async Task AssertNoWrongRankGroupAsync(SaveStore store, Guid saveId, RunTypeCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        Dictionary<int, int> selectionRanks = await context.TypeCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, e => e.SelectionRank).ConfigureAwait(false);
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(32);
        foreach (TypeCupTeamRoundEntity round in rounds)
        {
            TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
            document.GroupNumber.ShouldBe(round.GroupNumber);
            document.Placements.Count.ShouldBe(response.TeamCount);
            foreach (var placement in document.Placements)
            {
                selectionRanks.TryGetValue(placement.AthleteId, out int rank).ShouldBeTrue();
                rank.ShouldBe(round.GroupNumber);
            }
        }

        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        foreach (TypeCupTeamGroupStandingEntity leg in legs)
        {
            leg.SelectionRank.ShouldBe(leg.GroupNumber);
            selectionRanks[leg.SaveAthleteId].ShouldBe(leg.GroupNumber);
        }
    }

    private static async Task AssertPersistedAsync(SaveStore store, Guid saveId, RunTypeCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);

        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber).ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(32);
        foreach (int group in Enumerable.Range(1, 4))
        {
            rounds.Where(r => r.GroupNumber == group).Select(r => r.RoundNumber).ShouldBe(Enumerable.Range(1, 8).ToList());
        }

        foreach (TypeCupTeamRoundEntity round in rounds)
        {
            round.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();
            TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
            document.Placements.Count.ShouldBe(response.TeamCount);
            document.Checksum.ShouldBe(round.PayloadChecksum);
            document.SourceSeasonNumber.ShouldBe(response.SourceSeasonNumber);
            document.GroupNumber.ShouldBe(round.GroupNumber);
        }

        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        legs.Count.ShouldBe(response.TeamCount * 4);

        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.TeamRank).ToListAsync().ConfigureAwait(false);
        teams.Count.ShouldBe(response.TeamCount);
        teams.Select(s => s.TeamRank).ShouldBe(Enumerable.Range(1, response.TeamCount).ToList());
        teams.Single(s => s.TeamRank == 1).Medal.ShouldBe((int)TypeCupMedal.Gold);
        if (response.TeamCount >= 2)
        {
            teams.Single(s => s.TeamRank == 2).Medal.ShouldBe((int)TypeCupMedal.Silver);
        }

        if (response.TeamCount >= 3)
        {
            teams.Single(s => s.TeamRank == 3).Medal.ShouldBe((int)TypeCupMedal.Bronze);
        }
    }

    private static async Task AssertHonourAsync(SaveStore store, Guid saveId, RunTypeCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        List<HonourEntity> teamHonours = await context.Honours.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && (e.Kind == (int)HonourKind.TypeCupTeamChampion
                || e.Kind == (int)HonourKind.TypeCupTeamRunnerUp
                || e.Kind == (int)HonourKind.TypeCupTeamThirdPlace))
            .ToListAsync().ConfigureAwait(false);
        // MSS-047: podium teams 1st/2nd/3rd (or fewer when the field is small) each contribute four honours.
        int podiumRanks = Math.Min(3, response.TeamCount);
        teamHonours.Count.ShouldBe(podiumRanks * 4);
        teamHonours.Count(h => h.Kind == (int)HonourKind.TypeCupTeamChampion).ShouldBe(4);
        if (podiumRanks >= 2)
        {
            teamHonours.Count(h => h.Kind == (int)HonourKind.TypeCupTeamRunnerUp).ShouldBe(4);
        }

        if (podiumRanks >= 3)
        {
            teamHonours.Count(h => h.Kind == (int)HonourKind.TypeCupTeamThirdPlace).ShouldBe(4);
        }

        foreach (HonourEntity honour in teamHonours)
        {
            honour.LeagueName.ShouldBe(RunTypeCupTeamHandler.TeamLeagueName);
        }

        HashSet<int> championLegs = response.Legs
            .Where(l => string.Equals(l.CreatureType, response.ChampionTeamName, StringComparison.Ordinal))
            .Select(l => l.AthleteId).ToHashSet();
        championLegs.Count.ShouldBe(4);
        teamHonours.Where(h => h.Kind == (int)HonourKind.TypeCupTeamChampion).Select(h => h.SaveAthleteId).OrderBy(id => id).ShouldBe(championLegs.OrderBy(id => id).ToList());

        if (response.TeamCount >= 4)
        {
            List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
            TypeCupTeamStandingEntity fourth = teams.Single(t => t.TeamRank == 4);
            List<TypeCupTeamGroupStandingEntity> fourthLegs = await context.TypeCupTeamGroupStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.CreatureType == fourth.CreatureType).ToListAsync().ConfigureAwait(false);
            fourthLegs.Count.ShouldBe(4);
            foreach (TypeCupTeamGroupStandingEntity leg in fourthLegs)
            {
                teamHonours.Any(h => h.SaveAthleteId == leg.SaveAthleteId).ShouldBeFalse();
            }
        }

        ListHonoursHandler honoursHandler = new(store);
        ListHonoursResponse honours = await honoursHandler.HandleAsync(saveId).ConfigureAwait(false);
        honours.Honours.Any(h =>
            h.SeasonNumber == response.SourceSeasonNumber &&
            string.Equals(h.HonourKind, nameof(HonourKind.TypeCupTeamChampion), StringComparison.Ordinal)).ShouldBeTrue();
    }

    private static async Task AssertStoriesAsync(SaveStore store, Guid saveId, RunTypeCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<StoryEventEntity> stories = await context.StoryEvents.AsNoTracking()
            .Where(e => e.SeasonNumber == response.SourceSeasonNumber &&
                (e.EventType == StoryEventType.TypeCupTeamTitle || e.EventType == StoryEventType.TypeCupTeamMedal))
            .ToListAsync().ConfigureAwait(false);
        int medalTeams = Math.Min(response.TeamCount, 3);
        stories.Count(e => string.Equals(e.EventType, StoryEventType.TypeCupTeamMedal, StringComparison.Ordinal)).ShouldBe(medalTeams * 4);
        stories.Count(e => string.Equals(e.EventType, StoryEventType.TypeCupTeamTitle, StringComparison.Ordinal)).ShouldBe(4);
        foreach (StoryEventEntity title in stories.Where(e => string.Equals(e.EventType, StoryEventType.TypeCupTeamTitle, StringComparison.Ordinal)))
        {
            string text = StoryEventRenderer.Render(title.EventType, title.ContextJson);
            text.ShouldContain("Type Cup");
        }
    }

    private static async Task AssertNationalityAsync(SaveStore store, Guid saveId, RunTypeCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        Dictionary<int, string> allocated = await context.TypeCupSelections.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, e => e.CreatureType).ConfigureAwait(false);
        List<SaveAthleteEntity> participants = await context.SaveAthletes
            .Where(e => allocated.Keys.Contains(e.Id)).ToListAsync().ConfigureAwait(false);
        participants.Count.ShouldBe(response.TeamCount * 4);
        foreach (SaveAthleteEntity athlete in participants)
        {
            athlete.TypeCupNationality.ShouldBe(allocated[athlete.Id]);
        }
    }

    private static async Task AssertPreservationAsync(
        SaveStore store,
        Guid saveId,
        int stages,
        int seasons,
        int rounds,
        long lifetime,
        long effective,
        long championship,
        ulong rngBefore)
    {
        (int stagesAfter, int seasonsAfter, int roundsAfter, long lifetimeAfter, long effectiveAfter, long championshipAfter, ulong rngAfter) =
            await CapturePreservationAsync(store, saveId).ConfigureAwait(false);
        stagesAfter.ShouldBe(stages);
        seasonsAfter.ShouldBe(seasons);
        roundsAfter.ShouldBe(rounds);
        lifetimeAfter.ShouldBe(lifetime);
        effectiveAfter.ShouldBe(effective);
        championshipAfter.ShouldBe(championship);
        rngAfter.ShouldNotBe(rngBefore);
    }

    private static async Task AssertQueryMatchesAsync(SaveStore store, Guid saveId, RunTypeCupTeamResponse response)
    {
        GetTypeCupTeamResultHandler query = new(store);
        GetTypeCupTeamResultResponse summary = await query.HandleAsync(saveId).ConfigureAwait(false);
        summary.SourceSeasonNumber.ShouldBe(response.SourceSeasonNumber);
        summary.TeamCount.ShouldBe(response.TeamCount);
        summary.GroupCount.ShouldBe(4);
        summary.GroupRounds.ShouldBe(8);
        summary.Checksum.ShouldBe(response.Checksum);
        summary.ChampionCreatureType.ShouldBe(response.ChampionCreatureType);
        summary.Teams.Count.ShouldBe(response.TeamCount);
        summary.Legs.Count.ShouldBe(response.Legs.Count);
        summary.Teams.Select(m => m.CreatureType).ShouldBe(response.Teams.Select(m => m.CreatureType).ToList());
        summary.Teams.Select(m => m.TeamRank).ShouldBe(response.Teams.Select(m => m.TeamRank).ToList());
        summary.Teams.Select(m => m.Medal).ShouldBe(response.Teams.Select(m => m.Medal).ToList());

        GetTypeCupTeamResultResponse again = await query.HandleAsync(saveId, sourceSeasonNumber: response.SourceSeasonNumber).ConfigureAwait(false);
        again.ChampionCreatureType.ShouldBe(response.ChampionCreatureType);
        again.GroupRounds.ShouldBe(8);
    }

    private static async Task AssertReplayAsync(SaveStore store, Guid saveId, RunTypeCupTeamResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber).ToListAsync().ConfigureAwait(false);
        TypeCupTeamRoundPayloadDocument first = TypeCupTeamRoundPayloadDocument.FromStored(rounds[0].PayloadJson);
        first.Placements.Count.ShouldBe(response.TeamCount);
        first.RngBeforeState.ShouldBe(response.RngBeforeState);
        TypeCupTeamRoundPayloadDocument last = TypeCupTeamRoundPayloadDocument.FromStored(rounds[^1].PayloadJson);
        last.RngAfterState.ShouldBe(response.RngAfterState);
        foreach (TypeCupTeamRoundEntity round in rounds)
        {
            round.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();
            TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
            document.Checksum.ShouldBe(round.PayloadChecksum);
        }
    }

    private static async Task<(RunTypeCupTeamResponse Response, string Root)> RunTeamForSeedAsync(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Type Team Det", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        List<int> activeIds = await TakeAthletesAsync(store, saveId, 8).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, activeIds[..4], ["Elf"]).ConfigureAwait(false);
        await SetTypesAsync(store, saveId, activeIds[4..8], ["Dwarf"]).ConfigureAwait(false);
        await CreateEvenSeasonAsync(store, saveId, 2, activeIds, []).ConfigureAwait(false);
        SelectTypeCupTeamsHandler select = new(store);
        await select.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        RunTypeCupTeamHandler handler = new(store);
        RunTypeCupTeamResponse response = await handler.HandleAsync(saveId, sourceSeasonNumber: 2).ConfigureAwait(false);
        return (response, root);
    }

    private static async Task<(int StageCount, int SeasonCount, int RoundCount, long Lifetime, long Effective, long Championship, ulong Rng)> CapturePreservationAsync(
        SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int stages = await context.StageStandings.CountAsync().ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync().ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync().ConfigureAwait(false);
        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths).ConfigureAwait(false);
        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths).ConfigureAwait(false);
        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths).ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return (stages, seasons, rounds, lifetime, effective, championship, (ulong)rng.State);
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

    private static async Task<int> CreateEvenSeasonAsync(
        SaveStore store, Guid saveId, int seasonNumber, List<int> activeIds, List<int> poolIds)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await EnsureEvenSeasonRowAsync(context, seasonNumber).ConfigureAwait(false);
        LeagueEntity league = await EnsureEvenLeagueAsync(context, season).ConfigureAwait(false);
        await AddEvenMembershipsAsync(context, season, league, activeIds, poolIds).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return season.Id;
    }

    private static async Task<SeasonEntity> EnsureEvenSeasonRowAsync(SaveDbContext context, int seasonNumber)
    {
        SeasonEntity? existing = await context.Seasons.SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.IsComplete = true;
            await context.SaveChangesAsync().ConfigureAwait(false);
            return existing;
        }

        SeasonEntity season = new() { SeasonNumber = seasonNumber, HasSuperleague = false, IsComplete = true };
        context.Seasons.Add(season);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return season;
    }

    private static async Task<LeagueEntity> EnsureEvenLeagueAsync(SaveDbContext context, SeasonEntity season)
    {
        LeagueEntity? league = await context.Leagues.SingleOrDefaultAsync(e => e.SeasonId == season.Id).ConfigureAwait(false);
        if (league is not null)
        {
            return league;
        }

        LeagueEntity created = new()
        {
            SeasonId = season.Id,
            SportingColor = 0,
            Kind = (int)LeagueKind.Feeder,
            Name = "White League",
        };
        context.Leagues.Add(created);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return created;
    }

    private static async Task AddEvenMembershipsAsync(
        SaveDbContext context, SeasonEntity season, LeagueEntity league, List<int> activeIds, List<int> poolIds)
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

        foreach (int id in poolIds)
        {
            bool exists = await context.SeasonMemberships.AnyAsync(e => e.SeasonId == season.Id && e.SaveAthleteId == id).ConfigureAwait(false);
            if (!exists)
            {
                context.SeasonMemberships.Add(new SeasonMembershipEntity
                {
                    SeasonId = season.Id,
                    LeagueId = null,
                    SaveAthleteId = id,
                    SportingColor = 0,
                    DrawIndex = 0,
                });
            }
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-typecup-team-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
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
