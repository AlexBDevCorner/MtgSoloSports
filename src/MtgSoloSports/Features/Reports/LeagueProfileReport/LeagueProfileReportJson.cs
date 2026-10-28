using System.Text.Json;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.LeagueProfileReport;

/// <summary>
/// JSON envelope builder for league profile reports. Split from the handler so
/// no single method exceeds the curated analyzer length limit.
/// </summary>
internal static class LeagueProfileReportJson
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetLeagueProfileReportHandler.ReportData data)
    {
        object envelope = new
        {
            schemaVersion = ReportContract.SchemaVersion,
            reportType = ReportContract.LeagueProfileReportType,
            saveId = saveId.ToString("D"),
            generatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            save = new
            {
                name = metadata.Name,
                currentSeason = metadata.CurrentSeason,
                phase = metadata.Phase,
                schemaVersion = metadata.SchemaVersion,
                rulesVersion = rules.Version,
            },
            scope = new
            {
                leagueKey = data.Profile.LeagueKey,
            },
            snapshot = new
            {
                seasonCount,
                currentSeason = metadata.CurrentSeason,
                phase = metadata.Phase,
                totalSeasons = data.Profile.TotalSeasons,
                completedSeasons = data.Profile.CompletedSeasons,
            },
            units = new
            {
                leaguePoints = "official all-time Total league points in fixed-point thousandths: sum of final season championship points earned while competing in this league (divide by 1000)",
                careerRating = "weighted Hall of Fame career points are never included; this report carries league points only",
                missing = "null means unavailable/not-applicable; 0 is an exact zero, never a placeholder",
            },
            league = new
            {
                key = data.Profile.LeagueKey,
                name = data.Profile.Name,
                kind = data.Profile.Kind,
                feederDivision = data.Profile.FeederDivision,
                leagueLevel = data.Profile.LeagueLevel,
                sportingColor = data.Profile.SportingColor,
                sportingColorName = data.Profile.SportingColorName,
                totalSeasons = data.Profile.TotalSeasons,
                completedSeasons = data.Profile.CompletedSeasons,
                uniqueAthletes = data.Profile.UniqueAthletes,
                firstSeasonNumber = data.Profile.FirstSeasonNumber,
                lastCompletedSeasonNumber = data.Profile.LastCompletedSeasonNumber,
            },
            currentSeason = data.Profile.CurrentSeason,
            podiumHistory = data.Profile.PodiumHistory,
            allTimeLeaderboard = data.Profile.AllTimeLeaderboard,
            records = data.Profile.Records,
        };
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
    }
}
