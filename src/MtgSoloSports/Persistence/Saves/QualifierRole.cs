namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Qualifier role for one <see cref="QualifierStandingEntity"/>.
/// Incumbents come from Superleague ranks 17-24; challengers from feeder ranks 2-4.
/// The qualifier treats both roles identically; the role is provenance only.
/// </summary>
public enum QualifierRole
{
    Incumbent = 0,
    Challenger = 1,
}
