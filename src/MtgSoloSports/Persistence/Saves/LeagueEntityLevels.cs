using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// League-level helpers for persisted <see cref="LeagueEntity"/> rows.
/// Single canonical mapping from (Kind, FeederDivision) to
/// <see cref="LeagueLevel"/> so feature code never branches on raw ints or
/// parses league names. Historical v1 feeder rows (division None/0) map to
/// Feeder 1 for display/compatibility.
/// </summary>
public static class LeagueEntityLevels
{
    public static LeagueLevel GetLevel(LeagueEntity league)
    {
        ArgumentNullException.ThrowIfNull(league);
        bool isSuperleague = league.Kind == (int)LeagueKind.Superleague;
        if (isSuperleague)
        {
            if (league.FeederDivision != (int)FeederDivision.None)
            {
                throw new InvalidOperationException(
                    $"Superleague '{league.Name}' must carry feeder division None, was {league.FeederDivision}.");
            }

            return LeagueLevel.Superleague;
        }

        if (league.Kind != (int)LeagueKind.Feeder)
        {
            throw new InvalidOperationException($"League '{league.Name}' has corrupt kind {league.Kind}.");
        }

        return LeagueHierarchy.LevelForDivision((FeederDivision)league.FeederDivision, isSuperleague: false);
    }

    public static bool IsFeeder(LeagueEntity league) => LeagueHierarchy.IsFeeder(GetLevel(league));

    public static bool IsSuperleague(LeagueEntity league) => GetLevel(league) == LeagueLevel.Superleague;

    public static string DisplayLevel(LeagueEntity league) => LeagueHierarchy.DisplayName(GetLevel(league));
}
