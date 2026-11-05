namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record IndividualCupSummary(
    string? ChampionName,
    int? ChampionAthleteId,
    IReadOnlyList<CupPodiumEntry> TopThree,
    int FormatVersion = 0,
    bool IsPhased = false,
    int TotalRounds = 0,
    int QualifyingRounds = 16,
    int? CutoffRank = null,
    int? FinalFieldSize = null,
    bool? TieDrawConsumed = null,
    int? QualifierCount = null,
    int? EliminatedCount = null);
