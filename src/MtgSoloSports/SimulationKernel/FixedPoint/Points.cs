namespace MtgSoloSports.SimulationKernel.FixedPoint;

/// <summary>
/// Sporting points in thousandths. 77 points is stored as 77_000.
/// Integer-only storage keeps simulation deterministic across runtimes.
/// </summary>
public readonly record struct Points
{
    public Points(int thousandths)
    {
        if (thousandths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(thousandths), "Points cannot be negative.");
        }

        Thousandths = thousandths;
    }

    public int Thousandths { get; }

    public static Points Zero => new(0);

    public static Points FromPoints(int points)
    {
        checked
        {
            return new Points(points * 1000);
        }
    }

    public static Points FromThousandths(int thousandths) => new(thousandths);

    public int WholePoints => Thousandths / 1000;

    public int RemainderThousandths => Thousandths % 1000;

    public static Points operator +(Points left, Points right)
    {
        checked
        {
            return new Points(left.Thousandths + right.Thousandths);
        }
    }

    public static Points operator -(Points left, Points right)
    {
        checked
        {
            return new Points(left.Thousandths - right.Thousandths);
        }
    }

    public override string ToString() => $"{WholePoints}.{RemainderThousandths:000}";
}
