namespace MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;

/// <summary>
/// Thrown when the Type Cup qualification draw cannot run: no completed even
/// season selection exists, the source season is odd, the draw already exists,
/// the team event is already resolved, or the save uses the legacy
/// single-field format with no qualification stage. Maps to 409. Corrupted
/// sporting state throws <see cref="InvalidOperationException"/> instead.
/// </summary>
public sealed class DrawTypeCupQualificationGroupsConflictException : Exception
{
    public DrawTypeCupQualificationGroupsConflictException(string message)
        : base(message)
    {
    }
}
