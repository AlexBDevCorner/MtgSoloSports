namespace MtgSoloSports.Features.Cups.GetTypeCupTeamResult;

/// <summary>
/// Not-found marker when the Type Cup team event has not been resolved yet
/// for the requested source season. Mapped to HTTP 404.
/// </summary>
public sealed class TypeCupTeamResultNotFoundException : Exception
{
    public TypeCupTeamResultNotFoundException(string message)
        : base(message)
    {
    }
}
