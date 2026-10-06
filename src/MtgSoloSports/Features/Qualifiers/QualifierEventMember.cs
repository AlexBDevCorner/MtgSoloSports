namespace MtgSoloSports.Features.Qualifiers;

public sealed record QualifierEventMember(
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
