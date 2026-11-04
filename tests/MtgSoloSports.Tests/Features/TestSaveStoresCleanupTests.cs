using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features;

/// <summary>
/// MSS-131: fixture cleanup must release pooled SQLite handles before Windows
/// deletes the temp directory, without masking the test failure or leaking roots.
/// Fresh saves only; no seasons are simulated.
/// </summary>
public sealed class TestSaveStoresCleanupTests
{
    [Fact]
    public async Task DeleteRoot_ReleasesPooledHandles_AfterReads()
    {
        // Pooled reads dispose their contexts but keep idle handles; DeleteRoot owns the release.
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-cleanup-");
        SaveStore.CreationRecord created = await store.CreateAsync("Cleanup", 9101UL, 9102UL, UniverseTestCatalog.Build());
        using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
        {
            (await context.SaveAthletes.CountAsync()).ShouldBeGreaterThan(0);
        }

        TestSaveStores.DeleteRoot(root);
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact]
    public void DeleteRoot_MissingRoot_IsNoop()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-cleanup-missing-" + Guid.NewGuid().ToString("N"));
        Directory.Exists(root).ShouldBeFalse();

        TestSaveStores.DeleteRoot(root);
        TestSaveStores.DeleteRoot(root);
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteRoot_IsolatedRoots_DoNotInterfere()
    {
        var (firstStore, firstRoot) = TestSaveStores.CreateStore("mtgsolosports-cleanup-a-");
        var (secondStore, secondRoot) = TestSaveStores.CreateStore("mtgsolosports-cleanup-b-");
        try
        {
            SaveStore.CreationRecord first = await firstStore.CreateAsync("First", 9201UL, 9202UL, UniverseTestCatalog.Build());
            SaveStore.CreationRecord second = await secondStore.CreateAsync("Second", 9301UL, 9302UL, UniverseTestCatalog.Build());
            first.Detail.SaveId.ShouldNotBe(second.Detail.SaveId);
        }
        finally
        {
            TestSaveStores.DeleteRoot(firstRoot);
        }

        Directory.Exists(firstRoot).ShouldBeFalse();
        Directory.Exists(secondRoot).ShouldBeTrue();
        TestSaveStores.DeleteRoot(secondRoot);
        Directory.Exists(secondRoot).ShouldBeFalse();
    }
}
