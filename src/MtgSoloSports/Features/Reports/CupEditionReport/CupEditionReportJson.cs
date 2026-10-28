using System.Text.Json;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.CupEditionReport;

/// <summary>
/// JSON envelope builder for Cup edition reports. Split from the handler so no
/// single method exceeds the curated analyzer length limit.
/// </summary>
internal static class CupEditionReportJson
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetCupEditionReportHandler.ReportData data)
    {
        object envelope = new
        {
            schemaVersion = ReportContract.SchemaVersion,
            reportType = ReportContract.CupEditionReportType,
            saveId = saveId.ToString("D"),
            generatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            save = SavePart(metadata, rules),
            scope = new
            {
                cup = data.Cup,
                sourceSeasonNumber = data.SourceSeasonNumber,
                sourceSeasonId = data.SourceSeasonId,
            },
            snapshot = SnapshotPart(metadata, seasonCount, data),
            units = UnitsPart(),
            selection = SelectionPart(data),
            colorIndividual = data.ColorIndividual,
            colorTeam = data.ColorTeam,
            typeTournament = data.TypeTournament,
        };
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
    }

    private static object SavePart(SaveMetadataEntity metadata, SimulationKernel.Rules.RulesV1 rules)
    {
        return new
        {
            name = metadata.Name,
            currentSeason = metadata.CurrentSeason,
            phase = metadata.Phase,
            schemaVersion = metadata.SchemaVersion,
            rulesVersion = rules.Version,
        };
    }

    private static object SnapshotPart(
        SaveMetadataEntity metadata,
        int seasonCount,
        GetCupEditionReportHandler.ReportData data)
    {
        return new
        {
            state = data.State,
            teamCount = data.TeamCount,
            hasFullRanking = data.HasFullRanking,
            seasonCount,
            currentSeason = metadata.CurrentSeason,
            phase = metadata.Phase,
            colorIndividualComplete = data.ColorIndividual is not null,
            colorTeamComplete = data.ColorTeam is not null,
            typeTournamentComplete = data.TypeTournament is not null,
        };
    }

    private static object UnitsPart()
    {
        return new
        {
            points = "fixed-point thousandths: divide by 1000 for display (e.g. 77000 = 77.000 team points)",
            missing = "null means unavailable/not-applicable; 0 is an exact zero, never a placeholder",
            ranks = "teamRank 1 holds the champion team; individual cupRank 1 holds the individual champion (Color only)",
            qualification = "Type Cup qualification standings never award titles; only Final ranks 1-3 hold official medals",
        };
    }

    private static object SelectionPart(GetCupEditionReportHandler.ReportData data)
    {
        return new
        {
            hasFullRanking = data.HasFullRanking,
            note = data.HasFullRanking
                ? "Squads below are the persisted selected members; the full ranking lives in the selection event replay."
                : "Selections predate stored ranking reports; only the selected members are listed.",
            teams = data.Squads.Select(t => new
            {
                teamKey = t.TeamKey,
                teamName = t.TeamName,
                members = t.Members.Select(m => new
                {
                    athleteId = m.AthleteId,
                    name = m.Name,
                    selectionRank = m.SelectionRank,
                    finalRatingThousandths = m.FinalRatingThousandths,
                    typeRank = m.TypeRank,
                }).ToList(),
            }).ToList(),
        };
    }
}
