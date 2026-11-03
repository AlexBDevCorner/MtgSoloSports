using System.Text.Json;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.CupEditionReport;

/// <summary>
/// JSON envelope builder for the Chaos Cup dossier (MSS-114). Split from the
/// handler so no single method exceeds the curated analyzer length limit.
/// Fixed-point thousandths stay integers; only Final places 1..32 are official.
/// </summary>
internal static class ChaosDossierJson
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(data);

        object envelope = new
        {
            schemaVersion = ReportContract.SchemaVersion,
            reportType = ReportContract.ChaosCupDossierReportType,
            saveId = saveId.ToString("D"),
            generatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            save = SavePart(metadata, rules),
            scope = ScopePart(data),
            snapshot = SnapshotPart(metadata, seasonCount, data),
            units = UnitsPart(),
            fieldBreakdown = FieldBreakdownPart(data),
            entrants = EntrantsPart(data),
            stages = StagesPart(data),
            paths = PathsPart(data),
            final = FinalPart(data),
        };
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
    }

    private static object SavePart(SaveMetadataEntity metadata, SimulationKernel.Rules.RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rules);
        return new
        {
            name = metadata.Name,
            currentSeason = metadata.CurrentSeason,
            phase = metadata.Phase,
            schemaVersion = metadata.SchemaVersion,
            rulesVersion = rules.Version,
        };
    }

    private static object ScopePart(ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new
        {
            cup = "Chaos",
            sourceSeasonNumber = data.SourceSeasonNumber,
            sourceSeasonId = data.SourceSeasonId,
        };
    }

    private static object SnapshotPart(SaveMetadataEntity metadata, int seasonCount, ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(data);
        return new
        {
            state = data.State,
            isComplete = data.IsComplete,
            fieldSize = data.FieldSize,
            stageCount = data.StageCount,
            totalGroups = data.TotalGroups,
            totalRounds = data.TotalRounds,
            roundsPlayed = data.RoundsPlayed,
            groupsCompleted = data.GroupsCompleted,
            rulesVersion = data.RulesVersion,
            chaosFormatVersion = data.ChaosFormatVersion,
            editionChecksum = data.EditionChecksum,
            seasonCount,
            currentSeason = metadata.CurrentSeason,
            phase = metadata.Phase,
            finalComplete = data.FinalStandings.Count != 0,
            champion = data.Champion,
        };
    }

    private static object UnitsPart()
    {
        return new
        {
            points = "fixed-point thousandths: divide by 1000 for display (e.g. 77000 = 77.000 group points)",
            chaosPoints = "Chaos group scores are isolated per 32-athlete group, reset every stage; they award no league championship points, promotion, or new bonus and never substitute season totals",
            careerLeaders = "Chaos group scores are additive-career neutral in this dossier; official Career Leaders and Superleague points leaders are separate statistics",
            missing = "null means unavailable/not-applicable; 0 is an exact zero, never a placeholder",
            ranks = "groupRank 1..32 is official only inside its completed group; the Final holds official places P1..P32; eliminated athletes carry only an exit-stage category with no global numeric rank",
            ordering = "entrants and paths are ordered by athleteId ascending for determinism; that order is not a ranking",
        };
    }

    private static object FieldBreakdownPart(ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new
        {
            total = data.FieldSize,
            byColor = data.ByColor.Select(b => new { key = b.Key, label = b.Label, count = b.Count }).ToList(),
            byDivision = data.ByDivision.Select(b => new { key = b.Key, label = b.Label, count = b.Count }).ToList(),
        };
    }

    private static object EntrantsPart(ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Entrants.Select(e => new
        {
            athleteId = e.AthleteId,
            name = e.Name,
            sportingColor = e.SportingColor,
            sportingColorName = e.SportingColorName,
            sourceLeagueId = e.SourceLeagueId,
            sourceLeagueKind = e.SourceLeagueKind,
            sourceFeederDivision = e.SourceFeederDivision,
            isPoolInvite = e.IsPoolInvite,
            sourceLabel = e.SourceLabel,
            effectiveBonusThousandths = e.EffectiveBonusThousandths,
        }).ToList();
    }

    private static object StagesPart(ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Stages.Select(StagePart).ToList();
    }

    private static object StagePart(ChaosDossierData.Stage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        return new
        {
            stageIndex = stage.StageIndex,
            groupCount = stage.GroupCount,
            entrantCount = stage.EntrantCount,
            isFinal = stage.IsFinal,
            state = stage.State,
            isDrawn = stage.IsDrawn,
            isComplete = stage.IsComplete,
            drawChecksum = stage.DrawChecksum,
            groupsCompleted = stage.GroupsCompleted,
            roundsPlayed = stage.RoundsPlayed,
            totalRounds = stage.TotalRounds,
            standingsCount = stage.StandingsCount,
            groups = stage.Groups.Select(GroupPart).ToList(),
        };
    }

    private static object GroupPart(ChaosDossierData.Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new
        {
            groupNumber = group.GroupNumber,
            isComplete = group.IsComplete,
            isProvisional = group.IsProvisional,
            roundsPlayed = group.RoundsPlayed,
            totalRounds = group.TotalRounds,
            standings = group.Standings.Select(r => new
            {
                athleteId = r.AthleteId,
                name = r.Name,
                groupRank = r.GroupRank,
                groupScoreThousandths = r.GroupScoreThousandths,
                baseScoreThousandths = r.BaseScoreThousandths,
                roundWins = r.RoundWins,
                isAdvanced = r.IsAdvanced,
            }).ToList(),
            cutoff = CutoffPart(group.Cutoff),
        };
    }

    private static object? CutoffPart(ChaosDossierData.Cutoff? cutoff)
    {
        if (cutoff is null)
        {
            return null;
        }

        return new
        {
            p16 = new
            {
                athleteId = cutoff.P16.AthleteId,
                name = cutoff.P16.Name,
                groupRank = cutoff.P16.GroupRank,
                groupScoreThousandths = cutoff.P16.GroupScoreThousandths,
            },
            p17 = new
            {
                athleteId = cutoff.P17.AthleteId,
                name = cutoff.P17.Name,
                groupRank = cutoff.P17.GroupRank,
                groupScoreThousandths = cutoff.P17.GroupScoreThousandths,
            },
            gapThousandths = cutoff.GapThousandths,
            isProvisional = cutoff.IsProvisional,
            isOfficial = cutoff.IsOfficial,
        };
    }

    private static object PathsPart(ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return data.Paths.Select(p => new
        {
            athleteId = p.AthleteId,
            name = p.Name,
            sportingColorName = p.SportingColorName,
            sourceLabel = p.SourceLabel,
            isPoolInvite = p.IsPoolInvite,
            effectiveBonusThousandths = p.EffectiveBonusThousandths,
            stagesEntered = p.StagesEntered,
            isFinalist = p.IsFinalist,
            isEliminated = p.IsEliminated,
            eliminatedStageIndex = p.EliminatedStageIndex,
            exitStage = p.ExitStage,
            finalPlace = p.FinalPlace,
            stages = p.Stages.Select(s => new
            {
                stageIndex = s.StageIndex,
                groupCount = s.GroupCount,
                groupNumber = s.GroupNumber,
                isFinal = s.IsFinal,
                isDrawn = s.IsDrawn,
                isComplete = s.IsComplete,
                standingsProvisional = s.StandingsProvisional,
                groupRank = s.GroupRank,
                groupScoreThousandths = s.GroupScoreThousandths,
                baseScoreThousandths = s.BaseScoreThousandths,
                roundWins = s.RoundWins,
                isAdvanced = s.IsAdvanced,
                roundsPlayed = s.RoundsPlayed,
                totalRounds = s.TotalRounds,
            }).ToList(),
        }).ToList();
    }

    private static object? FinalPart(ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.FinalStandings.Count == 0)
        {
            return null;
        }

        return new
        {
            teamCount = data.FinalStandings.Count,
            standings = data.FinalStandings.Select(r => new
            {
                athleteId = r.AthleteId,
                name = r.Name,
                finalPlace = r.GroupRank,
                finalScoreThousandths = r.GroupScoreThousandths,
                baseScoreThousandths = r.BaseScoreThousandths,
                roundWins = r.RoundWins,
            }).ToList(),
        };
    }
}
