using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Seasons.StartNextSeason;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Seasons;

/// <summary>
/// MSS-070: after Type Cup squad selection with a scalable (&gt;32-team) field
/// and no Cup rounds yet, season-status must report <c>RunTypeCupTeam</c> with
/// playable <c>type-cup-team</c> progress. Previously the read path threw an
/// unhandled tournament-draw conflict (HTTP 500) which the UI masked as
/// "Nothing left to run for this season."
/// </summary>
public sealed class TypeCupPostSelectionStatusTests
{
    [Fact]
    public async Task PostSelection_TournamentField_ProgressesToFinal_ThenStartsNextSeason()
    {
        // MSS-122: shared real Type Cup selected template; post-selection status,
        // downgrade/upgrade, step, bulk finish and next-season start below still execute.
        var (store, root, saveId) = await SharedSaveTemplates.ForkTypeCupSelectedAsync("mtgsolosports-mss070-");
        try
        {

            int teamCount = await CountSelectedTeamsAsync(store, saveId);
            teamCount.ShouldBeGreaterThan(32);
            await AssertNoDrawOrRoundsAsync(store, saveId);

            // Core regression: the precise post-selection status.
            GetSeasonStatusResponse preDraw = await AssertPreDrawStatusAsync(store, saveId, teamCount);

            // Season 3 stays forbidden while the Cup is pending.
            await Should.ThrowAsync<StartNextSeasonConflictException>(
                () => new StartNextSeasonHandler(store).HandleAsync(saveId));

            // Pre-tournament-schema save upgraded through supported migrations:
            // squads survive, nothing is invented, status keeps working.
            await AssertDowngradeUpgradeAsync(store, root, saveId, teamCount);

            // First play auto-creates the qualification draw, then reports progress.
            PlayTypeCupTeamRoundHandler step = new(store);
            PlayTypeCupTeamRoundResponse firstRound = await step.HandleAsync(saveId, sourceSeasonNumber: 2);
            firstRound.RoundsPlayed.ShouldBe(1);
            firstRound.IsComplete.ShouldBeFalse();
            await AssertDrawExistsAsync(store, saveId);
            GetSeasonStatusResponse afterFirst = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            afterFirst.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunTypeCupTeam]);
            afterFirst.EventProgress.ShouldNotBeNull();
            afterFirst.EventProgress!.Event.ShouldBe(PostseasonEvents.TypeCupTeam);
            afterFirst.EventProgress.RoundsPlayed.ShouldBe(1);
            afterFirst.EventProgress.TotalRounds.ShouldBe(preDraw.EventProgress!.TotalRounds);
            afterFirst.ComputedPhase.ShouldBe(PostseasonEvents.InProgressPhase(PostseasonEvents.TypeCupTeam));

            // Running the remaining rounds completes every qualification group
            // and then the 32-team Final in order.
            RunTypeCupTeamResponse completed = await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            completed.TeamCount.ShouldBe(32);
            GetSeasonStatusResponse ready = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            ready.SourceSeasonNumber.ShouldBe(2);
            ready.ExpectedCup.ShouldBe(CupExtensionPoint.TypeCup);
            ready.CupSelectionResolved.ShouldBeTrue();
            ready.CupTeamResolved.ShouldBeTrue();
            ready.CupComplete.ShouldBeTrue();
            ready.ReadyToStartNextSeason.ShouldBeTrue();
            ready.LegalNextActions.ShouldBe([SeasonLifecycleActions.StartNextSeason]);

