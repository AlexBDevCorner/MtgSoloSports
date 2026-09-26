namespace MtgSoloSports.Features.Superleague.CreateInaugural;

/// <summary>
/// Thrown when the inaugural Superleague cannot be created: Season 1 is not
/// yet complete, or the Season 2 Superleague already exists. Maps to 409.
/// Corrupted sporting state throws <see cref="InvalidOperationException"/> instead.
/// </summary>
public sealed class CreateInauguralSuperleagueConflictException : Exception
{
    public CreateInauguralSuperleagueConflictException(string message)
        : base(message)
    {
    }
}
