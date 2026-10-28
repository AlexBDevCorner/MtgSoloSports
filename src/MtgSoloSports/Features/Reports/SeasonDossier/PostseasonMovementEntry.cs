namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record PostseasonMovementEntry(
    int AthleteId,
    string AthleteName,
    string Kind,
    int FromLeagueId,
    string FromLeagueName,
    int ToLeagueId,
    string ToLeagueName,
    int FromSeasonRank,
    string SportingColor);
