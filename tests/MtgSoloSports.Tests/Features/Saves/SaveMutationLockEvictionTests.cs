using System.Collections.Concurrent;
using System.Reflection;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Saves;

/// <summary>
/// MSS-158 save-deletion eviction regressions: deleting a save must release
/// its uncontended per-save entries (mutation lock plus the SaveStore
/// migration/snapshot gates) so the maps stay bounded by live save ids, while
/// a contended entry (held semaphore) is retained and retries on the next
/// delete. Assertions are per save id, not global counts: the suite runs
/// classes in parallel and other tests legitimately keep entries for their
/// own live saves, so "this test's N deletes leaked zero entries" is the
/// deterministic form of "the entry count does not grow with N".
/// Fresh saves only; no seasons are simulated.
/// </summary>
public sealed class SaveMutationLockEvictionTests
{
    [Fact]
    public async Task DeleteAsync_EvictsUncontendedEntries_CountDoesNotGrowWithSaveCount()
    {
        const int saveCount = 5;
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-mss158-evict-");
        try
        {
            ConcurrentDictionary<Guid, SemaphoreSlim> mutationLocks = GetPrivateMap(typeof(SaveMutationLock), "Locks");
            ConcurrentDictionary<Guid, SemaphoreSlim> migrationGates = GetPrivateMap(typeof(SaveStore), "MigrationGates");
            ConcurrentDictionary<Guid, SemaphoreSlim> snapshotGates = GetPrivateMap(typeof(SaveStore), "SnapshotGates");

            List<Guid> saveIds = new(saveCount);
            for (int i = 0; i < saveCount; i++)
            {
                SaveStore.CreationRecord created = await store.CreateAsync(
                    $"Eviction{i}", 1901UL + (ulong)i, 2901UL + (ulong)i, UniverseTestCatalog.Build());
                Guid saveId = created.Detail.SaveId;
                saveIds.Add(saveId);

                // Seed the entries a save collects from earlier activity (one
                // mutation plus hot reads) so each delete provably removes a
                // real entry instead of asserting vacuous absence.
                _ = SaveMutationLock.Acquire(saveId);
                _ = AcquirePrivateGate(migrationGates, saveId);
                _ = AcquirePrivateGate(snapshotGates, saveId);
                mutationLocks.ContainsKey(saveId).ShouldBeTrue();
                migrationGates.ContainsKey(saveId).ShouldBeTrue();
                snapshotGates.ContainsKey(saveId).ShouldBeTrue();

                await store.DeleteAsync(saveId);

                mutationLocks.ContainsKey(saveId).ShouldBeFalse();
                migrationGates.ContainsKey(saveId).ShouldBeFalse();
                snapshotGates.ContainsKey(saveId).ShouldBeFalse();
            }

            // Entry count does not grow with N: this test's saves left zero entries.
            saveIds.Count(mutationLocks.ContainsKey).ShouldBe(0);
            saveIds.Count(migrationGates.ContainsKey).ShouldBe(0);
            saveIds.Count(snapshotGates.ContainsKey).ShouldBe(0);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task EvictIfUncontended_HeldEntry_IsRetained_ThenEvictedAfterRelease()
    {
        ConcurrentDictionary<Guid, SemaphoreSlim> mutationLocks = GetPrivateMap(typeof(SaveMutationLock), "Locks");
        Guid saveId = Guid.NewGuid();

        SemaphoreSlim held = SaveMutationLock.Acquire(saveId);
        await held.WaitAsync();
        try
        {
            SaveMutationLock.EvictIfUncontended(saveId);

            // A held semaphore (current holder or queued waiter) must keep its
            // entry; the next delete retries.
            mutationLocks.ContainsKey(saveId).ShouldBeTrue();
            SaveMutationLock.Acquire(saveId).ShouldBe(held);
        }
        finally
        {
            held.Release();
        }

        SaveMutationLock.EvictIfUncontended(saveId);
        mutationLocks.ContainsKey(saveId).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteAsync_BlockedByHeldGate_RetainsEntries_NextDeleteEvicts()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-mss158-blocked-");
        SemaphoreSlim? heldGate = null;
        bool gateHeld = false;
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("BlockedDelete", 1951UL, 2951UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            ConcurrentDictionary<Guid, SemaphoreSlim> migrationGates = GetPrivateMap(typeof(SaveStore), "MigrationGates");
            ConcurrentDictionary<Guid, SemaphoreSlim> snapshotGates = GetPrivateMap(typeof(SaveStore), "SnapshotGates");
            heldGate = AcquirePrivateGate(snapshotGates, saveId);
            await heldGate.WaitAsync();
            gateHeld = true;

            // The delete blocks on the held snapshot gate; cancellation must
            // leave every entry in place (retry happens on the next delete).
            using CancellationTokenSource cts = new();
            Task deleteTask = store.DeleteAsync(saveId, cts.Token);
            await WaitUntilAsync(
                () => AcquirePrivateGate(migrationGates, saveId).CurrentCount == 0 && !deleteTask.IsCompleted,
                TimeSpan.FromSeconds(5),
                "DeleteAsync did not acquire the migration gate.");
            cts.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => deleteTask);

            GetPrivateMap(typeof(SaveMutationLock), "Locks").ContainsKey(saveId).ShouldBeTrue();
            migrationGates.ContainsKey(saveId).ShouldBeTrue();
            snapshotGates.ContainsKey(saveId).ShouldBeTrue();

            heldGate.Release();
            gateHeld = false;

            // Retry on the next delete: the uncontended delete evicts all three.
            await store.DeleteAsync(saveId);
            GetPrivateMap(typeof(SaveMutationLock), "Locks").ContainsKey(saveId).ShouldBeFalse();
            migrationGates.ContainsKey(saveId).ShouldBeFalse();
            snapshotGates.ContainsKey(saveId).ShouldBeFalse();
            File.Exists(store.GetSaveFilePath(saveId)).ShouldBeFalse();
        }
        finally
        {
            if (gateHeld && heldGate is not null)
            {
                try
                {
                    heldGate.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }

            TestSaveStores.DeleteRoot(root);
        }
    }

    private static ConcurrentDictionary<Guid, SemaphoreSlim> GetPrivateMap(Type declaringType, string fieldName)
    {
        FieldInfo? field = declaringType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        field.ShouldNotBeNull($"{declaringType.Name}.{fieldName} map must exist.");
        return (ConcurrentDictionary<Guid, SemaphoreSlim>)field.GetValue(null)!;
    }

    private static SemaphoreSlim AcquirePrivateGate(ConcurrentDictionary<Guid, SemaphoreSlim> gates, Guid saveId)
    {
        return gates.GetOrAdd(saveId, static _ => new SemaphoreSlim(1, 1));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new Xunit.Sdk.XunitException(message);
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }
}
