namespace MtgSoloSports.Features.Qualifiers;

public sealed record QualifierEventMember(
    int AthleteId,
    string SportingColor,
    string Role,
    int FromLeagueId,
    int FromSeasonRank,
    int QualifierRank,
    int QualifierScoreThousandths,
    int BaseScoreThousandths,
    int RoundWins,
    bool IsQualified);
