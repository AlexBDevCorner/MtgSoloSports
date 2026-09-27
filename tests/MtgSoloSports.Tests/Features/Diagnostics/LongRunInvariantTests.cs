using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Diagnostics.LongRunInvariants;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// MSS-031 structural invariant/property runs over many seeds and seasons:
/// league sizes, duplicates, bonus timing, qualifier counts, nationality
/// immutability and Cup rotation. Short partial-season runs cover the
/// per-stage shape quickly; one two-season run covers qualifier/Cups/rotation.
/// </summary>
public sealed class LongRunInvariantTests
{
    [Theory]
    [InlineData(111UL, 222UL)]
    [InlineData(333UL, 444UL)]
    [InlineData(555UL, 666UL)]
    public async Task FiveGlobalStages_StructuralShapeHolds(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                $"Invariants {seed}", seed, stream, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            CompleteStageForAllLeaguesHandler bulk = new(store);
            for (int stage = 0; stage < 5; stage++)
            {
                await bulk.HandleAsync(saveId);
            }

            ValidateLongRunInvariantsHandler handler = new(store);
            ValidateLongRunInvariantsResponse response = await handler.HandleAsync(saveId);
            response.Passed.ShouldBeTrue(
                string.Join("; ", response.Invariants.Where(i => !i.Passed).Select(i => $"{i.Name}: {i.Detail}")));
            response.Invariants.Count.ShouldBe(6);
            PassedByName(response, "league_sizes").ShouldBeTrue();
            PassedByName(response, "no_duplicates").ShouldBeTrue();
            PassedByName(response, "bonus_timing").ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoSeasons_QualifierCupsAndRotationHold()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Invariants Two Seasons", 4242UL, 777UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            SimulateSeasonsHandler sim = new(store);
            SimulateSeasonsResponse done = await sim.HandleAsync(saveId, new SimulateSeasonsRequest(2));
            done.SeasonsCompleted.ShouldBe(2);

            ValidateLongRunInvariantsHandler handler = new(store);
            ValidateLongRunInvariantsResponse response = await handler.HandleAsync(saveId);
            response.Passed.ShouldBeTrue(
                string.Join("; ", response.Invariants.Where(i => !i.Passed).Select(i => $"{i.Name}: {i.Detail}")));
            PassedByName(response, "qualifier_counts").ShouldBeTrue();
            PassedByName(response, "nationality_immutability").ShouldBeTrue();
            PassedByName(response, "cup_rotation").ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool PassedByName(ValidateLongRunInvariantsResponse response, string name)
    {
        return response.Invariants.Single(i => string.Equals(i.Name, name, StringComparison.Ordinal)).Passed;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-inv-" + Guid.NewGuid().ToString("N"));
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
