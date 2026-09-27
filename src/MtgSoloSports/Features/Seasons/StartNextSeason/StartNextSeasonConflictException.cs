namespace MtgSoloSports.Features.Seasons.StartNextSeason;

/// <summary>
/// The next season cannot start yet: the current season is still in progress or
/// the pending postseason transition (movement, qualifier, rebalancing) is not
/// yet resolved. Callers should follow the legal next action instead of
/// skipping or repairing state.
/// </summary>
public sealed class StartNextSeasonConflictException : Exception
{
    public StartNextSeasonConflictException(string message)
        : base(message)
    {
    }
}
