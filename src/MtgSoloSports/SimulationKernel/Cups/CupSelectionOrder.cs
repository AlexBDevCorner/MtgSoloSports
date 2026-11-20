namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Single shared seven-field Cup selection comparator (consolidated in
/// MSS-151). Both Type Cup allocation and Color Cup selection
/// order candidates identically: final rating descending, then bonus,
/// performance, form, chaos and prestige normalized components descending,
/// then name ordinal ascending, then athlete id ascending. The comparator body
/// exists exactly once here; per-cup record types adapt through
/// <see cref="Key"/> so a comparator change can never drift between the two
/// selection paths. Pure ordinal fixed-point ordering: no RNG, clock,
/// double/float or database ordering.
/// </summary>
internal static class CupSelectionOrder
{
    /// <summary>
    /// The seven selection fields plus ordinal identity of one candidate.
    /// Field names mirror the per-cup <c>ScoredCandidate</c> records so the
    /// cascade below stays verbatim-readable.
    /// </summary>
    internal readonly record struct Key(
        int FinalRatingThousandths,
        int BonusNormThousandths,
        int PerformanceNormThousandths,
        int FormNormThousandths,
        int ChaosNormThousandths,
        int PrestigeNormThousandths,
        string Name,
        int AthleteId);

    internal static int Compare(Key left, Key right)
    {
        int final = right.FinalRatingThousandths.CompareTo(left.FinalRatingThousandths);
        if (final != 0)
        {
            return final;
        }

        int bonus = right.BonusNormThousandths.CompareTo(left.BonusNormThousandths);
        if (bonus != 0)
        {
            return bonus;
        }

        int perf = right.PerformanceNormThousandths.CompareTo(left.PerformanceNormThousandths);
        if (perf != 0)
        {
            return perf;
        }

        int form = right.FormNormThousandths.CompareTo(left.FormNormThousandths);
        if (form != 0)
        {
            return form;
        }

        int chaos = right.ChaosNormThousandths.CompareTo(left.ChaosNormThousandths);
        if (chaos != 0)
        {
            return chaos;
        }

        int prestige = right.PrestigeNormThousandths.CompareTo(left.PrestigeNormThousandths);
        if (prestige != 0)
        {
            return prestige;
        }

        int name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        if (name != 0)
        {
            return name;
        }

        return left.AthleteId.CompareTo(right.AthleteId);
    }
}
