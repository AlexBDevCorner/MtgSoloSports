using System.Collections.Concurrent;

namespace MtgSoloSports.Features.Simulation.AdvanceRound;

/// <summary>
/// Simple in-process per-save lock so only one mutating simulation operation
/// runs per save at a time. Read-only queries never take this lock.
/// </summary>
public static class AdvanceRoundSaveLock
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    public static SemaphoreSlim Acquire(Guid saveId)
    {
        return Locks.GetOrAdd(saveId, static _ => new SemaphoreSlim(1, 1));
    }
}
