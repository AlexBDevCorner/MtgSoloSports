namespace MtgSoloSports.Features.Cups.RunColorCupTeam;

/// <summary>
/// Immutable presentation row for one Color Cup team leg (group standing).
/// Built only from persisted group standings so replay never resimulates.
/// </summary>
public sealed record ColorCupTeamLegMember(
    int AthleteId,
    string Name,
    string SportingColor,
    int SelectionRank,
    int GroupNumber,
    int GroupRank,
    int GroupScoreThousandths,
    int BaseScoreThousandths,
    int RoundWins);
