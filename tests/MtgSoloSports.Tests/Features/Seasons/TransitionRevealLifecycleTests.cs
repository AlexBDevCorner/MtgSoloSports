using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Superleague.GetAutomaticMovement;
using MtgSoloSports.Features.Superleague.GetInauguralRoster;
using MtgSoloSports.Features.Superleague.GetRebalanceResult;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Seasons;

/// <summary>
/// MSS-053 integration: promotion/relegation and feeder-rebalance results are
/// persisted exactly once by the manual lifecycle step, stay identical across
/// re-reads (reveal reload / back-forward navigation never reruns the sporting
/// action), and the next legal action is already visible for the Continue step.
/// Presentation work must not change these sporting results.
/// </summary>
public sealed class TransitionRevealLifecycleTests
{
    [Fact]
    public async Task AutomaticMovement_ExecuteOnce_ReloadIdentical_ContinueIsQualifier()
    {
        // MSS-067: shared Season 1 template, then the same lifecycle drain.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-transition-");
        try
        {
            await RunLifecycleToActionAsync(store, saveId, SeasonLifecycleActions.ResolveAutomaticMovement);

            GetAutomaticMovementHandler movementQuery = new(store);
            await Should.ThrowAsync<AutomaticMovementNotFoundException>(
                () => movementQuery.HandleAsync(saveId, fromSeasonNumber: 2));

            AdvanceToNextEventHandler advance = new(store);
            AdvanceToNextEventResponse executed = await advance.HandleAsync(saveId);
            executed.ExecutedAction.ShouldBe(SeasonLifecycleActions.ResolveAutomaticMovement);

            GetAutomaticMovementResponse first =
                await movementQuery.HandleAsync(saveId, fromSeasonNumber: 2);
            first.FromSeasonNumber.ShouldBe(2);
            first.MovementCount.ShouldBeGreaterThan(0);

            // Reloading the reveal re-reads the same persisted facts.
            GetAutomaticMovementResponse second =
                await movementQuery.HandleAsync(saveId, fromSeasonNumber: 2);
            CanonicalMovement(first).ShouldBe(CanonicalMovement(second));

            // Continuing exposes the qualifier without rerunning movement.
            GetSeasonStatusHandler status = new(store);
            GetSeasonStatusResponse after = await status.HandleAsync(saveId);
            after.MovementResolved.ShouldBeTrue();
            after.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunQualifier]);

