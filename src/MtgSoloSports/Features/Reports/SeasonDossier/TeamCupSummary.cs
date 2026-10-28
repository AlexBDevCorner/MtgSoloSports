namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record TeamCupSummary(
    string? ChampionTeamKey,
    string? ChampionTeamName,
    IReadOnlyList<CupPodiumEntry> TopThree,
    IReadOnlyList<CupRosterEntry> WinningRoster);
