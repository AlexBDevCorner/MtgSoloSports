namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record AlmanacCupPodium(
    string TeamKey,
    string TeamName,
    int TeamRank,
    string Medal,
    int TeamScoreThousandths);
