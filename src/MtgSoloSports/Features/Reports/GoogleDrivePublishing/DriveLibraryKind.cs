namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-091: stable semantic identity for the historical report library.
/// Every published file is keyed by (save id, report type, season number or
/// team key) so repeat syncs update the same Drive file id when changed and
/// skip it when unchanged. MSS-093 adds athlete/standings/league profile
/// reports under explicit athlete categories; standings/league profiles stay
/// download-only.
/// </summary>
internal static class DriveLibraryKind
{
    internal const string SeasonDossier = "season-dossier";

    internal const string ColorEdition = "color-edition";

    internal const string TypeEdition = "type-edition";

    internal const string ColorTeam = "color-team";

    internal const string TypeTeam = "type-team";

    internal const string ChaosEdition = "chaos-edition";

    internal const string Almanac = "almanac";

    internal const string Athlete = "athlete";

    internal const string AthletesIndex = "athletes-index";

    internal static string DossierReport(int seasonNumber) => $"{SeasonDossier}:{seasonNumber}";

    internal static string EditionReport(string cup, int seasonNumber) => $"{cup}-edition:{seasonNumber}";

    internal static string TeamReport(string cup, string teamKey) => $"{cup}-team:{teamKey}";

    internal static string AthleteReport(int athleteId) => $"{Athlete}:{athleteId}";

    internal static string AthleteKey(int athleteId) => athleteId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static int SplitAthleteKey(string key)
    {
        if (!int.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int athleteId) || athleteId <= 0)
        {
            throw new ArgumentException($"Invalid athlete key '{key}'.", nameof(key));
        }

        return athleteId;
    }

    internal static string CategoryOf(string report)
    {
        int separator = report.IndexOf(':');
        return separator < 0 ? report : report[..separator];
    }

    internal static string EditionKey(string cup, int seasonNumber) => $"{cup}:{seasonNumber}";

    internal static string TeamKey(string cup, string teamKey) => $"{cup}:{teamKey}";

    internal static (string Cup, int SeasonNumber) SplitEditionKey(string key)
    {
        int separator = key.IndexOf(':');
        if (separator < 0)
        {
            throw new ArgumentException($"Invalid edition key '{key}'.", nameof(key));
        }

        return (key[..separator], int.Parse(
            key[(separator + 1)..],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static (string Cup, string TeamKey) SplitTeamKey(string key)
    {
        int separator = key.IndexOf(':');
        if (separator < 0)
        {
            throw new ArgumentException($"Invalid team key '{key}'.", nameof(key));
        }

        return (key[..separator], key[(separator + 1)..]);
    }
}
