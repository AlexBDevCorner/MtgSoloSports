namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record LeaguePointsLeaderEntry(
    int AthleteId,
    string AthleteName,
    long TotalLeaguePointsThousandths,
    int CompletedSeasons,
    int Titles,
    int BestRank);
