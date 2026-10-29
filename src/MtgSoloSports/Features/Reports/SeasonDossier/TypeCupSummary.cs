namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record TypeCupSummary(
    string? ChampionTeamKey,
    IReadOnlyList<CupPodiumEntry> FinalTopThree,
    int FinishedQualificationGroups,
    int QualificationGroupCount,
    IReadOnlyList<string> QualifiedTeams,
    IReadOnlyList<CupRosterEntry> WinningRoster,
    bool IsDirectFinal,
    IReadOnlyList<TypeCupQualificationGroupEntry> QualificationGroups,
    int QualificationPolicyVersion,
    int WildcardCount,
    IReadOnlyList<int> GuaranteedPlacesPerGroup,
    IReadOnlyList<TypeCupWildcardEntry> Wildcards);
