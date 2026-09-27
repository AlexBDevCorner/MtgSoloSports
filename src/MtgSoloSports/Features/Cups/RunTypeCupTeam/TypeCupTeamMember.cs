namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Immutable presentation row for one Type Cup creature-type team.
/// Built only from persisted team standings so replay never resimulates.
/// </summary>
public sealed record TypeCupTeamMember(
    string CreatureType,
    string TeamName,
    int TeamRank,
    int TeamScoreThousandths,
    int TeamBaseThousandths,
    int GroupWins,
    int RoundWins,
    string Medal);
