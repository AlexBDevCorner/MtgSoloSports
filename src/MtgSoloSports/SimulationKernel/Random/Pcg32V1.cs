namespace MtgSoloSports.SimulationKernel.Random;

/// <summary>
/// Explicit versioned deterministic PRNG (PCG-XSH-RR 64/32, v1).
/// This is the only permitted source of sporting randomness.
/// State is serializable via <see cref="Pcg32State"/> so saves can persist algorithm + state.
/// </summary>
public sealed class Pcg32V1
{
    public const string AlgorithmName = "Pcg32V1";
    public const int AlgorithmVersion = 1;

    private const ulong Multiplier = 6364136223846793005UL;

    private ulong _state;
    private readonly ulong _stream;
    private readonly ulong _increment;

    public Pcg32V1(ulong seed, ulong stream)
    {
        _stream = stream;
        _increment = (stream << 1) | 1UL;
        _state = 0UL;
        NextUInt32();
        unchecked
        {
            _state += seed;
        }

        NextUInt32();
    }

    private Pcg32V1(ulong state, ulong stream, bool restore)
    {
        if (!restore)
        {
            throw new ArgumentException("Restore flag must be true.", nameof(restore));
        }

        _state = state;
        _stream = stream;
        _increment = (stream << 1) | 1UL;
    }

    public ulong State => _state;

    public ulong Stream => _stream;

    public Pcg32State Snapshot() => new(_state, _stream);

    public static Pcg32V1 Restore(Pcg32State state) => new(state.State, state.Stream, restore: true);

    public static Pcg32V1 Restore(ulong state, ulong stream) => new(state, stream, restore: true);

    /// <summary>
    /// Advances the generator and returns the next 32-bit output.
    /// </summary>
    public uint NextUInt32()
    {
        ulong oldState = _state;
        unchecked
        {
            _state = (oldState * Multiplier) + _increment;
        }

        uint xorshifted = (uint)((((oldState >> 18) ^ oldState) >> 27) & 0xFFFFFFFFUL);
        int rotation = (int)(oldState >> 59);
        return RotateRight(xorshifted, rotation);
    }

    /// <summary>
    /// Returns a value in [0, exclusiveUpperBound) with equal probability, using rejection sampling.
    /// </summary>
    public int NextInt32(int exclusiveUpperBound)
    {
        if (exclusiveUpperBound <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exclusiveUpperBound), "Bound must be positive.");
        }

        uint bound = (uint)exclusiveUpperBound;
        return (int)NextBounded(bound);
    }

    /// <summary>
    /// Returns a value in [0, bound) with equal probability, using rejection sampling.
    /// The rejection threshold is computed in the 32-bit output domain so the
    /// accepted range stays an exact multiple of <paramref name="bound"/>.
    /// </summary>
    public uint NextBounded(uint bound)
    {
        if (bound == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bound), "Bound must be positive.");
        }

        uint threshold = ComputeRejectionThreshold(bound);
        while (true)
        {
            uint value = NextUInt32();
            if (value >= threshold)
            {
                return value % bound;
            }
        }
    }

    /// <summary>
    /// Computes the rejection threshold for <see cref="NextBounded"/> in the 32-bit output domain.
    /// The accepted raw range [threshold, 2^32) is always an exact multiple of <paramref name="bound"/>.
    /// </summary>
    public static uint ComputeRejectionThreshold(uint bound)
    {
        if (bound == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bound), "Bound must be positive.");
        }

        unchecked
        {
            return (0u - bound) % bound;
        }
    }

    private static uint RotateRight(uint value, int rotation)
    {
        rotation &= 31;
        return rotation == 0 ? value : (value >> rotation) | (value << (32 - rotation));
    }
}
