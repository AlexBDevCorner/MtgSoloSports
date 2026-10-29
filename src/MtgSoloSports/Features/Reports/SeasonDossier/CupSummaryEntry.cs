namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record CupSummaryEntry(
    string Cup,
    string State,
    bool HasSelection,
    IndividualCupSummary? Individual,
    TeamCupSummary? Team,
    TypeCupSummary? Tournament,
    string Note);