            StartNextSeasonResponse started = await new StartNextSeasonHandler(store).HandleAsync(saveId);
            started.FromSeasonNumber.ShouldBe(2);
            started.ToSeasonNumber.ShouldBe(3);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task<GetSeasonStatusResponse> AssertPreDrawStatusAsync(SaveStore store, Guid saveId, int teamCount)
    {
        (ulong rngBefore, ulong streamBefore) = await LoadRngAsync(store, saveId).ConfigureAwait(false);

        GetSeasonStatusHandler status = new(store);
        GetSeasonStatusResponse first = await status.HandleAsync(saveId).ConfigureAwait(false);
        GetSeasonStatusResponse second = await status.HandleAsync(saveId).ConfigureAwait(false);

        first.SourceSeasonNumber.ShouldBe(2);
        first.ExpectedCup.ShouldBe(CupExtensionPoint.TypeCup);
        first.PersistedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.CupSelectionResolved));
        first.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.CupSelectionResolved));
        first.CupSelectionResolved.ShouldBeTrue();
        first.CupTeamResolved.ShouldBeFalse();
        first.CupComplete.ShouldBeFalse();
        first.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunTypeCupTeam]);
        first.EventProgress.ShouldNotBeNull();
        first.EventProgress!.Event.ShouldBe(PostseasonEvents.TypeCupTeam);
        first.EventProgress.SourceSeasonNumber.ShouldBe(2);
        first.EventProgress.RoundsPlayed.ShouldBe(0);

        RulesV1 rules = RulesV1.CreateDefault();
        int expectedGroups = TypeCupTournamentFormat.QualificationGroupCount(teamCount, rules);
        expectedGroups.ShouldBeGreaterThan(1);
        int expectedTotal = (expectedGroups + 1) * rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds;
        first.EventProgress.TotalRounds.ShouldBe(expectedTotal);
        first.EventProgress.QualificationGroupCount.ShouldBe(expectedGroups);
        first.EventProgress.TournamentPhase.ShouldBe((int)TypeCupTournamentFormat.TournamentPhase.Qualification);
        first.EventProgress.QualificationGroup.ShouldBe(1);
        first.EventProgress.Group.ShouldBe(1);
        first.EventProgress.RoundInGroup.ShouldBe(1);
        first.EventProgress.TournamentStage.ShouldBe($"Qualification Group 1 of {expectedGroups}");

        // Repeated reads are pure: identical snapshots, RNG untouched.
        second.LegalNextActions.ShouldBe(first.LegalNextActions);
        second.EventProgress.ShouldNotBeNull();
        second.EventProgress!.Event.ShouldBe(first.EventProgress!.Event);
        second.EventProgress.TotalRounds.ShouldBe(first.EventProgress.TotalRounds);
        second.EventProgress.RoundsPlayed.ShouldBe(first.EventProgress.RoundsPlayed);
        second.ComputedPhase.ShouldBe(first.ComputedPhase);
        second.PersistedPhase.ShouldBe(first.PersistedPhase);
        (ulong rngAfter, ulong streamAfter) = await LoadRngAsync(store, saveId).ConfigureAwait(false);
        rngAfter.ShouldBe(rngBefore);
        streamAfter.ShouldBe(streamBefore);
        return first;
    }

    private static async Task AssertDowngradeUpgradeAsync(SaveStore store, string root, Guid saveId, int teamCount)
    {
        string path = Path.Combine(root, $"{saveId:N}.db");

        // Rewind the file to the supported schema preceding the Type Cup
        // tournament migration, keeping every sporting row intact.
        SaveDbContextFactory factory = TestFactory(root);
        using (SaveDbContext context = factory.Create(path))
        {
            await context.Database.MigrateAsync("20261006130817_AddKindToMovementUniqueness").ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();

        // A fresh store (as after an app restart) must heal the schema through
        // the status read alone, without losing squads or inventing results.
        // Parallel first reads share one per-save migration gate in SaveStore,
        // so concurrent upgrades serialize checkpoint/migrate/rollback instead
        // of racing (MSS-070 P1).
        SaveStore upgraded = OpenStore(root);
        const int concurrentReads = 8;
        Task<GetSeasonStatusResponse>[] upgrades = Enumerable
            .Range(0, concurrentReads)
            .Select(_ => new GetSeasonStatusHandler(upgraded).HandleAsync(saveId))
            .ToArray();
        GetSeasonStatusResponse[] healedAll = await Task.WhenAll(upgrades).ConfigureAwait(false);
        foreach (GetSeasonStatusResponse healedEach in healedAll)
        {
            healedEach.SourceSeasonNumber.ShouldBe(2);
            healedEach.ExpectedCup.ShouldBe(CupExtensionPoint.TypeCup);
            healedEach.CupSelectionResolved.ShouldBeTrue();
            healedEach.CupTeamResolved.ShouldBeFalse();
            healedEach.CupComplete.ShouldBeFalse();
            healedEach.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunTypeCupTeam]);
            healedEach.EventProgress.ShouldNotBeNull();
            healedEach.EventProgress!.Event.ShouldBe(PostseasonEvents.TypeCupTeam);
        }

        GetSeasonStatusResponse healed = healedAll[0];

        int selections = await CountSelectedTeamsAsync(upgraded, saveId).ConfigureAwait(false);
        selections.ShouldBe(teamCount);
        await AssertNoDrawOrRoundsAsync(upgraded, saveId).ConfigureAwait(false);

        IReadOnlyList<string> pending = await SaveSchemaMigrator.GetPendingMigrationsAsync(
            TestFactory(root), path, CancellationToken.None).ConfigureAwait(false);
        pending.ShouldBeEmpty();
    }

    private static async Task<int> CountSelectedTeamsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        int selections = await context.TypeCupSelections.CountAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false);
        (selections % 4).ShouldBe(0);
        return selections / 4;
    }

    private static async Task AssertNoDrawOrRoundsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        (await context.TypeCupTournamentDraws.AnyAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false)).ShouldBeFalse();
        (await context.TypeCupTeamRounds.AnyAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false)).ShouldBeFalse();
        (await context.TypeCupTeamStandings.AnyAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false)).ShouldBeFalse();
    }

    private static async Task AssertDrawExistsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        (await context.TypeCupTournamentDraws.AnyAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false)).ShouldBeTrue();
    }

    private static async Task<(ulong State, ulong Stream)> LoadRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return ((ulong)rng.State, (ulong)rng.Stream);
    }

    private static SaveStore OpenStore(string root)
    {
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveStore store = new(options, environment, TestFactory(root), TimeProvider.System, NullLogger<SaveStore>.Instance);
        return store;
    }

    private static SaveDbContextFactory TestFactory(string root)
    {
        _ = root;
        return new SaveDbContextFactory(new SaveSqliteConnectionInterceptor());
    }

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
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
