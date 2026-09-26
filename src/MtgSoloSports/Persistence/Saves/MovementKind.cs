namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Kind of postseason league movement. MSS-014 persists only
/// <see cref="InauguralPromotion"/>; later slices (automatic movement,
/// qualifier, rebalancing) add new kinds without rewriting existing rows.
/// Stored as an integer so future kinds extend the enum.
/// </summary>
public enum MovementKind
{
    InauguralPromotion = 0,
}
