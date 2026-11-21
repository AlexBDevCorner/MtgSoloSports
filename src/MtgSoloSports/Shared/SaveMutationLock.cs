using System.Collections.Concurrent;

namespace MtgSoloSports.Shared;

/// <summary>
/// In-process per-save mutation lock so only one mutating simulation
/// operation runs per save at a time. Read-only queries never take it.
/// Lives in Shared because it is a genuinely global concept: mutation
/// handlers in every feature and save file replacement in SaveStore
/// serialize on it, not just AdvanceRound where it originated.
/// </summary>
public static class SaveMutationLock
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    public static SemaphoreSlim Acquire(Guid saveId)
    {
        return Locks.GetOrAdd(saveId, static _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Weak eviction used by SaveStore after a save is deleted: removes the
    /// entry only when uncontended (no holder, no queued waiter); a
    /// contended entry stays and retries on the next delete, so the map is
    /// bounded by distinct live save ids rather than growing forever. Weak
    /// by design: an acquirer that fetched the semaphore but has not called
    /// Wait yet can race the removal and end up on a detached semaphore
    /// while a later acquirer gets a fresh one; accepted because this is a
    /// localhost single-user app and the race needs a delete concurrent
    /// with an in-flight acquirer for the same save id.
    /// </summary>
    internal static void EvictIfUncontended(Guid saveId)
        => EvictIfUncontended(Locks, saveId);

    /// <summary>
    /// Same uncontended-eviction rule for the other per-save semaphore
    /// gate maps (SaveStore migration/snapshot gates) so one implementation
    /// governs every per-save entry's lifecycle.
    /// </summary>
    internal static void EvictIfUncontended(ConcurrentDictionary<Guid, SemaphoreSlim> gates, Guid saveId)
    {
        if (gates.TryGetValue(saveId, out SemaphoreSlim? gate) && gate.CurrentCount == 1)
        {
            gates.TryRemove(saveId, out _);
        }
    }
}
