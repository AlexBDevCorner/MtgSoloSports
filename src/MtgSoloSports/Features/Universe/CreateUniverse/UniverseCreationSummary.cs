using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Universe.CreateUniverse;

/// <summary>
/// Deterministic creation summary for UI display. Counts come from the save's
/// own rules snapshot quotas; the checksum fingerprints the selected athlete
/// set so identical seeds can be compared at a glance.
/// </summary>
public sealed record UniverseCreationSummary(
    int TotalAthletes,
    IReadOnlyDictionary<SportingColor, int> AthletesPerColor,
    string Checksum);
