namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

/// <summary>Thrown when no Type Cup tournament exists for the season (404).</summary>
public sealed class TypeCupTournamentNotFoundException : Exception
{
    public TypeCupTournamentNotFoundException(string message)
        : base(message)
    {
    }
}
