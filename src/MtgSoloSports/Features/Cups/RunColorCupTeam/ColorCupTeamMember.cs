namespace MtgSoloSports.Features.Cups.RunColorCupTeam;

/// <summary>
/// Immutable presentation row for one Color Cup color team.
/// Built only from persisted team standings so replay never resimulates.
/// </summary>
public sealed record ColorCupTeamMember(
    int SportingColor,
    string TeamName,
    int TeamRank,
    int TeamScoreThousandths,
    int TeamBaseThousandths,
    int GroupWins,
    int RoundWins,
    string Medal);
