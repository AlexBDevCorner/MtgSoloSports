namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record TypeCupQualificationGroupEntry(
    int QualificationGroup,
    int GroupSize,
    int FinalPlaces,
    int GuaranteedPlaces,
    string Checksum,
    IReadOnlyList<string> QualifiedTeams,
    IReadOnlyList<string> EliminatedTeams,
    string? WildcardCandidate,
    string? WildcardWinner,
    IReadOnlyList<TypeCupQualificationTeamEntry> Teams);
