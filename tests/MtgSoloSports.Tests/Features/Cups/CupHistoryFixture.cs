using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// One save simulated through three full seasons, built once for the Cup
/// history read tests: Color Cup after Seasons 1 and 3, Type Cup after Season 2.
/// Tests must only read from it.
/// </summary>
public sealed class CupHistoryFixture : IAsyncLifetime
{
    private string _root = string.Empty;

    public SaveStore Store { get; private set; } = null!;

    public Guid SaveId { get; private set; }

    public async Task InitializeAsync()
    {
        (Store, _root) = PostseasonTestSaves.CreateStore();
        SaveStore.CreationRecord created = await Store.CreateAsync("Cup History", 7171UL, 8282UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        SaveId = created.Detail.SaveId;
        SimulateSeasonsResponse simulated = await new SimulateSeasonsHandler(Store).HandleAsync(SaveId, new SimulateSeasonsRequest(3)).ConfigureAwait(false);
        simulated.SeasonsCompleted.ShouldBe(3);
    }

    public Task DisposeAsync()
    {
        PostseasonTestSaves.DeleteRoot(_root);
        return Task.CompletedTask;
    }
}
