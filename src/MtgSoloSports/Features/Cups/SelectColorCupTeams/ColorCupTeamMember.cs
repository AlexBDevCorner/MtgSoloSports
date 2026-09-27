namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

/// <summary>
/// One selected Color Cup representative with persisted scores and components
/// so the user can understand why the athlete was chosen. All sporting values
/// are fixed-point thousandths integers.
/// </summary>
public sealed record ColorCupTeamMember(
    int SaveAthleteId,
    string Name,
    string SportingColor,
    int SelectionRank,
    int FinalRatingThousandths,
    int BonusNormThousandths,
    int PerformanceNormThousandths,
    int FormNormThousandths,
    int PrestigeNormThousandths,
    int BonusRawThousandths,
    int PerformanceRawThousandths,
    int FormRaw,
    int PrestigeRaw);
