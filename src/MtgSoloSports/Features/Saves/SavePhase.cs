namespace MtgSoloSports.Features.Saves;

/// <summary>
/// Current lifecycle phase of a save. Persisted as text so future phases can be
/// added without rewriting existing save files. Unknown values abort, never default.
/// </summary>
public enum SavePhase
{
    SeasonInProgress = 0,
}
