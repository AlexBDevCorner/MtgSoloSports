using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Seasons;

/// <summary>
/// MSS-079: after Run remaining rounds completes a multi-group Type Cup,
/// season-status must hand off to StartNextSeason and duplicate runs must be
/// rejected without altering persisted state. Uses the lifecycle-valid
/// tournament path (Season 2 selection through AdvanceToNextEvent) so the
/// handoff assertions observe the same chain as the reported Season 4 save.
/// </summary>
public sealed class TypeCupCompletionHandoffTests
{
    [Fact]
    public async Task PartialThenBulk_CompletesCup_AndHandsOff()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await DrainToTypeCupSelectionAsync(store);
            int total = await ExpectedTotalAsync(store, saveId);

            PlayTypeCupTeamRoundHandler step = new(store);
            for (int i = 0; i < 10; i++)
            {
                await step.HandleAsync(saveId, sourceSeasonNumber: 2);
            }

            GetSeasonStatusResponse mid = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            mid.CupComplete.ShouldBeFalse();

            RunTypeCupTeamResponse completed = await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            completed.TeamCount.ShouldBe(32);

            await AssertCompletedHandoffAsync(store, saveId, total);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WhollyBulk_CompletesCup_AndHandsOff()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await DrainToTypeCupSelectionAsync(store);
            int total = await ExpectedTotalAsync(store, saveId);
            RunTypeCupTeamResponse completed = await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            completed.TeamCount.ShouldBe(32);

            await AssertCompletedHandoffAsync(store, saveId, total);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Rerun_AfterComplete_IsRejected_WithoutStateChange()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await DrainToTypeCupSelectionAsync(store);
            await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            CapturedState before = await CaptureAsync(store, saveId);

