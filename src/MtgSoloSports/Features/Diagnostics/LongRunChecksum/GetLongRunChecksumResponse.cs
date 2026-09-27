namespace MtgSoloSports.Features.Diagnostics.LongRunChecksum;

public sealed record GetLongRunChecksumResponse(
    Guid SaveId,
    int SeasonsCompleted,
    int TotalRounds,
    int TotalStageStandings,
    int TotalSeasonStandings,
    ulong RngState,
    ulong RngStream,
    string Checksum,
    IReadOnlyList<LongRunSeasonChecksumEntry> SeasonChecksums);
