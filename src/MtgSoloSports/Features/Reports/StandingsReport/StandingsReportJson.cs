using System.Text.Json;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Reports.StandingsReport;

/// <summary>
/// JSON envelope builder for standings reports. Split from the handler so no
/// single method exceeds the curated analyzer length limit.
/// </summary>
internal static class StandingsReportJson
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity season,
        LeagueEntity league,
        GetStandingsReportHandler.ReportData data)
    {
        LeagueLevel level = LeagueEntityLevels.GetLevel(league);
        FeederDivision division = (FeederDivision)league.FeederDivision;
        object envelope = new
        {
            schemaVersion = ReportContract.SchemaVersion,
            reportType = ReportContract.StandingsReportType,
            saveId = saveId.ToString("D"),
            generatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            save = SavePart(metadata, rules),
            scope = new
            {
                seasonNumber = season.SeasonNumber,
                leagueId = league.Id,
            },
            snapshot = SnapshotPart(metadata, season, data),
            league = LeaguePart(league, level),
            units = UnitsPart(),
            zones = ZonesPart(level, division, data),
            standings = RowsPart(data),
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

    private static object SnapshotPart(SaveMetadataEntity metadata, SeasonEntity season, GetStandingsReportHandler.ReportData data)
    {
        return new
        {
            seasonNumber = season.SeasonNumber,
            isSeasonComplete = season.IsComplete,
            status = data.Status,
            isFinal = data.IsFinal,
            completedStages = data.CompletedStages,
            totalStages = data.TotalStages,
            globalStage = data.GlobalStage,
            seasonChecksum = data.SeasonChecksum,
            currentSeason = metadata.CurrentSeason,
            phase = metadata.Phase,
        };
    }

    private static object LeaguePart(LeagueEntity league, LeagueLevel level)
    {
        string sportingColorName = Enum.IsDefined(typeof(SimulationKernel.Catalog.SportingColor), league.SportingColor)
            ? ((SimulationKernel.Catalog.SportingColor)league.SportingColor).ToString()
            : "Unknown";
        return new
        {
            id = league.Id,
            name = league.Name,
            kind = ((LeagueKind)league.Kind).ToString(),
            level = level.ToString(),
            feederDivision = league.FeederDivision,
            sportingColor = league.SportingColor,
            sportingColorName,
        };
    }

    private static object UnitsPart()
    {
        return new
        {
            championshipPoints = "official season championship points in fixed-point thousandths (divide by 1000; 32-position table, no bonus multiplier)",
            stageScore = "persisted stage-score sums in fixed-point thousandths (display only; tie-break input)",
            baseScore = "persisted base-score sums in fixed-point thousandths (display only; tie-break input)",
            missing = "null means unavailable/not-applicable; 0 is an exact zero, never a placeholder",
            ranks = "seasonRank 1 holds the league champion (isChampion true) only when isFinal is true",
        };
    }

    private static object ZonesPart(LeagueLevel level, FeederDivision division, GetStandingsReportHandler.ReportData data)
    {
        return new
        {
            authoritative = data.IsFinal,
            summary = ReportZones.ZoneSummary(level, division),
            note = data.IsFinal
                ? "Zones derive from final ranks and are authoritative for postseason movement."
                : "Provisional table: zones are not authoritative and are omitted per row.",
        };
    }

    private static object RowsPart(GetStandingsReportHandler.ReportData data)
    {
        return data.Rows.Select(r => new
        {
            rank = r.Rank,
            athleteId = r.AthleteId,
            name = r.Name,
            championshipPointsThousandths = r.TotalChampionshipPointsThousandths,
            stageScoreThousandths = r.TotalStageScoreThousandths,
            baseScoreThousandths = r.TotalBaseScoreThousandths,
            stageWins = r.StageWins,
            roundWins = r.RoundWins,
            isChampion = r.IsChampion,
            zone = r.Zone,
            zoneLabel = r.ZoneLabel,
        }).ToList();
    }
}
