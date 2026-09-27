namespace MtgSoloSports.Features.Cups.GetColorCupSelection;

/// <summary>
/// One selected representative with scores and components.
/// </summary>
public sealed record GetColorCupMember(
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
