namespace MtgSoloSports.SimulationKernel.FixedPoint;

/// <summary>
/// Sporting bonus in thousandths. 0.100 is stored as 100.
/// Integer-only storage keeps simulation deterministic across runtimes.
/// </summary>
public readonly record struct Bonus
{
    public Bonus(int thousandths)
    {
        if (thousandths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(thousandths), "Bonus cannot be negative.");
        }

        Thousandths = thousandths;
    }

    public int Thousandths { get; }

    public static Bonus Zero => new(0);

    public static Bonus FromThousandths(int thousandths) => new(thousandths);

    public static Bonus operator +(Bonus left, Bonus right)
    {
        checked
        {
            return new Bonus(left.Thousandths + right.Thousandths);
        }
    }

    public static Bonus operator -(Bonus left, Bonus right)
    {
        checked
        {
            return new Bonus(left.Thousandths - right.Thousandths);
        }
    }

    /// <summary>
    /// Scales a bonus by an integer multiplier (for example Superleague x2).
    /// </summary>
    public Bonus Scale(int multiplier)
    {
        if (multiplier < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), "Multiplier cannot be negative.");
        }

        checked
        {
            return new Bonus(Thousandths * multiplier);
        }
    }

    public override string ToString() => $"+{Thousandths / 1000}.{Thousandths % 1000:000}";
}
