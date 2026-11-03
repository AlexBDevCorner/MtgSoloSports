namespace MtgSoloSports.Features.Saves;

/// <summary>
/// Current lifecycle phase of a save. Persisted as text so future phases can be
/// added without rewriting existing save files. Unknown values abort, never default.
/// The postseason forms one legal chain with explicit inspectable boundaries:
/// Season 1 completes into <see cref="SeasonComplete"/>, then Chaos
/// (<see cref="ChaosSelectionResolved"/> field plus first draw, then
/// <see cref="ChaosComplete"/> tournament Final) for eligible seasons, then the special
/// inaugural transition (<see cref="InauguralMovementResolved"/>), then feeder
/// rebalancing (<see cref="Rebalanced"/>). Season 2+ completes into
/// <see cref="SeasonComplete"/>, then Chaos for eligible seasons, then
/// <see cref="AutomaticMovementResolved"/>,
/// then <see cref="QualifierResolved"/>, then <see cref="Rebalanced"/>.
/// <see cref="Rebalanced"/> is the post-rebalance Cup extension point. Odd seasons
/// continue through <see cref="CupSelectionResolved"/> (Color Cup field selected),
/// <see cref="CupIndividualResolved"/> (Color Cup individual complete) and
/// <see cref="CupComplete"/> (team complete); even seasons continue through
/// <see cref="CupSelectionResolved"/> (Type Cup field allocated) directly to
/// <see cref="CupComplete"/> (team complete). Only <see cref="CupComplete"/> may
/// start the next season, returning to <see cref="SeasonInProgress"/>.
/// Ineligible (pre-Chaos) seasons skip both Chaos phases and keep the previous chain.
/// </summary>
public enum SavePhase
{
    SeasonInProgress = 0,

    SeasonComplete = 1,

    ChaosSelectionResolved = 2,

    ChaosComplete = 3,

    InauguralMovementResolved = 4,

    AutomaticMovementResolved = 5,

    QualifierResolved = 6,

    Rebalanced = 7,

    CupSelectionResolved = 8,

    CupIndividualResolved = 9,

    CupComplete = 10,
}
