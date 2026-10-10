namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

/// <summary>
/// One global wildcard place (MSS-071): which group's candidate earned it and
/// why. <see cref="TeamScoreThousandths"/> is the official qualification total;
/// <see cref="NormalizedNumerator"/> / <see cref="NormalizedDenominator"/> encode
/// the exact adjusted comparison (<c>score * groupSize / expectedBaseSum</c>) so
/// unequal group sizes are auditable without floating point. <see cref="TieDraw"/>
/// is true only when the cutoff split exactly tied adjusted performances and a
/// seeded draw decided the place.
/// </summary>
public sealed record TypeCupTournamentWildcard(
    string CreatureType,
    int QualificationGroup,
    int GroupRank,
    int TeamScoreThousandths,
    int TeamBaseThousandths,
    int GroupSize,
    long NormalizedNumerator,
    long NormalizedDenominator,
    bool TieDraw);
