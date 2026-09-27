namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Immutable presentation row for one Color Cup individual athlete.
/// Built only from persisted Cup standings so replay never resimulates.
/// </summary>
public sealed record ColorCupIndividualMember(
    int AthleteId,
    string Name,
    string SportingColor,
    int SelectionRank,
    int CupRank,
    int CupScoreThousandths,
    int BaseScoreThousandths,
    int RoundWins,
    string Medal);
