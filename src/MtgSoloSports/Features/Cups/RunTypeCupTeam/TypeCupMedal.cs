namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Cup medal stored per Type Cup team standing row. Gold/Silver/Bronze go to team
/// ranks 1/2/3; every other team stores <see cref="None"/>. Stored as an
/// integer so future Cup kinds extend without rewriting rows.
/// </summary>
public enum TypeCupMedal
{
    None = 0,
    Gold = 1,
    Silver = 2,
    Bronze = 3,
}
