namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record UniverseAlmanacData(
    int SeasonCount,
    int FromSeasonNumber,
    int ToSeasonNumber,
    IReadOnlyList<AlmanacSeasonEntry> Seasons,
    IReadOnlyList<SuperleaguePodiumEntry> SuperleaguePodiums,
    IReadOnlyList<FeederTitleEntry> FeederTitles,
    IReadOnlyList<AlmanacCupEditionEntry> CupEditions,
    IReadOnlyList<Cups.ListCupEditions.ListCupEditionsResponse.TeamSummary> ColorTeamTable,
    IReadOnlyList<Cups.ListCupEditions.ListCupEditionsResponse.ColorMedalRow> ColorIndividualMedals,
    IReadOnlyList<Cups.ListCupEditions.ListCupEditionsResponse.ColorMedalRow> ColorCombinedMedals,
    IReadOnlyList<Cups.ListCupEditions.ListCupEditionsResponse.TeamSummary> TypeTeamTable,
    IReadOnlyList<AlmanacCareerRecordEntry> CareerRecords,
    AlmanacCareerLeaders WeightedLeaders,
    IReadOnlyList<LeaguePointsLeaderEntry> SuperleaguePointsLeaders,
    IReadOnlyList<StreakEntry> TitleStreaks,
    IReadOnlyList<StreakEntry> StageWinStreaks);
