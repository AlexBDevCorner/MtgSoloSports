using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.AdvanceChaosCupStage;

/// <summary>
/// Pure deterministic next-stage Chaos Cup draw (MSS-102).
/// Advancers are canonically ordered (name ordinal, then athlete id) before the
/// single versioned-RNG shuffle so mixed group completion order yields identical
/// draws and database ordering never affects the outcome. The shuffled list is
/// chunked contiguously into 32-athlete groups in group-number order, giving a
/// pure random composition with no color, tier or seeding constraints. Stage 0
/// keeps its balanced 4/color/group first draw; this helper covers stages 1..5
/// only. All methods consume only the caller-supplied <see cref="Pcg32V1"/>;
/// the caller persists <c>rng.Snapshot()</c> in the same transaction as the draws.
/// </summary>
public static class AdvanceChaosCupStageSelection
{
    public sealed record StageAdvancer(int SaveAthleteId, string Name);

    /// <summary>
    /// Draws the next stage: canonically orders advancers, shuffles once with
    /// <paramref name="rng"/>, then chunks into 32-athlete groups.
    /// Returns groups in group-number order with draw order preserved.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<StageAdvancer>> DrawRandomStage(
        IReadOnlyList<StageAdvancer> advancers,
        Pcg32V1 rng,
        int stageIndex)
    {
        ArgumentNullException.ThrowIfNull(advancers);
        ArgumentNullException.ThrowIfNull(rng);
        if (stageIndex < 1 || stageIndex >= ChaosCupFormat.StageCount)
        {
            throw new InvalidOperationException($"Chaos Cup random draw covers stages 1..{ChaosCupFormat.StageCount - 1}, was {stageIndex}.");
        }

        int expected = ChaosCupFormat.EntrantCountForStage(stageIndex);
        if (advancers.Count != expected)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} must draw exactly {expected} advancers, was {advancers.Count}.");
        }

        List<StageAdvancer> ordered = advancers
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ThenBy(a => a.SaveAthleteId)
            .ToList();
        EnsureUnique(ordered, stageIndex);

        List<StageAdvancer> shuffled = [.. ordered];
        DeterministicShuffle.Shuffle(shuffled, rng);

        int groupCount = ChaosCupFormat.GroupCountForStage(stageIndex);
        List<IReadOnlyList<StageAdvancer>> groups = new(groupCount);
        for (int group = 0; group < groupCount; group++)
        {
            int offset = checked(group * ChaosCupFormat.GroupSize);
            groups.Add(shuffled.GetRange(offset, ChaosCupFormat.GroupSize));
        }

        return groups;
    }

    private static void EnsureUnique(List<StageAdvancer> ordered, int stageIndex)
    {
        HashSet<int> seen = new(ordered.Count);
        foreach (StageAdvancer advancer in ordered)
        {
            if (advancer.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} advancer has corrupt athlete id {advancer.SaveAthleteId}.");
            }

            if (string.IsNullOrWhiteSpace(advancer.Name))
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} advancer {advancer.SaveAthleteId} has an empty name.");
            }

            if (!seen.Add(advancer.SaveAthleteId))
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} assigns athlete {advancer.SaveAthleteId} more than once.");
            }
        }
    }
}
