namespace MtgSoloSports.SimulationKernel.TieBreaking;

/// <summary>
/// A ranked entry. Rank is 1-based position in the final best-first order.
/// After a complete tie-break the order is total, so ranks are 1..N.
/// </summary>
public sealed record RankedEntry<T>(T Entry, int Rank);
