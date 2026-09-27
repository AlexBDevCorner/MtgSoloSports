namespace MtgSoloSports.Features.Cups.GetColorCupTeamResult;

/// <summary>
/// Immutable read row for one Color Cup color team.
/// Built only from persisted team standings so replay never resimulates.
/// </summary>
public sealed record GetColorCupTeamMember(
    int SportingColor,
    string TeamName,
    int TeamRank,
    int TeamScoreThousandths,
    int TeamBaseThousandths,
    int GroupWins,
    int RoundWins,
    string Medal);
