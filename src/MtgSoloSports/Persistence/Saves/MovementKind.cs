namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Kind of postseason league movement. MSS-014 persists only
/// <see cref="InauguralPromotion"/>; MSS-015 adds automatic Superleague
/// movement plus qualifier-candidate markers without rewriting existing rows.
/// Stored as an integer so future kinds extend the enum.
/// </summary>
public enum MovementKind
{
    InauguralPromotion = 0,

    /// <summary>
    /// Feeder-league champion (rank 1) automatically promoted to the next
    /// Superleague. Exactly eight per normal transition.
    /// </summary>
    AutomaticPromotion = 1,

    /// <summary>
    /// Superleague rank 25-32 automatically relegated to the returning
    /// sporting-color feeder. Exactly eight per normal transition.
    /// </summary>
    AutomaticRelegation = 2,

    /// <summary>
    /// Superleague rank 17-24 entering the qualifier as an incumbent.
    /// Exactly eight per normal transition; winners decided by MSS-016.
    /// </summary>
    QualifierIncumbent = 3,

    /// <summary>
    /// Feeder rank 2-4 entering the qualifier as a challenger.
    /// Exactly 24 per normal transition (3 per feeder); winners decided by MSS-016.
    /// </summary>
    QualifierChallenger = 4,
}
