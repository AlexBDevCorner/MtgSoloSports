using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Simulation.AdvanceRound;

/// <summary>
/// Structural invariants for a single simulated round. Fundamental failures
/// throw and abort the mutation; corrupted sporting state is never silently repaired.
/// </summary>
public static class RoundInvariants
{
    /// <summary>
    /// Validates a freshly simulated round before commit: exact 32 placements,
    /// positions 1..32 exactly once, unique athletes, base points matching the
    /// snapshot scoring table, final points matching base plus stage-start
    /// active bonus, cumulative totals chaining, and RNG advancement.
    /// </summary>
    public static void ValidateSimulation(
        RoundPayloadDocument payload,
        RulesV1 rules,
        ulong rngBeforeState,
        ulong rngBeforeStream)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(rules);

        if (payload.Version != RoundPayloadDocument.PayloadVersion)
        {
            throw new InvalidOperationException($"Round payload version must be {RoundPayloadDocument.PayloadVersion}, was {payload.Version}.");
        }

        if (payload.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Round payload rules version must be {rules.Version}, was {payload.RulesVersion}.");
        }

        if (payload.SeasonNumber < 1)
        {
            throw new InvalidOperationException($"Round payload season must be >= 1, was {payload.SeasonNumber}.");
        }

        if (payload.StageNumber < 1 || payload.StageNumber > rules.StagesPerSeason)
        {
            throw new InvalidOperationException($"Round payload stage must be 1..{rules.StagesPerSeason}, was {payload.StageNumber}.");
        }

        if (payload.RoundNumber < 1 || payload.RoundNumber > rules.RoundsPerStage)
        {
            throw new InvalidOperationException($"Round payload round must be 1..{rules.RoundsPerStage}, was {payload.RoundNumber}.");
        }

        if (payload.Placements.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException($"Round payload must contain exactly {rules.LeagueSize} placements, was {payload.Placements.Count}.");
        }

        if (payload.RngBeforeState != rngBeforeState || payload.RngBeforeStream != rngBeforeStream)
        {
            throw new InvalidOperationException("Round payload RNG-before does not match the persisted save RNG.");
        }

        if (payload.RngAfterState == rngBeforeState && payload.RngAfterStream == rngBeforeStream)
        {
            throw new InvalidOperationException("Round simulation must advance the save RNG.");
        }

