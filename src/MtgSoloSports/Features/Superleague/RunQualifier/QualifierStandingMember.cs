namespace MtgSoloSports.Features.Superleague.RunQualifier;

/// <summary>
/// Immutable presentation row for one qualifier athlete.
/// Built only from persisted qualifier standings so replay never resimulates.
/// </summary>
public sealed record QualifierStandingMember(
    int AthleteId,
    string Name,
    string SportingColor,
    string Role,
    int FromLeagueId,
    string FromLeagueName,
    int FromSeasonRank,
    int QualifierRank,
    int QualifierScoreThousandths,
    int BaseScoreThousandths,
    int RoundWins,
    bool IsQualified);
