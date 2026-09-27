namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Conflict marker when the Color Cup individual event cannot run: no
/// completed odd season with a resolved 32-athlete selection, or the Cup has
/// already been resolved for the source season. Mapped to HTTP 409.
/// </summary>
public sealed class RunColorCupIndividualConflictException : Exception
{
    public RunColorCupIndividualConflictException(string message)
        : base(message)
    {
    }
}
