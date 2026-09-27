namespace MtgSoloSports.Features.Cups.GetTypeCupTeamResult;

/// <summary>
/// Immutable read row for one Type Cup team leg (group standing).
/// Built only from persisted group standings so replay never resimulates.
/// </summary>
public sealed record GetTypeCupTeamLegMember(
    int AthleteId,
    string Name,
    string CreatureType,
    int SelectionRank,
    int GroupNumber,
    int GroupRank,
    int GroupScoreThousandths,
    int BaseScoreThousandths,
    int RoundWins);
