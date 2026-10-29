namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record PostseasonQualifierEntry(
    int AthleteId,
    string AthleteName,
    int QualifierBoundary,
    string QualifierBoundaryLabel,
    int QualifierSportingColor,
    int QualifierRank,
    int QualifierScoreThousandths,
    int BaseScoreThousandths,
    bool IsQualified,
    string Role,
    int FromLeagueId,
    string FromLeagueName,
    int FromSeasonRank);
