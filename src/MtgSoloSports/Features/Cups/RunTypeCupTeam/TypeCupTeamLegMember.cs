namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Immutable presentation row for one Type Cup team leg (group standing).
/// Built only from persisted group standings so replay never resimulates.
/// </summary>
public sealed record TypeCupTeamLegMember(
    int AthleteId,
    string Name,
    string CreatureType,
    int SelectionRank,
    int GroupNumber,
    int GroupRank,
    int GroupScoreThousandths,
    int BaseScoreThousandths,
    int RoundWins);