        EnsurePlacements(payload, rules);
    }

    private static void EnsurePlacements(RoundPayloadDocument payload, RulesV1 rules)
    {
        PlacementAccumulator accumulator = new();
        foreach (RoundPayloadEntry entry in payload.Placements)
        {
            CheckPlacementIdentity(entry, accumulator);
            CheckPlacementPoints(entry, rules);
            CheckPlacementRanks(entry, rules);
        }

        if (!accumulator.Positions.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
        {
            throw new InvalidOperationException("Round payload must cover positions 1..32 exactly once.");
        }

        EnsureRankConsistency(payload);
    }

    private sealed class PlacementAccumulator
    {
        public HashSet<int> Positions { get; } = new();

        public HashSet<int> AthleteIds { get; } = new();

        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
    }

    private static void CheckPlacementIdentity(RoundPayloadEntry entry, PlacementAccumulator accumulator)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Round payload contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Round payload contains an athlete with an empty name.");
        }

        if (!accumulator.Positions.Add(entry.Position))
        {
            throw new InvalidOperationException($"Round payload contains duplicate position {entry.Position}.");
        }

        if (!accumulator.AthleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Round payload contains duplicate athlete id {entry.AthleteId}.");
        }

        if (!accumulator.Names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Round payload contains duplicate athlete '{entry.Name}'.");
        }
    }

    private static void CheckPlacementPoints(RoundPayloadEntry entry, RulesV1 rules)
    {
        if (entry.Position < 1 || entry.Position > rules.LeagueSize)
        {
            throw new InvalidOperationException($"Round payload position {entry.Position} is out of range.");
        }

        int expectedBase = rules.ScoringTable[entry.Position - 1] * RulesV1.FixedScale;
        if (entry.BaseThousandths != expectedBase)
        {
            throw new InvalidOperationException($"Round payload base points for position {entry.Position} must be {expectedBase}, was {entry.BaseThousandths}.");
        }

        if (entry.ActiveBonusThousandths < 0)
        {
            throw new InvalidOperationException($"Round payload active bonus for '{entry.Name}' cannot be negative.");
        }

        long expectedFinal = (long)entry.BaseThousandths * (RulesV1.BonusPercentScale + entry.ActiveBonusThousandths) / RulesV1.BonusPercentScale;
        if (entry.FinalThousandths != expectedFinal)
        {
            throw new InvalidOperationException($"Round payload final points for '{entry.Name}' must be {expectedFinal}, was {entry.FinalThousandths}.");
        }

        long expectedAfter = (long)entry.CumulativeBeforeThousandths + entry.FinalThousandths;
        if (entry.CumulativeAfterThousandths != expectedAfter)
        {
            throw new InvalidOperationException($"Round payload cumulative total for '{entry.Name}' is corrupt.");
        }
    }

    private static void CheckPlacementRanks(RoundPayloadEntry entry, RulesV1 rules)
    {
        if (entry.RankBefore < 1 || entry.RankBefore > rules.LeagueSize)
        {
            throw new InvalidOperationException($"Round payload rank-before for '{entry.Name}' is out of range.");
        }

        if (entry.RankAfter < 1 || entry.RankAfter > rules.LeagueSize)
        {
            throw new InvalidOperationException($"Round payload rank-after for '{entry.Name}' is out of range.");
        }

        if (entry.RankMovement != entry.RankBefore - entry.RankAfter)
        {
            throw new InvalidOperationException($"Round payload rank movement for '{entry.Name}' is corrupt.");
        }
    }

    private static void EnsureRankConsistency(RoundPayloadDocument payload)
    {
        List<RoundPayloadEntry> byBefore = payload.Placements.OrderBy(e => e.RankBefore).ToList();
        List<RoundPayloadEntry> byAfter = payload.Placements.OrderBy(e => e.RankAfter).ToList();

        for (int i = 0; i < byBefore.Count; i++)
        {
            if (byBefore[i].RankBefore != i + 1)
            {
                throw new InvalidOperationException("Round payload rank-before sequence is corrupt.");
            }
        }

        for (int i = 0; i < byAfter.Count; i++)
        {
            if (byAfter[i].RankAfter != i + 1)
            {
                throw new InvalidOperationException("Round payload rank-after sequence is corrupt.");
            }
        }

        for (int i = 1; i < byBefore.Count; i++)
        {
            bool ordered = byBefore[i - 1].CumulativeBeforeThousandths > byBefore[i].CumulativeBeforeThousandths ||
                (byBefore[i - 1].CumulativeBeforeThousandths == byBefore[i].CumulativeBeforeThousandths &&
                 string.Compare(byBefore[i - 1].Name, byBefore[i].Name, StringComparison.Ordinal) < 0);
            if (!ordered)
            {
                throw new InvalidOperationException("Round payload rank-before order contradicts cumulative-before totals.");
            }
        }

        for (int i = 1; i < byAfter.Count; i++)
        {
            bool ordered = byAfter[i - 1].CumulativeAfterThousandths > byAfter[i].CumulativeAfterThousandths ||
                (byAfter[i - 1].CumulativeAfterThousandths == byAfter[i].CumulativeAfterThousandths &&
                 string.Compare(byAfter[i - 1].Name, byAfter[i].Name, StringComparison.Ordinal) < 0);
            if (!ordered)
            {
                throw new InvalidOperationException("Round payload rank-after order contradicts cumulative-after totals.");
            }
        }
    }
}