            // The following lifecycle step is the qualifier, not a repeated movement.
            AdvanceToNextEventResponse next = await advance.HandleAsync(saveId);
            next.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunQualifier);

            GetAutomaticMovementResponse third =
                await movementQuery.HandleAsync(saveId, fromSeasonNumber: 2);
            CanonicalMovement(third).ShouldBe(CanonicalMovement(first));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rebalance_ExecuteOnce_ReloadIdentical_ContinueIsCupSelection()
    {
        // MSS-067: shared Season 1 template, then the same lifecycle drains.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-transition-");
        try
        {
            // Season 1 rebalances without a qualifier first; drive through the
            // Season 2 movement and qualifier so this covers the Season 2
            // rebalance (fromSeason 2) followed by Type Cup selection.
            await RunLifecycleToActionAsync(store, saveId, SeasonLifecycleActions.ResolveAutomaticMovement);
            AdvanceToNextEventHandler drain = new(store);
            (await drain.HandleAsync(saveId)).ExecutedAction.ShouldBe(SeasonLifecycleActions.ResolveAutomaticMovement);
            (await drain.HandleAsync(saveId)).ExecutedAction.ShouldBe(SeasonLifecycleActions.RunQualifier);
            await RunLifecycleToActionAsync(store, saveId, SeasonLifecycleActions.RebalanceFeeders);

            GetRebalanceResultHandler rebalanceQuery = new(store);
            await Should.ThrowAsync<RebalanceResultNotFoundException>(
                () => rebalanceQuery.HandleAsync(saveId, fromSeasonNumber: 2));

            AdvanceToNextEventHandler advance = new(store);
            AdvanceToNextEventResponse executed = await advance.HandleAsync(saveId);
            executed.ExecutedAction.ShouldBe(SeasonLifecycleActions.RebalanceFeeders);

            GetRebalanceResultResponse first =
                await rebalanceQuery.HandleAsync(saveId, fromSeasonNumber: 2);
            first.FromSeasonNumber.ShouldBe(2);
            first.MovementCount.ShouldBeGreaterThan(0);

            // Reloading the reveal re-reads the same persisted backend selections.
            GetRebalanceResultResponse second =
                await rebalanceQuery.HandleAsync(saveId, fromSeasonNumber: 2);
            CanonicalRebalance(first).ShouldBe(CanonicalRebalance(second));

            // Continuing exposes Cup selection without rerunning the rebalance.
            GetSeasonStatusHandler status = new(store);
            GetSeasonStatusResponse after = await status.HandleAsync(saveId);
            after.Rebalanced.ShouldBeTrue();
            after.LegalNextActions.ShouldBe([SeasonLifecycleActions.SelectTypeCup]);

            AdvanceToNextEventResponse next = await advance.HandleAsync(saveId);
            next.ExecutedAction.ShouldBe(SeasonLifecycleActions.SelectTypeCup);

            GetRebalanceResultResponse third =
                await rebalanceQuery.HandleAsync(saveId, fromSeasonNumber: 2);
            CanonicalRebalance(third).ShouldBe(CanonicalRebalance(first));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Inaugural_ExecuteOnce_ReloadIdentical_ContinueIsRebalance()
    {
        // MSS-067: shared Season 1 template, then the same lifecycle drain.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-transition-");
        try
        {
            await RunLifecycleToActionAsync(store, saveId, SeasonLifecycleActions.ResolveInauguralMovement);

            GetInauguralRosterHandler inauguralQuery = new(store);
            await Should.ThrowAsync<InauguralRosterNotFoundException>(
                () => inauguralQuery.HandleAsync(saveId));

            AdvanceToNextEventHandler advance = new(store);
            AdvanceToNextEventResponse executed = await advance.HandleAsync(saveId);
            executed.ExecutedAction.ShouldBe(SeasonLifecycleActions.ResolveInauguralMovement);

            GetInauguralRosterResponse first = await inauguralQuery.HandleAsync(saveId);
            first.Members.Count.ShouldBe(32);

            GetInauguralRosterResponse second = await inauguralQuery.HandleAsync(saveId);
            CanonicalInaugural(first).ShouldBe(CanonicalInaugural(second));

            GetSeasonStatusHandler status = new(store);
            GetSeasonStatusResponse after = await status.HandleAsync(saveId);
            after.MovementResolved.ShouldBeTrue();
            after.LegalNextActions.ShouldBe([SeasonLifecycleActions.RebalanceFeeders]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RunLifecycleToActionAsync(SaveStore store, Guid saveId, string action)
    {
        GetSeasonStatusHandler status = new(store);
        AdvanceToNextEventHandler advance = new(store);
        for (int guard = 0; guard < 300; guard++)
        {
            GetSeasonStatusResponse current = await status.HandleAsync(saveId).ConfigureAwait(false);
            if (current.LegalNextActions.Count == 1
                && string.Equals(current.LegalNextActions[0], action, StringComparison.Ordinal))
            {
                return;
            }

            await advance.HandleAsync(saveId).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Lifecycle never reached {action}.");
    }

    private static string CanonicalMovement(GetAutomaticMovementResponse response)
    {
        static string Member(AutomaticMovementMember m) =>
            $"{m.AthleteId}:{m.FromLeagueId}:{m.ToLeagueId}:{m.MovementKind}:{m.FromSeasonRank}";
        string Promoted() => string.Join(",", response.Promoted.Select(Member).OrderBy(s => s, StringComparer.Ordinal));
        string Relegated() => string.Join(",", response.Relegated.Select(Member).OrderBy(s => s, StringComparer.Ordinal));
        return $"{response.FromSeasonNumber}->{response.ToSeasonNumber}|{response.MovementCount}|P[{Promoted()}]|R[{Relegated()}]";
    }

    private static string CanonicalRebalance(GetRebalanceResultResponse response)
    {
        static string Member(RebalanceMovementMember m) =>
            $"{m.AthleteId}:{m.FromLeagueId}:{m.ToLeagueId}:{m.Kind}:{m.FromSeasonRank}";
        string Part(IEnumerable<RebalanceMovementMember> members) =>
            string.Join(",", members.Select(Member).OrderBy(s => s, StringComparer.Ordinal));
        return $"{response.FromSeasonNumber}->{response.ToSeasonNumber}|{response.MovementCount}|" +
            $"D[{Part(response.Departed)}]|R[{Part(response.Returned)}]|" +
            $"X[{Part(response.Displaced)}]|Y[{Part(response.Draws)}]";
    }

    private static string CanonicalInaugural(GetInauguralRosterResponse response)
    {
        return string.Join(
            ",",
            response.Members
                .Select(m => $"{m.AthleteId}:{m.FromLeagueId}:{m.FromSeasonRank}")
                .OrderBy(s => s, StringComparer.Ordinal));
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-transition-" + Guid.NewGuid().ToString("N"));
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
