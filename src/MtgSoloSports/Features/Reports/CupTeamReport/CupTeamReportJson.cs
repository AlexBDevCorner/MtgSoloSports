using System.Text.Json;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.CupTeamReport;

/// <summary>
/// JSON envelope builder for Cup team reports. Split from the handler so no
/// single method exceeds the curated analyzer length limit.
/// </summary>
internal static class CupTeamReportJson
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetCupTeamReportHandler.ReportData data)
    {
        object envelope = new
        {
            schemaVersion = ReportContract.SchemaVersion,
            reportType = ReportContract.CupTeamReportType,
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
                cup = data.Cup,
                teamKey = data.History.TeamKey,
                teamName = data.History.TeamName,
            },
            snapshot = new
            {
                seasonCount,
                currentSeason = metadata.CurrentSeason,
                phase = metadata.Phase,
                editions = data.History.Honours.Editions,
            },
            units = new
            {
                points = "fixed-point thousandths: divide by 1000 for display (e.g. 77000 = 77.000 team points)",
                missing = "null means unavailable/not-applicable; 0 is an exact zero, never a placeholder",
                history = "one entry per selected edition (newest first); Type Cup finalists keep both qualification and Final stages",
            },
            honours = data.History.Honours,
            seasons = data.History.Seasons,
            roster = data.History.Roster,
            individualMedals = data.History.IndividualMedals,
        };
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
    }
}
