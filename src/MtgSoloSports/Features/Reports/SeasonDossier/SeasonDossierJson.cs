using System.Text.Json;
using MtgSoloSports.Features.Reports.StandingsReport;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal static class SeasonDossierJson
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        SeasonDossierData data)
    {
        object envelope = new
        {
            schemaVersion = ReportContract.SchemaVersion,
            reportType = ReportContract.SeasonDossierReportType,
            saveId = saveId.ToString("D"),
            generatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            save = SavePart(metadata, rules),
            scope = new
            {
                seasonNumber = data.SeasonNumber,
                seasonId = data.SeasonId,
                seasonCount,
            },
            snapshot = SnapshotPart(metadata, data),
            units = UnitsPart(),
            superleague = SuperleaguePart(data),
            stageProgression = data.StageProgression,
            feeders = data.FeederSummaries,
            postseason = data.Postseason,
            colorCup = CupPart(data.ColorCup),
            typeCup = CupPart(data.TypeCup),
            history = HistoryPart(data.History),
            careerRecords = data.CareerRecords,
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

    private static object SnapshotPart(SaveMetadataEntity metadata, SeasonDossierData data)
    {
        return new
        {
            seasonNumber = data.SeasonNumber,
            isSeasonComplete = data.IsComplete,
            status = data.Status,
            isFinal = data.IsFinal,
            completedStages = data.CompletedStages,
            totalStages = data.TotalStages,
            globalStage = data.GlobalStage,
            seasonChecksum = data.SeasonChecksum,
            currentSeason = metadata.CurrentSeason,
            phase = metadata.Phase,
            staleWarning = "Uploads become stale as simulation advances; re-export after new stages or postseason events.",
        };
    }

    private static object UnitsPart()
    {
        return new
        {
            championshipPoints = "official season championship points in fixed-point thousandths (divide by 1000; 32-position table, no bonus multiplier)",
            stageScore = "persisted stage-score sums in fixed-point thousandths (display only)",
            weightedCareerPoints = "weighted Hall of Fame career points are never in this dossier; leader titles below are official league honours only",
            qualification = "Type Cup qualification never awards titles; only Final ranks 1-3 hold official medals. Color individual and team events are separate titles.",
            status = "final = persisted official table; provisional = cumulative from completed stages only; pending/not-held = omitted, never inferred",
        };
    }

    private static object SuperleaguePart(SeasonDossierData data)
    {
        return new
        {
            leagueId = data.Superleague.LeagueId,
            leagueName = data.Superleague.LeagueName,
            isOfficialSuperleague = data.Superleague.IsOfficialSuperleague,
            isFinal = data.IsFinal,
            table = data.Superleague.Table.Select(r => new
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
            }).ToList(),
            stagePlacements = SuperleagueStagePlacementsPart(data),
        };
    }

    private static object SuperleagueStagePlacementsPart(SeasonDossierData data)
    {
        return new
        {
            version = StagePlacementReport.ProjectionVersion,
            leagueId = data.Superleague.LeagueId,
            leagueName = data.Superleague.LeagueName,
            isOfficialSuperleague = data.Superleague.IsOfficialSuperleague,
            completedStages = data.CompletedStages,
            totalStages = data.TotalStages,
            stageNumbers = StagePlacementReport.StageNumbers(data.Superleague.StagePlacements).ToList(),
            units = new
            {
                stageRank = "per-stage finish P1..P32 within one completed stage; never cumulative seasonRank",
                stageNumber = "chronological completed stage 1..totalStages; absent means unplayed/in-progress, never P32 or zero",
                championshipPointsThousandths = "official per-stage championship points in fixed-point thousandths (divide by 1000); superleague table totals stay cumulative",
                earnedBonusThousandths = "bonus earned during this stage in fixed-point thousandths; applies only from the next stage",
                stageScoreThousandths = "persisted stage-score sum in fixed-point thousandths (display only; tie-break input)",
                roundWins = "rounds won within this stage (0..roundsPerStage)",
            },
            note = "Every completed stage lists all 32 roster athletes exactly once. Winners-only stageProgression is preserved separately; this trajectory never substitutes for it.",
            placements = data.Superleague.StagePlacements.Select(c => new
            {
                stageNumber = c.StageNumber,
                athleteId = c.AthleteId,
                stageRank = c.StageRank,
                championshipPointsThousandths = c.ChampionshipPointsThousandths,
                earnedBonusThousandths = c.EarnedBonusThousandths,
                stageScoreThousandths = c.StageScoreThousandths,
                roundWins = c.RoundWins,
            }).ToList(),
        };
    }

    private static object? CupPart(CupSummaryEntry? cup)
    {
        if (cup is null)
        {
            return null;
        }

        return new
        {
            cup = cup.Cup,
            state = cup.State,
            hasSelection = cup.HasSelection,
            individual = cup.Individual,
            team = cup.Team,
            tournament = cup.Tournament,
            note = cup.Note,
        };
    }

    private static object HistoryPart(HistorySection history)
    {
        return new
        {
            previousSeasonNumber = history.PreviousSeasonNumber,
            previousChampion = history.PreviousChampion,
            defendingChampion = history.DefendingChampion,
            championsLastFive = history.ChampionsLastFive,
            leaderCareers = history.LeaderCareers,
            cupPerformerCareers = history.CupPerformerCareers,
        };
    }
}
