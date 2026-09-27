namespace MtgSoloSports.Features.Cups.GetTypeCupSelection;

/// <summary>
/// One allocated Type Cup representative with persisted scores and components.
/// All sporting values are fixed-point thousandths integers.
/// </summary>
public sealed record GetTypeCupMember(
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
