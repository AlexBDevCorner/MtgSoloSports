namespace MtgSoloSports.SimulationKernel.Leagues;

/// <summary>
/// Feeder subdivision. <see cref="None"/> applies only to the Superleague;
/// every feeder league carries an explicit division (First/Second/Third).
/// Persisted on the league row so later tasks never parse league names.
/// </summary>
public enum FeederDivision
{
    None = 0,
    First = 1,
    Second = 2,
    Third = 3,
}