            RunTypeCupTeamConflictException conflict = await Should.ThrowAsync<RunTypeCupTeamConflictException>(
                () => new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2));
            conflict.Message.ShouldContain("already been resolved");

            await Should.ThrowAsync<RunTypeCupTeamConflictException>(
                () => new PlayTypeCupTeamRoundHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2));

            CapturedState after = await CaptureAsync(store, saveId);
            after.ShouldBe(before);

            GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            status.CupComplete.ShouldBeTrue();
            status.LegalNextActions.ShouldBe([SeasonLifecycleActions.StartNextSeason]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private sealed record CapturedState(string Rounds, string Teams, string Rng, string Phase, int Honours);

    private static async Task AssertCompletedHandoffAsync(SaveStore store, Guid saveId, int total)
    {
        GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        status.SourceSeasonNumber.ShouldBe(2);
        status.ExpectedCup.ShouldBe(CupExtensionPoint.TypeCup);
        status.CupSelectionResolved.ShouldBeTrue();
        status.CupTeamResolved.ShouldBeTrue();
        status.CupComplete.ShouldBeTrue();
        status.ReadyToStartNextSeason.ShouldBeTrue();
        status.LegalNextActions.ShouldBe([SeasonLifecycleActions.StartNextSeason]);
        status.EventProgress.ShouldBeNull();

        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        int played = await context.TypeCupTeamRounds.CountAsync(e => e.SourceSeasonId == season.Id).ConfigureAwait(false);
        played.ShouldBe(total);
        bool finalStandings = await context.TypeCupTeamStandings.AnyAsync(
            e => e.SourceSeasonId == season.Id && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final).ConfigureAwait(false);
        finalStandings.ShouldBeTrue();
        bool complete = await HistoryEventRows.IsCompleteAsync(context, season, PostseasonEvents.TypeCupTeam, CancellationToken.None).ConfigureAwait(false);
        complete.ShouldBeTrue();
    }

    private static async Task<int> ExpectedTotalAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, CancellationToken.None).ConfigureAwait(false);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        int selections = await context.TypeCupSelections.CountAsync(e => e.SourceSeasonId == season.Id).ConfigureAwait(false);
        int teamCount = selections / rules.TypeCupMinTeamSize;
        int groups = TypeCupTournamentFormat.QualificationGroupCount(teamCount, rules);
        return checked((groups + 1) * rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds);
    }

    private static async Task<CapturedState> CaptureAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<string> rounds = await context.TypeCupTeamRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == season.Id)
            .OrderBy(e => e.TournamentPhase).ThenBy(e => e.QualificationGroup).ThenBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber)
            .Select(e => $"{e.TournamentPhase}/{e.QualificationGroup}/{e.GroupNumber}/{e.RoundNumber}:{e.PayloadChecksum}")
            .ToListAsync().ConfigureAwait(false);
        List<string> teams = await context.TypeCupTeamStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == season.Id)
            .OrderBy(e => e.TournamentPhase).ThenBy(e => e.QualificationGroup).ThenBy(e => e.TeamRank)
            .Select(e => $"{e.TournamentPhase}/{e.QualificationGroup}:{e.CreatureType}:{e.TeamRank}:{e.TeamScoreThousandths}")
            .ToListAsync().ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        string phase = (await context.SaveMetadata.SingleAsync().ConfigureAwait(false)).Phase;
        int honours = await context.Honours.CountAsync(e => e.SeasonId == season.Id).ConfigureAwait(false);
        return new CapturedState(string.Join("|", rounds), string.Join("|", teams), $"{rng.State}:{rng.Stream}", phase, honours);
    }

    private static async Task<Guid> DrainToTypeCupSelectionAsync(SaveStore store)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            "MSS-079 Type Cup", 79079UL, 80079UL, BuildTournamentCatalog()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        GetSeasonStatusHandler status = new(store);
        AdvanceToNextEventHandler advance = new(store);
        for (int guard = 0; guard < 200; guard++)
        {
            GetSeasonStatusResponse current = await status.HandleAsync(saveId).ConfigureAwait(false);
            if (IsTypeCupSelection(current))
            {
                break;
            }

            await advance.HandleAsync(saveId).ConfigureAwait(false);
        }

        GetSeasonStatusResponse pending = await status.HandleAsync(saveId).ConfigureAwait(false);
        pending.LegalNextActions.ShouldBe([SeasonLifecycleActions.SelectTypeCup]);
        AdvanceToNextEventResponse selected = await advance.HandleAsync(saveId).ConfigureAwait(false);
        selected.ExecutedAction.ShouldBe(SeasonLifecycleActions.SelectTypeCup);
        return saveId;
    }

    private static bool IsTypeCupSelection(GetSeasonStatusResponse current)
    {
        return current.LegalNextActions.Count == 1
            && string.Equals(current.LegalNextActions[0], SeasonLifecycleActions.SelectTypeCup, StringComparison.Ordinal);
    }

    private static IReadOnlyList<CatalogAthlete> BuildTournamentCatalog()
    {
        const int types = 64;
        const int perColor = 4;
        List<CatalogAthlete> athletes = new(types * 8 * perColor);
        for (int type = 0; type < types; type++)
        {
            string typeName = $"MSS079-Type-{type:D2}";
            foreach (SportingColor color in Enum.GetValues<SportingColor>())
            {
                (IReadOnlyList<string> FrontColors, string ManaCost, bool IsArtifact) shape = color switch
                {
                    SportingColor.White => (["W"], "{W}", false),
                    SportingColor.Blue => (["U"], "{U}", false),
                    SportingColor.Black => (["B"], "{B}", false),
                    SportingColor.Red => (["R"], "{R}", false),
                    SportingColor.Green => (["G"], "{G}", false),
                    SportingColor.Multicolor => (["W", "U"], "{W}{U}", false),
                    SportingColor.Hybrid => (["W", "U"], "{W/U}", false),
                    _ => ([], "{4}", true),
                };
                for (int rep = 0; rep < perColor; rep++)
                {
                    athletes.Add(new CatalogAthlete(
                        $"MSS079 {typeName} {color} {rep:D2}",
                        color,
                        [typeName],
                        shape.IsArtifact,
                        false,
                        color == SportingColor.Hybrid,
                        shape.FrontColors,
                        shape.ManaCost,
                        $"Creature — {typeName}",
                        null,
                        "t79"));
                }
            }
        }

        return athletes;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-type079h-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
    }

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
