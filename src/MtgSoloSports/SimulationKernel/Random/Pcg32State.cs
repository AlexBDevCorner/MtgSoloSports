using System.Runtime.InteropServices;

namespace MtgSoloSports.SimulationKernel.Random;

/// <summary>
/// Serializable RNG state for <see cref="Pcg32V1"/>.
/// Persist both values alongside generated results in the same transaction.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct Pcg32State(ulong State, ulong Stream);
