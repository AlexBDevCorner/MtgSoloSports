using System.Text.Json;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal static class UniverseAlmanacJson
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        UniverseAlmanacData data)
    {
        object envelope = new
        {
            schemaVersion = ReportContract.SchemaVersion,
            reportType = ReportContract.UniverseAlmanacReportType,
            saveId = saveId.ToString("D"),
            generatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            save = SavePart(metadata, rules),
            scope = new
            {
                fromSeasonNumber = data.FromSeasonNumber,
                toSeasonNumber = data.ToSeasonNumber,
                seasonCount = data.SeasonCount,
            },
            snapshot = SnapshotPart(metadata, data),
            units = UnitsPart(),
            seasons = data.Seasons,
            superleaguePodiums = data.SuperleaguePodiums,
            feederTitles = data.FeederTitles,
            cupEditions = data.CupEditions,
            colorTeamTable = data.ColorTeamTable,
            colorIndividualMedals = data.ColorIndividualMedals,
            colorCombinedMedals = data.ColorCombinedMedals,
            typeTeamTable = data.TypeTeamTable,
            careerRecords = data.CareerRecords,
            careerLeaders = LeadersPart(data.WeightedLeaders),
            superleaguePointsLeaders = data.SuperleaguePointsLeaders,
            titleStreaks = data.TitleStreaks,
            stageWinStreaks = data.StageWinStreaks,
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

    private static object SnapshotPart(SaveMetadataEntity metadata, UniverseAlmanacData data)
    {
        return new
        {
            seasonCount = data.SeasonCount,
            fromSeasonNumber = data.FromSeasonNumber,
            toSeasonNumber = data.ToSeasonNumber,
            currentSeason = metadata.CurrentSeason,
            phase = metadata.Phase,
            staleWarning = "Uploads become stale as simulation advances; re-export the almanac occasionally and a fresh season dossier for new coverage.",
        };
    }

    private static object UnitsPart()
    {
        return new
        {
            careerPoints = "weighted Hall of Fame career points (weights version labelled); higher is better across leagues and major Cups",
            leaguePoints = "official Total league points in fixed-point thousandths: sum of final season championship points earned while competing in that league (divide by 1000); never mixed with weighted career points",
            qualification = "Type Cup qualification never awards titles; only Final ranks 1-3 hold official medals. Color individual and team events are separate titles.",
            missing = "null means unavailable/not-applicable; 0 is an exact zero, never a placeholder",
        };
    }

    private static object LeadersPart(AlmanacCareerLeaders leaders)
    {
        return new
        {
            weightsVersion = leaders.WeightsVersion,
            totalAthletes = leaders.TotalAthletes,
            fromSeason = leaders.FromSeason,
            toSeason = leaders.ToSeason,
            leaders = leaders.Leaders,
        };
    }
}
