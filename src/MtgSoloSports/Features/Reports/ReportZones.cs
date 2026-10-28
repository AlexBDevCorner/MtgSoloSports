using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Reports;

/// <summary>
/// Authoritative zone mapping for final standings tables. Mirrors the
/// frontend visual zones so reports and UI agree, but zones are only
/// authoritative when <c>isFinal</c> is true; provisional tables carry no
/// zone. Bands come from Game Rules §§10-11 (Superleague 1-16 safe, 17-24
/// qualifier, 25-32 relegated; tiered feeder bands per level).
/// </summary>
public static class ReportZones
{
    public static string? ZoneForRank(LeagueLevel level, int rank, bool isFinal)
    {
        if (!isFinal)
        {
            return null;
        }

        return level switch
        {
            LeagueLevel.Superleague => rank <= 16 ? "safe" : rank <= 24 ? "qualifier" : "relegation",
            LeagueLevel.Feeder1 => rank == 1 ? "champion"
                : rank <= 4 ? "qualifier-up"
                : rank <= 16 ? "safe"
                : rank <= 24 ? "qualifier-hold" : "relegated",
            LeagueLevel.Feeder2 => rank <= 8 ? "promoted"
                : rank <= 16 ? "qualifier-up"
                : rank <= 24 ? "qualifier-hold" : "relegated",
            LeagueLevel.Feeder3 => rank <= 8 ? "promoted"
                : rank <= 16 ? "qualifier-up" : "safe",
            _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
        };
    }

    public static string ZoneLabel(LeagueLevel level, string zone)
    {
        return (level, zone) switch
        {
            (LeagueLevel.Superleague, "safe") => "Safe (1–16)",
            (LeagueLevel.Superleague, "qualifier") => "Qualifier (17–24)",
            (LeagueLevel.Superleague, "relegation") => "Relegation (25–32)",
            (_, "champion") => "Champion · auto-promoted",
            (_, "promoted") => "Auto-promoted",
            (_, "qualifier-up") => "Qualifier · challenger",
            (_, "qualifier-hold") => "Qualifier · incumbent",
            (_, "relegated") => "Relegated",
            (_, "safe") => "Safe",
            _ => zone,
        };
    }

    public static string ZoneSummary(LeagueLevel level)
    {
        return level switch
        {
            LeagueLevel.Superleague => "Superleague 1–16 safe, 17–24 qualifier, 25–32 relegated",
            LeagueLevel.Feeder1 => "Feeder 1: 1 champion promoted, 2–4 qualifier challengers, 5–16 safe, 17–24 qualifier incumbents, 25–32 relegated",
            LeagueLevel.Feeder2 => "Feeder 2: 1–8 promoted, 9–16 qualifier challengers, 17–24 qualifier incumbents, 25–32 relegated",
            LeagueLevel.Feeder3 => "Feeder 3: 1–8 promoted, 9–16 qualifier challengers, 17–32 safe",
            _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
        };
    }
}
