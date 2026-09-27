namespace MtgSoloSports.Features.Saves;

/// <summary>
/// Current lifecycle phase of a save. Persisted as text so future phases can be
/// added without rewriting existing save files. Unknown values abort, never default.
/// The postseason forms one legal chain with explicit inspectable boundaries:
/// Season 1 completes into <see cref="SeasonComplete"/>, then the special
/// inaugural transition (<see cref="InauguralMovementResolved"/>), then feeder
/// rebalancing (<see cref="Rebalanced"/>). Season 2+ completes into
/// <see cref="SeasonComplete"/>, then <see cref="AutomaticMovementResolved"/>,
/// then <see cref="QualifierResolved"/>, then <see cref="Rebalanced"/>.
/// <see cref="Rebalanced"/> is the post-rebalance Cup extension point. Odd seasons
/// continue through <see cref="CupSelectionResolved"/> (Color Cup field selected),
/// <see cref="CupIndividualResolved"/> (Color Cup individual complete) and
/// <see cref="CupComplete"/> (team complete); even seasons continue through
/// <see cref="CupSelectionResolved"/> (Type Cup field allocated) directly to
/// <see cref="CupComplete"/> (team complete). Only <see cref="CupComplete"/> may
/// start the next season, returning to <see cref="SeasonInProgress"/>.
/// </summary>
public enum SavePhase
{
    SeasonInProgress = 0,

    SeasonComplete = 1,

    InauguralMovementResolved = 2,

    AutomaticMovementResolved = 3,

    QualifierResolved = 4,

    Rebalanced = 5,

    CupSelectionResolved = 6,

    CupIndividualResolved = 7,

    CupComplete = 8,
}
