namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

/// <summary>
/// Thrown when Color Cup team selection cannot run: no completed odd season is
/// ready, the source season is even (Type Cup territory), or the selection for
/// the source season already exists. Maps to 409. Corrupted sporting state
/// throws <see cref="InvalidOperationException"/> instead.
/// </summary>
public sealed class SelectColorCupTeamsConflictException : Exception
{
    public SelectColorCupTeamsConflictException(string message)
        : base(message)
    {
    }
}
