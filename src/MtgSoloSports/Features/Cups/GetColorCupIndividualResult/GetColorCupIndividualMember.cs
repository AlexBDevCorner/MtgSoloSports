namespace MtgSoloSports.Features.Cups.GetColorCupIndividualResult;

/// <summary>
/// Immutable presentation row for one Color Cup individual athlete.
/// Built only from persisted Cup standings so replay never resimulates.
/// </summary>
public sealed record GetColorCupIndividualMember(
    int AthleteId,
    string Name,
    string SportingColor,
    int SelectionRank,
    int CupRank,
    int CupScoreThousandths,
    int BaseScoreThousandths,
    int RoundWins,
    string Medal);
