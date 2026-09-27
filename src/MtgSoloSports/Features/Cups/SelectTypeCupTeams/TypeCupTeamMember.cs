namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

/// <summary>
/// One allocated Type Cup representative with persisted scores and components
/// so the user can understand why the athlete was chosen. All sporting values
/// are fixed-point thousandths integers. <c>TypeRank</c> is the athlete's rank
/// within its creature type's full candidate ordering (1 is strongest);
/// <c>SelectionRank</c> is the athlete's order #1..#4 within its allocated team.
/// </summary>
public sealed record TypeCupTeamMember(
    int SaveAthleteId,
    string Name,
    string CreatureType,
    int SelectionRank,
    int TypeRank,
    int FinalRatingThousandths,
    int BonusNormThousandths,
    int PerformanceNormThousandths,
    int FormNormThousandths,
    int PrestigeNormThousandths,
    int BonusRawThousandths,
    int PerformanceRawThousandths,
    int FormRaw,
    int PrestigeRaw);
