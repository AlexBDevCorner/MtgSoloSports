namespace MtgSoloSports.SimulationKernel.FixedPoint;

/// <summary>
/// Weighted Cup selection score in thousandths, computed with integer arithmetic only.
/// Each normalized component is expressed in thousandths (0..1000).
/// </summary>
public readonly record struct SelectionScore
{
    public SelectionScore(int thousandths)
    {
        if (thousandths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(thousandths), "Selection score cannot be negative.");
        }

        Thousandths = thousandths;
    }

    public int Thousandths { get; }

    public static SelectionScore Zero => new(0);

    public static SelectionScore FromThousandths(int thousandths) => new(thousandths);

    /// <summary>
    /// Combines normalized components with integer permille weights.
    /// Result is (bonusNorm * bonusWeight + perfNorm * perfWeight + formNorm * formWeight + prestigeNorm * prestigeWeight) / 1000.
    /// </summary>
    public static SelectionScore Combine(
        int bonusNormThousandths,
        int performanceNormThousandths,
        int formNormThousandths,
        int prestigeNormThousandths,
        int bonusWeightPermille,
        int performanceWeightPermille,
        int formWeightPermille,
        int prestigeWeightPermille)
    {
        if (bonusNormThousandths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bonusNormThousandths), "Normalized components cannot be negative.");
        }

        if (performanceNormThousandths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(performanceNormThousandths), "Normalized components cannot be negative.");
        }

        if (formNormThousandths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(formNormThousandths), "Normalized components cannot be negative.");
        }

        if (prestigeNormThousandths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prestigeNormThousandths), "Normalized components cannot be negative.");
        }

        if (bonusWeightPermille < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bonusWeightPermille), "Weights cannot be negative.");
        }

        if (performanceWeightPermille < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(performanceWeightPermille), "Weights cannot be negative.");
        }

        if (formWeightPermille < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(formWeightPermille), "Weights cannot be negative.");
        }

        if (prestigeWeightPermille < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prestigeWeightPermille), "Weights cannot be negative.");
        }

        checked
        {
            long total = ((long)bonusNormThousandths * bonusWeightPermille)
                + ((long)performanceNormThousandths * performanceWeightPermille)
                + ((long)formNormThousandths * formWeightPermille)
                + ((long)prestigeNormThousandths * prestigeWeightPermille);
            return new SelectionScore((int)(total / 1000L));
        }
    }

    public static bool operator <(SelectionScore left, SelectionScore right) => left.Thousandths < right.Thousandths;

    public static bool operator >(SelectionScore left, SelectionScore right) => left.Thousandths > right.Thousandths;

    public static bool operator <=(SelectionScore left, SelectionScore right) => left.Thousandths <= right.Thousandths;

    public static bool operator >=(SelectionScore left, SelectionScore right) => left.Thousandths >= right.Thousandths;
}
