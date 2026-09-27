namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Cup medal stored per individual standing row. Gold/Silver/Bronze go to Cup
/// ranks 1/2/3; every other athlete stores <see cref="None"/>. Stored as an
/// integer so future Cup kinds extend without rewriting rows.
/// </summary>
public enum ColorCupMedal
{
    None = 0,
    Gold = 1,
    Silver = 2,
    Bronze = 3,
}
