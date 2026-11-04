namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record ChaosSummaryEntry(
    string State,
    bool HasEdition,
    bool IsComplete,
    int FieldSize,
    int RoundsPlayed,
    int TotalRounds,
    int GroupsCompleted,
    int TotalGroups,
    string EditionChecksum,
    int RulesVersion,
    int ChaosFormatVersion,
    ChaosChampionEntry? Champion,
    IReadOnlyList<CupPodiumEntry> FinalTopThree,
    IReadOnlyList<ChaosBreakdownEntry> ByColor,
    IReadOnlyList<ChaosBreakdownEntry> ByDivision,
    string Note);
