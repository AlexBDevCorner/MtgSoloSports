namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

/// <summary>
/// Thrown when Type Cup team allocation cannot run: no completed even season is
/// ready, the source season is odd (Color Cup territory), or the allocation for
/// the source season already exists. Maps to 409. Corrupted sporting state
/// throws <see cref="MtgSoloSports.Shared.SaveCorruptionException"/> instead (MSS-144: 500).
/// </summary>
public sealed class SelectTypeCupTeamsConflictException : Exception
{
    public SelectTypeCupTeamsConflictException(string message)
        : base(message)
    {
    }
}
