using MtgSoloSports.Features.Records.GetHallOfFame;

namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record AlmanacCareerLeaders(
    string WeightsVersion,
    int TotalAthletes,
    int? FromSeason,
    int? ToSeason,
    IReadOnlyList<HallOfFameEntry> Leaders);
