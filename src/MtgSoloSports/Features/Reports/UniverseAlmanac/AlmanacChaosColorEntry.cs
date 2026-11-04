namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record AlmanacChaosColorEntry(
    string TeamKey,
    string TeamName,
    int Gold,
    int Silver,
    int Bronze,
    int Entrants);
