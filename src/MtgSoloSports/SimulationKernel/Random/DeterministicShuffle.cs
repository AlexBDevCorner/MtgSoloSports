namespace MtgSoloSports.SimulationKernel.Random;

/// <summary>
/// Deterministic Fisher-Yates shuffle consuming only <see cref="Pcg32V1"/>.
/// </summary>
public static class DeterministicShuffle
{
    public static void Shuffle<T>(IList<T> values, Pcg32V1 rng)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(rng);

        for (int i = values.Count - 1; i > 0; i--)
        {
            int j = rng.NextInt32(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    public static void Shuffle<T>(Span<T> values, Pcg32V1 rng)
    {
        ArgumentNullException.ThrowIfNull(rng);

        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = rng.NextInt32(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    public static T[] ShuffledCopy<T>(IReadOnlyList<T> values, Pcg32V1 rng)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(rng);

        T[] copy = new T[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            copy[i] = values[i];
        }

        Shuffle(copy.AsSpan(), rng);
        return copy;
    }
}
