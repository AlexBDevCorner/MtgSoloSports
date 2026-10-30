using System.Collections.Concurrent;

namespace MtgSoloSports.Tests.Features.Cups;

internal static class ColorCupActiveFixtures
{
    private static readonly ConcurrentDictionary<Guid, (Dictionary<int, List<int>> Active, Dictionary<int, List<int>> Pool)> Saved = new();
    private static readonly ConcurrentDictionary<Guid, (List<int> Low, List<int> High)> F3Saved = new();
    private static readonly ConcurrentDictionary<int, bool> HistoricalPool = new();

    internal static void Remember(Guid saveId, Dictionary<int, List<int>> active, Dictionary<int, List<int>> pool)
    {
        Saved[saveId] = (active, pool);
    }

    internal static (Dictionary<int, List<int>> Active, Dictionary<int, List<int>> Pool) Lookup(Guid saveId)
    {
        return Saved[saveId];
    }

    internal static void RememberF3(Guid saveId, List<int> low, List<int> high)
    {
        Saved.TryGetValue(saveId, out _);
        F3Saved[saveId] = (low, high);
    }

    internal static (List<int> Low, List<int> High) LookupF3(Guid saveId)
    {
        return F3Saved[saveId];
    }

    internal static void RememberHistorical(List<int> poolIds)
    {
        foreach (int id in poolIds)
        {
            HistoricalPool[id] = true;
        }
    }

    internal static bool IsHistoricalPool(int athleteId)
    {
        return HistoricalPool.ContainsKey(athleteId);
    }
}
