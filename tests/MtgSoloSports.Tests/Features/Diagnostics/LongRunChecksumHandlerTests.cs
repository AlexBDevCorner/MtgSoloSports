using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Diagnostics.LongRunChecksum;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// Proves the stable checksum detects sporting divergence without storing
/// full datasets: identical seeds hash identically, different seeds differ,
/// and bulk fast simulation hashes identically to manual progression.
/// </summary>
public sealed class LongRunChecksumHandlerTests
{
    [Fact]
    public async Task SameSeed_SameChecksum_DifferentSeed_Differs()
    {
        // MSS-067: same-seed copy forked instead of creating the same universe twice.
        var (firstStore, firstRoot) = CreateStore();
        try
        {
            var catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord first = await firstStore.CreateAsync("Checksum A", 9001UL, 9002UL, catalog);
            var (secondStore, secondRoot, secondId) = await TestSaveStores.ForkAsync(firstStore, first.Detail.SaveId, "mtgsolosports-ck-");
            var (thirdStore, thirdRoot) = CreateStore();
            try
            {
                SaveStore.CreationRecord third = await thirdStore.CreateAsync("Checksum C", 7001UL, 7002UL, catalog);

                CompleteStageForAllLeaguesHandler bulkFirst = new(firstStore);
                CompleteStageForAllLeaguesHandler bulkSecond = new(secondStore);
                CompleteStageForAllLeaguesHandler bulkThird = new(thirdStore);
                for (int stage = 0; stage < 3; stage++)
                {
                    await bulkFirst.HandleAsync(first.Detail.SaveId);
                    await bulkSecond.HandleAsync(secondId);
                    await bulkThird.HandleAsync(third.Detail.SaveId);
                }

                GetLongRunChecksumResponse a = await new GetLongRunChecksumHandler(firstStore).HandleAsync(first.Detail.SaveId);
                GetLongRunChecksumResponse b = await new GetLongRunChecksumHandler(secondStore).HandleAsync(secondId);
                GetLongRunChecksumResponse c = await new GetLongRunChecksumHandler(thirdStore).HandleAsync(third.Detail.SaveId);

                string.Equals(a.Checksum, b.Checksum, StringComparison.Ordinal).ShouldBeTrue();
                a.SeasonChecksums.Select(e => e.Checksum).ToList().ShouldBe(b.SeasonChecksums.Select(e => e.Checksum).ToList());
                string.Equals(c.Checksum, a.Checksum, StringComparison.Ordinal).ShouldBeFalse();
                a.TotalRounds.ShouldBeGreaterThan(0);
                a.TotalStageStandings.ShouldBeGreaterThan(0);
            }
            finally
            {
                TestSaveStores.DeleteRoot(secondRoot);
                Directory.Delete(thirdRoot, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
        }
    }

    [Fact]
    public async Task FastSimulation_MatchesManual_Checksum()
    {
        // MSS-067: second save forked instead of creating the same universe twice.
        var (firstStore, firstRoot) = CreateStore();
        try
        {
            var catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord manual = await firstStore.CreateAsync("Checksum Manual", 5150UL, 6160UL, catalog);
            var (secondStore, secondRoot, fastId) = await TestSaveStores.ForkAsync(firstStore, manual.Detail.SaveId, "mtgsolosports-ck-fast-");
            try
            {
                CompleteStageForAllLeaguesHandler bulk = new(firstStore);
                for (int stage = 1; stage <= 32; stage++)
                {
                    await bulk.HandleAsync(manual.Detail.SaveId);
                }

                MtgSoloSports.Features.Simulation.CompleteSeason.CompleteSeasonHandler fastHandler = new(secondStore);
                await fastHandler.HandleAsync(fastId);

                GetLongRunChecksumResponse m = await new GetLongRunChecksumHandler(firstStore).HandleAsync(manual.Detail.SaveId);
                GetLongRunChecksumResponse f = await new GetLongRunChecksumHandler(secondStore).HandleAsync(fastId);
                string.Equals(f.Checksum, m.Checksum, StringComparison.Ordinal).ShouldBeTrue();
            }
            finally
            {
                TestSaveStores.DeleteRoot(secondRoot);
            }
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-ck-" + Guid.NewGuid().ToString("N"));
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
