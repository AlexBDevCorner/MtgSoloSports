namespace MtgSoloSports.SimulationKernel.Leagues;

/// <summary>
/// Exact rational bonus scale for one competitive tier.
/// Stored as numerator/denominator (for example 1/2 for Feeder 2) so all
/// sporting arithmetic stays fixed-point integer-only with no
/// <c>double</c>/<c>float</c> drift.
/// The single deterministic truncation rule is: multiply first, then integer
/// divide, truncating toward zero (floor for non-negative bonus).
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly record struct TierBonusScale
{
    public TierBonusScale(int numerator, int denominator)
    {
        if (numerator < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator), "Bonus scale numerator cannot be negative.");
        }

        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "Bonus scale denominator must be positive.");
        }

        Numerator = numerator;
        Denominator = denominator;
    }

    public int Numerator { get; }

    public int Denominator { get; }

    /// <summary>
    /// Scales base bonus thousandths by this tier: (value * numerator) / denominator,
    /// truncated toward zero. Checked integer-only.
    /// </summary>
    public int ScaleThousandths(int baseThousandths)
    {
        if (baseThousandths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseThousandths), "Bonus cannot be negative.");
        }

        checked
        {
            return (int)(((long)baseThousandths * Numerator) / Denominator);
        }
    }

    public override string ToString() => $"{Numerator}/{Denominator}";
}
