namespace MtgSoloSports.Features.Diagnostics.LongRunStats;

public sealed record GetLongRunStatsResponse(
    Guid SaveId,
    int Seasons,
    int Leagues,
    int Rounds,
    int StageStandings,
    int SeasonStandings,
    int QualifierRounds,
    int QualifierStandings,
    int ColorCupSelections,
    int TypeCupSelections,
    int Honours,
    int StoryEvents,
    long DatabaseBytes,
    ulong RngState,
    ulong RngStream);
