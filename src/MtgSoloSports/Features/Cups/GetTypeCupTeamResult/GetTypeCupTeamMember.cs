namespace MtgSoloSports.Features.Cups.GetTypeCupTeamResult;

/// <summary>
/// Immutable read row for one Type Cup creature-type team.
/// Built only from persisted team standings so replay never resimulates.
/// </summary>
public sealed record GetTypeCupTeamMember(
    string CreatureType,
    string TeamName,
    int TeamRank,
    int TeamScoreThousandths,
    int TeamBaseThousandths,
    int GroupWins,
    int RoundWins,
    string Medal);
