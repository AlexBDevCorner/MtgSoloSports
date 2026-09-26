using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Single-row table (Id always 1) holding versioned RNG state.
/// SQLite INTEGER is signed 64-bit, so ulong bits are stored unchecked in long columns.
/// Future simulation mutations update this row in the same transaction as results.
/// </summary>
public sealed class RngStateEntity
{
    public int Id { get; set; }

    public string Algorithm { get; set; } = string.Empty;

    public int AlgorithmVersion { get; set; }

    public long State { get; set; }

    public long Stream { get; set; }

    public static RngStateEntity FromState(Pcg32State state)
    {
        return new RngStateEntity
        {
            Id = 1,
            Algorithm = Pcg32V1.AlgorithmName,
            AlgorithmVersion = Pcg32V1.AlgorithmVersion,
            State = unchecked((long)state.State),
            Stream = unchecked((long)state.Stream),
        };
    }

    public Pcg32State ToState()
    {
        if (!string.Equals(Algorithm, Pcg32V1.AlgorithmName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported RNG algorithm '{Algorithm}'.");
        }

        if (AlgorithmVersion != Pcg32V1.AlgorithmVersion)
        {
            throw new InvalidOperationException($"Unsupported RNG version {AlgorithmVersion}.");
        }

        return new Pcg32State(unchecked((ulong)State), unchecked((ulong)Stream));
    }
}
