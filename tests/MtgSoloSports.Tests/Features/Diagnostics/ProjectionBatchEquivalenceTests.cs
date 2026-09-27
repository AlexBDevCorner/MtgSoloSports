using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// Proves the batched projection refresh writes rows consistent with the
/// normalized standings: career totals match the sums of the athlete's stage
/// standings and the profile query serves them without payload scans.
/// </summary>
public sealed class ProjectionBatchEquivalenceTests
{
    [Fact]
    public async Task BatchedRefresh_CareerMatchesStageSums()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Batch Equivalence", 4242UL, 8484UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            CompleteStageForAllLeaguesHandler bulk = new(store);
            await bulk.HandleAsync(saveId);
            await bulk.HandleAsync(saveId);

            GetAthleteProfileHandler profiles = new(store);
            using SaveDbContext context = store.OpenDbContext(saveId);
            List<int> athleteIds = await context.SeasonMemberships
                .AsNoTracking()
                .Where(e => e.LeagueId != null)
                .Select(e => e.SaveAthleteId)
                .Distinct()
                .Take(8)
                .ToListAsync();

            foreach (int athleteId in athleteIds)
            {
                GetAthleteProfileResponse profile = await profiles.HandleAsync(saveId, athleteId);
                List<StageStandingEntity> rows = await context.StageStandings
                    .AsNoTracking()
                    .Where(e => e.SaveAthleteId == athleteId)
                    .ToListAsync();
                int expectedRoundWins = rows.Sum(e => e.RoundWins);
                int expectedEarned = rows.Sum(e => e.EarnedBonusThousandths);
                profile.Career.RoundWins.ShouldBe(expectedRoundWins);
                profile.Career.LifetimeEarnedBonusThousandths.ShouldBe(expectedEarned);
                profile.Career.SeasonsActive.ShouldBeGreaterThan(0);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-batch-" + Guid.NewGuid().ToString("N"));
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
