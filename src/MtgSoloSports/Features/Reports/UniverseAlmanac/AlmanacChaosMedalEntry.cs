namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record AlmanacChaosMedalEntry(
    int AthleteId,
    string AthleteName,
    int Gold,
    int Silver,
    int Bronze,
    int Editions,
    int LastSeasonNumber);
