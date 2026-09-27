using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// Opt-in benchmark/performance harness. Skipped in normal CI: set
/// <c>MTG_LONGRUN=1</c> to simulate <c>MTG_LONGRUN_SEASONS</c> seasons
/// (default 5 smoke; 100 routine; 1000 stress outside CI time limits).
/// Without the flag this test only validates harness bounds quickly.
/// </summary>
public sealed class LongRunBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public LongRunBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task OptIn_LongRun_SimulatesAndReports()
    {
        string? flag = Environment.GetEnvironmentVariable("MTG_LONGRUN");
        if (!string.Equals(flag, "1", StringComparison.Ordinal))
        {
            SimulateSeasonsHandler.MaxSeasonsPerRequest.ShouldBeGreaterThanOrEqualTo(200);
            LongRunBenchmarkHarness.ReadFromEnvironmentOrDefault(5, 1UL, 2UL).Seasons.ShouldBe(5);
            return;
        }

        LongRunBenchmarkHarness.Options options =
            LongRunBenchmarkHarness.ReadFromEnvironmentOrDefault(5, 4242UL, 777UL);
        options.Seasons.ShouldBeGreaterThanOrEqualTo(1);
        options.Seasons.ShouldBeLessThanOrEqualTo(1000);

        var (store, root) = CreateStore();
        try
        {
            LongRunBenchmarkHarness.Report report = await LongRunBenchmarkHarness.RunAsync(
                store, UniverseTestCatalog.Build(), options);
            report.SeasonsCompleted.ShouldBe(options.Seasons);
            report.TotalSimulation.ShouldBeGreaterThan(TimeSpan.Zero);
            report.DatabaseBytes.ShouldBeGreaterThan(0);
            report.Rounds.ShouldBeGreaterThan(0);
            report.InvariantsPassed.ShouldBeTrue();
            _output.WriteLine(
                $"seasons={report.SeasonsCompleted} total={report.TotalSimulation} dbBytes={report.DatabaseBytes} " +
                $"rounds={report.Rounds} stageStandings={report.StageStandings} " +
                $"status={report.StatusLatency} standings={report.StandingsLatency} profile={report.ProfileLatency} " +
                $"records={report.RecordsLatency} replay={report.ReplayLatency} managed={report.ManagedMemoryBytes} " +
                $"checksum={report.Checksum}");
            foreach (LongRunBenchmarkHarness.SeasonTiming timing in report.SeasonTimings.Take(10))
            {
                _output.WriteLine($"season {timing.SeasonNumber}: {timing.Elapsed}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-bench-" + Guid.NewGuid().ToString("N"));
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
