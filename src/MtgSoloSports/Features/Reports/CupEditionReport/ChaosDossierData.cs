using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Reports.CupEditionReport;

/// <summary>
/// Versioned Chaos Cup dossier data (MSS-114). One authoritative snapshot for an
/// edition: frozen field, per-stage groups with official ranks/cutoffs, per-athlete
/// paths and the official Final. Built read-only from persisted draws/standings
/// plus batched round counts (never payloads, never RNG). Partial editions stay
/// honest: undrawn stages report zeros, drawn-but-incomplete groups list entrants
/// with progress and no invented ranks, and no champion is declared early.
/// Eliminated athletes carry only an exit-stage category; no fake global rank.
/// </summary>
internal sealed record ChaosDossierData(
    int SourceSeasonNumber,
    int SourceSeasonId,
    string State,
    bool IsComplete,
    int RulesVersion,
    int ChaosFormatVersion,
    int FieldSize,
    string EditionChecksum,
    int StageCount,
    int TotalGroups,
    int TotalRounds,
    int RoundsPlayed,
    int GroupsCompleted,
    IReadOnlyList<ChaosDossierData.Breakdown> ByColor,
    IReadOnlyList<ChaosDossierData.Breakdown> ByDivision,
    IReadOnlyList<ChaosDossierData.Entrant> Entrants,
    IReadOnlyList<ChaosDossierData.Stage> Stages,
    IReadOnlyList<ChaosDossierData.Path> Paths,
    IReadOnlyList<ChaosDossierData.Standing> FinalStandings,
    ChaosDossierData.ChampionRow? Champion)
{
    internal sealed record Breakdown(string Key, string Label, int Count);

    internal sealed record Entrant(
        int AthleteId,
        string Name,
        int SportingColor,
        string SportingColorName,
        int? SourceLeagueId,
        int SourceLeagueKind,
        int SourceFeederDivision,
        bool IsPoolInvite,
        string SourceLabel,
        int EffectiveBonusThousandths);

    internal sealed record Stage(
        int StageIndex,
        int GroupCount,
        int EntrantCount,
        bool IsFinal,
        string State,
        bool IsDrawn,
        bool IsComplete,
        string DrawChecksum,
        int GroupsCompleted,
        int RoundsPlayed,
        int TotalRounds,
        int StandingsCount,
        IReadOnlyList<Group> Groups);

    internal sealed record Group(
        int GroupNumber,
        bool IsComplete,
        bool IsProvisional,
        int RoundsPlayed,
        int TotalRounds,
        IReadOnlyList<Standing> Standings,
        Cutoff? Cutoff);

    internal sealed record Standing(
        int AthleteId,
        string Name,
        int GroupRank,
        int GroupScoreThousandths,
        int BaseScoreThousandths,
        int RoundWins,
        bool IsAdvanced);

    internal sealed record CutoffEntry(
        int AthleteId,
        string Name,
        int GroupRank,
        int GroupScoreThousandths);

    internal sealed record Cutoff(
        CutoffEntry P16,
        CutoffEntry P17,
        int GapThousandths,
        bool IsProvisional,
        bool IsOfficial);

    internal sealed record PathStage(
        int StageIndex,
        int GroupCount,
        int GroupNumber,
        bool IsFinal,
        bool IsDrawn,
        bool IsComplete,
        bool StandingsProvisional,
        int? GroupRank,
        int? GroupScoreThousandths,
        int? BaseScoreThousandths,
        int? RoundWins,
        bool? IsAdvanced,
        int RoundsPlayed,
        int TotalRounds);

    internal sealed record Path(
        int AthleteId,
        string Name,
        string SportingColorName,
        string SourceLabel,
        bool IsPoolInvite,
        int EffectiveBonusThousandths,
        int StagesEntered,
        bool IsFinalist,
        bool IsEliminated,
        int? EliminatedStageIndex,
        string? ExitStage,
        int? FinalPlace,
        IReadOnlyList<PathStage> Stages);

    internal sealed record ChampionRow(
        int AthleteId,
        string Name,
        int FinalPlace,
        int FinalScoreThousandths,
        int RoundWins);

    internal static async Task<ChaosDossierData> LoadAsync(
        SaveDbContext context,
        SeasonEntity source,
        SimulationKernel.Rules.RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);

        ChaosCupEditionEntity edition = await context.ChaosCupEditions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CupEditionReportNotFoundException(
                $"Chaos Cup edition for Season {source.SeasonNumber} has not been resolved yet.");
        ChaosCupInvariants.ValidateEdition(edition, rules);

        List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ChaosCupInvariants.ValidateEntrants(source, edition, entrants);

        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> standings = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<(int Stage, int Group), int> roundsByGroup = await LoadRoundCountsAsync(context, source, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> names = await LoadNamesAsync(context, entrants.Select(e => e.SaveAthleteId).ToList(), cancellationToken).ConfigureAwait(false);

        ValidateCounts(source, draws, standings);
        return Build(source, edition, entrants, draws, standings, roundsByGroup, names);
    }

    internal static async Task<Dictionary<(int Stage, int Group), int>> LoadRoundCountsAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        var rows = await context.ChaosCupRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .Select(e => new { e.StageIndex, e.GroupNumber })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<(int Stage, int Group), int> counts = new();
        foreach (var row in rows)
        {
            var key = (row.StageIndex, row.GroupNumber);
            counts.TryGetValue(key, out int current);
            counts[key] = checked(current + 1);
        }

        return counts;
    }

    internal static async Task<Dictionary<int, string>> LoadNamesAsync(
        SaveDbContext context,
        List<int> athleteIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(athleteIds);
        List<int> distinct = athleteIds.Distinct().ToList();
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => distinct.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        if (names.Count != distinct.Count)
        {
            throw new InvalidOperationException("Chaos Cup dossier references unknown athletes.");
        }

        return names;
    }

    internal static void ValidateCounts(
        SeasonEntity source,
        List<ChaosCupDrawEntity> draws,
        List<ChaosCupGroupStandingEntity> standings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(standings);
        foreach (int stage in Enumerable.Range(0, ChaosCupFormat.StageCount))
        {
            int expected = ChaosCupFormat.EntrantCountForStage(stage);
            int drawCount = draws.Count(d => d.StageIndex == stage);
            if (drawCount != 0 && drawCount != expected)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stage} holds {drawCount} draws, expected {expected}.");
            }

            int standingCount = standings.Count(s => s.StageIndex == stage);
            if (standingCount % ChaosCupFormat.GroupSize != 0)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stage} holds {standingCount} standings, expected multiples of {ChaosCupFormat.GroupSize}.");
            }

            if (standingCount > drawCount)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stage} holds {standingCount} standings for {drawCount} drawn athletes.");
            }
        }

        foreach (ChaosCupDrawEntity draw in draws)
        {
            if (draw.StageIndex < 0 || draw.StageIndex >= ChaosCupFormat.StageCount)
            {
                throw new InvalidOperationException($"Chaos Cup stage {draw.StageIndex} is out of range.");
            }
        }

        foreach (ChaosCupGroupStandingEntity standing in standings)
        {
            if (standing.StageIndex < 0 || standing.StageIndex >= ChaosCupFormat.StageCount)
            {
                throw new InvalidOperationException($"Chaos Cup stage {standing.StageIndex} is out of range.");
            }
        }
    }

    internal static ChaosDossierData Build(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        List<ChaosCupEntrantEntity> entrants,
        List<ChaosCupDrawEntity> draws,
        List<ChaosCupGroupStandingEntity> standings,
        Dictionary<(int Stage, int Group), int> roundsByGroup,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(roundsByGroup);
        ArgumentNullException.ThrowIfNull(names);

        List<Entrant> entrantRows = BuildEntrants(entrants, names);
        List<Stage> stages = BuildStages(draws, standings, roundsByGroup, names);
        List<Path> paths = BuildPaths(entrantRows, draws, standings, roundsByGroup);
        List<Standing> finalStandings = BuildFinalStandings(stages, names);
        ChampionRow? champion = finalStandings.Count == ChaosCupFormat.FinalFieldSize && stages.All(s => s.IsComplete)
            ? BuildChampion(finalStandings)
            : null;

        bool complete = stages.All(s => s.IsComplete);
        return new ChaosDossierData(
            source.SeasonNumber,
            source.Id,
            complete ? CupEditionState.Completed : CupEditionState.InProgress,
            complete,
            edition.RulesVersion,
            edition.ChaosFormatVersion,
            edition.FieldSize,
            edition.EditionChecksum,
            ChaosCupFormat.StageCount,
            ChaosCupFormat.StageGroupCounts.Sum(),
            checked(ChaosCupFormat.StageGroupCounts.Sum() * ChaosCupFormat.RoundsPerGroup),
            stages.Sum(s => s.RoundsPlayed),
            stages.Sum(s => s.GroupsCompleted),
            BuildByColor(entrants),
            BuildByDivision(entrants),
            entrantRows,
            stages,
            paths,
            finalStandings,
            champion);
    }

    internal static List<Entrant> BuildEntrants(
        List<ChaosCupEntrantEntity> entrants,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(names);
        return entrants
            .OrderBy(e => e.SaveAthleteId)
            .Select(e => new Entrant(
                e.SaveAthleteId,
                names[e.SaveAthleteId],
                e.SportingColor,
                ((SportingColor)e.SportingColor).ToString(),
                e.SourceLeagueId,
                e.SourceLeagueKind,
                e.SourceFeederDivision,
                e.IsPoolInvite,
                SourceLabelFor(e),
                e.EffectiveBonusThousandths))
            .ToList();
    }

    internal static List<Stage> BuildStages(
        List<ChaosCupDrawEntity> draws,
        List<ChaosCupGroupStandingEntity> standings,
        Dictionary<(int Stage, int Group), int> roundsByGroup,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(roundsByGroup);
        ArgumentNullException.ThrowIfNull(names);
        Dictionary<int, List<ChaosCupDrawEntity>> drawsByStage = draws
            .GroupBy(d => d.StageIndex)
            .ToDictionary(g => g.Key, g => g.ToList());
        Dictionary<int, List<ChaosCupGroupStandingEntity>> standingsByStage = standings
            .GroupBy(s => s.StageIndex)
            .ToDictionary(g => g.Key, g => g.ToList());
        List<Stage> stages = new(ChaosCupFormat.StageCount);
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            drawsByStage.TryGetValue(stage, out List<ChaosCupDrawEntity>? stageDraws);
            stageDraws ??= [];
            standingsByStage.TryGetValue(stage, out List<ChaosCupGroupStandingEntity>? stageStandings);
            stageStandings ??= [];
            stages.Add(BuildStage(stage, stageDraws, stageStandings, roundsByGroup, names));
        }

        return stages;
    }

    internal static Stage BuildStage(
        int stageIndex,
        List<ChaosCupDrawEntity> stageDraws,
        List<ChaosCupGroupStandingEntity> stageStandings,
        Dictionary<(int Stage, int Group), int> roundsByGroup,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(stageDraws);
        ArgumentNullException.ThrowIfNull(stageStandings);
        ArgumentNullException.ThrowIfNull(roundsByGroup);
        ArgumentNullException.ThrowIfNull(names);

        int groupCount = ChaosCupFormat.GroupCountForStage(stageIndex);
        int entrantCount = ChaosCupFormat.EntrantCountForStage(stageIndex);
        bool isFinal = ChaosCupFormat.IsFinalStage(stageIndex);
        bool isDrawn = stageDraws.Count != 0;
        int totalRounds = checked(groupCount * ChaosCupFormat.RoundsPerGroup);
        int roundsPlayed = SumStageRounds(stageIndex, stageDraws, roundsByGroup);
        List<Group> groups = BuildGroups(stageIndex, isFinal, isDrawn, stageDraws, stageStandings, roundsByGroup, names);
        return FinishStage(stageIndex, groupCount, entrantCount, isFinal, isDrawn, totalRounds, roundsPlayed, stageDraws, stageStandings, groups);
    }

    internal static int SumStageRounds(
        int stageIndex,
        List<ChaosCupDrawEntity> stageDraws,
        Dictionary<(int Stage, int Group), int> roundsByGroup)
    {
        ArgumentNullException.ThrowIfNull(stageDraws);
        ArgumentNullException.ThrowIfNull(roundsByGroup);
        int roundsPlayed = 0;
        foreach (ChaosCupDrawEntity draw in stageDraws)
        {
            // Count once per group: only the first draw row per group contributes.
            if (draw.DrawIndex != 0)
            {
                continue;
            }

            roundsByGroup.TryGetValue((stageIndex, draw.GroupNumber), out int played);
            roundsPlayed = checked(roundsPlayed + played);
        }

        return roundsPlayed;
    }

    internal static List<Group> BuildGroups(
        int stageIndex,
        bool isFinal,
        bool isDrawn,
        List<ChaosCupDrawEntity> stageDraws,
        List<ChaosCupGroupStandingEntity> stageStandings,
        Dictionary<(int Stage, int Group), int> roundsByGroup,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(stageDraws);
        ArgumentNullException.ThrowIfNull(stageStandings);
        ArgumentNullException.ThrowIfNull(roundsByGroup);
        ArgumentNullException.ThrowIfNull(names);
        Dictionary<int, List<ChaosCupGroupStandingEntity>> standingsByGroup = stageStandings
            .GroupBy(s => s.GroupNumber)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.GroupRank).ToList());
        List<int> groupNumbers = stageDraws.Select(d => d.GroupNumber).Distinct().OrderBy(g => g).ToList();
        List<Group> groups = new(groupNumbers.Count);
        foreach (int groupNumber in groupNumbers)
        {
            standingsByGroup.TryGetValue(groupNumber, out List<ChaosCupGroupStandingEntity>? groupStandings);
            groupStandings ??= [];
            groups.Add(BuildGroup(stageIndex, groupNumber, isFinal, isDrawn, groupStandings, roundsByGroup, names));
        }

        return groups;
    }

    internal static Group BuildGroup(
        int stageIndex,
        int groupNumber,
        bool isFinal,
        bool isDrawn,
        List<ChaosCupGroupStandingEntity> groupStandings,
        Dictionary<(int Stage, int Group), int> roundsByGroup,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(groupStandings);
        ArgumentNullException.ThrowIfNull(roundsByGroup);
        ArgumentNullException.ThrowIfNull(names);
        roundsByGroup.TryGetValue((stageIndex, groupNumber), out int played);
        bool groupComplete = played == ChaosCupFormat.RoundsPerGroup
            && groupStandings.Count == ChaosCupFormat.GroupSize;
        List<Standing> standingRows = groupComplete
            ? BuildStandingRows(groupStandings, names)
            : [];
        Cutoff? cutoff = groupComplete && !isFinal
            ? BuildCutoff(groupStandings, names)
            : null;
        return new Group(
            groupNumber,
            groupComplete,
            isDrawn && !groupComplete,
            played,
            ChaosCupFormat.RoundsPerGroup,
            standingRows,
            cutoff);
    }

    internal static List<Standing> BuildStandingRows(
        List<ChaosCupGroupStandingEntity> groupStandings,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(groupStandings);
        ArgumentNullException.ThrowIfNull(names);
        return groupStandings
            .OrderBy(s => s.GroupRank)
            .Select(s => new Standing(
                s.SaveAthleteId,
                names[s.SaveAthleteId],
                s.GroupRank,
                s.GroupScoreThousandths,
                s.BaseScoreThousandths,
                s.RoundWins,
                s.IsAdvanced))
            .ToList();
    }

    internal static Stage FinishStage(
        int stageIndex,
        int groupCount,
        int entrantCount,
        bool isFinal,
        bool isDrawn,
        int totalRounds,
        int roundsPlayed,
        List<ChaosCupDrawEntity> stageDraws,
        List<ChaosCupGroupStandingEntity> stageStandings,
        List<Group> groups)
    {
        ArgumentNullException.ThrowIfNull(stageDraws);
        ArgumentNullException.ThrowIfNull(stageStandings);
        ArgumentNullException.ThrowIfNull(groups);
        int groupsCompleted = groups.Count(g => g.IsComplete);
        int standingsCount = stageStandings.Count;
        bool isComplete = isDrawn
            && stageDraws.Count == entrantCount
            && roundsPlayed == totalRounds
            && standingsCount == entrantCount;
        string state = !isDrawn
            ? GetChaosCupEditionState.NotDrawn
            : isComplete ? CupEditionState.Completed : CupEditionState.InProgress;
        string checksum = stageDraws.Count > 0 ? stageDraws[0].DrawChecksum : string.Empty;
        return new Stage(
            stageIndex,
            groupCount,
            entrantCount,
            isFinal,
            state,
            isDrawn,
            isComplete,
            checksum,
            groupsCompleted,
            roundsPlayed,
            totalRounds,
            standingsCount,
            groups);
    }

    internal static Cutoff BuildCutoff(
        List<ChaosCupGroupStandingEntity> groupStandings,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(groupStandings);
        ArgumentNullException.ThrowIfNull(names);
        List<ChaosCupGroupStandingEntity> ordered = groupStandings.OrderBy(s => s.GroupRank).ToList();
        if (ordered.Count != ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException("Chaos Cup dossier cutoff requires a complete 32-athlete group.");
        }

        ChaosCupGroupStandingEntity sixteenth = ordered[ChaosCupFormat.AdvancingPerGroup - 1];
        ChaosCupGroupStandingEntity seventeenth = ordered[ChaosCupFormat.AdvancingPerGroup];
        if (sixteenth.GroupRank != ChaosCupFormat.AdvancingPerGroup
            || seventeenth.GroupRank != ChaosCupFormat.AdvancingPerGroup + 1)
        {
            throw new InvalidOperationException("Chaos Cup dossier cutoff requires official ranks 16 and 17.");
        }

        return new Cutoff(
            new CutoffEntry(sixteenth.SaveAthleteId, names[sixteenth.SaveAthleteId], sixteenth.GroupRank, sixteenth.GroupScoreThousandths),
            new CutoffEntry(seventeenth.SaveAthleteId, names[seventeenth.SaveAthleteId], seventeenth.GroupRank, seventeenth.GroupScoreThousandths),
            checked(sixteenth.GroupScoreThousandths - seventeenth.GroupScoreThousandths),
            false,
            true);
    }

    internal static List<Path> BuildPaths(
        List<Entrant> entrantRows,
        List<ChaosCupDrawEntity> draws,
        List<ChaosCupGroupStandingEntity> standings,
        Dictionary<(int Stage, int Group), int> roundsByGroup)
    {
        ArgumentNullException.ThrowIfNull(entrantRows);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(roundsByGroup);

        Dictionary<(int Athlete, int Stage), ChaosCupDrawEntity> drawByAthleteStage = draws
            .ToDictionary(d => (d.SaveAthleteId, d.StageIndex));
        Dictionary<(int Athlete, int Stage), ChaosCupGroupStandingEntity> standingByAthleteStage = standings
            .ToDictionary(s => (s.SaveAthleteId, s.StageIndex));

        List<Path> paths = new(entrantRows.Count);
        foreach (Entrant entrant in entrantRows.OrderBy(e => e.AthleteId))
        {
            paths.Add(BuildOnePath(entrant, drawByAthleteStage, standingByAthleteStage, roundsByGroup));
        }

        return paths;
    }

    internal static Path BuildOnePath(
        Entrant entrant,
        Dictionary<(int Athlete, int Stage), ChaosCupDrawEntity> drawByAthleteStage,
        Dictionary<(int Athlete, int Stage), ChaosCupGroupStandingEntity> standingByAthleteStage,
        Dictionary<(int Stage, int Group), int> roundsByGroup)
    {
        ArgumentNullException.ThrowIfNull(entrant);
        ArgumentNullException.ThrowIfNull(drawByAthleteStage);
        ArgumentNullException.ThrowIfNull(standingByAthleteStage);
        ArgumentNullException.ThrowIfNull(roundsByGroup);
        List<PathStage> stages = new(ChaosCupFormat.StageCount);
        int? eliminatedStage = null;
        foreach (int stage in Enumerable.Range(0, ChaosCupFormat.StageCount))
        {
            PathStage? built = TryBuildPathStage(entrant.AthleteId, stage, drawByAthleteStage, standingByAthleteStage, roundsByGroup, ref eliminatedStage);
            if (built is not null)
            {
                stages.Add(built);
            }
        }

        return FinishPath(entrant, stages, eliminatedStage);
    }

    internal static PathStage? TryBuildPathStage(
        int athleteId,
        int stage,
        Dictionary<(int Athlete, int Stage), ChaosCupDrawEntity> drawByAthleteStage,
        Dictionary<(int Athlete, int Stage), ChaosCupGroupStandingEntity> standingByAthleteStage,
        Dictionary<(int Stage, int Group), int> roundsByGroup,
        ref int? eliminatedStage)
    {
        ArgumentNullException.ThrowIfNull(drawByAthleteStage);
        ArgumentNullException.ThrowIfNull(standingByAthleteStage);
        ArgumentNullException.ThrowIfNull(roundsByGroup);
        if (!drawByAthleteStage.TryGetValue((athleteId, stage), out ChaosCupDrawEntity? draw))
        {
            return null;
        }

        roundsByGroup.TryGetValue((stage, draw.GroupNumber), out int played);
        bool hasStanding = standingByAthleteStage.TryGetValue((athleteId, stage), out ChaosCupGroupStandingEntity? standing);
        bool groupComplete = played == ChaosCupFormat.RoundsPerGroup && hasStanding;
        if (hasStanding && !groupComplete)
        {
            throw new InvalidOperationException($"Chaos Cup dossier holds standings for incomplete group {stage}/{draw.GroupNumber}.");
        }

        if (hasStanding && !ChaosCupFormat.IsFinalStage(stage) && !standing!.IsAdvanced && eliminatedStage is null)
        {
            eliminatedStage = stage;
        }

        return new PathStage(
            stage,
            ChaosCupFormat.GroupCountForStage(stage),
            draw.GroupNumber,
            ChaosCupFormat.IsFinalStage(stage),
            true,
            groupComplete,
            !groupComplete,
            groupComplete ? standing!.GroupRank : null,
            groupComplete ? standing!.GroupScoreThousandths : null,
            groupComplete ? standing!.BaseScoreThousandths : null,
            groupComplete ? standing!.RoundWins : null,
            groupComplete && !ChaosCupFormat.IsFinalStage(stage) ? standing!.IsAdvanced : null,
            played,
            ChaosCupFormat.RoundsPerGroup);
    }

    internal static Path FinishPath(Entrant entrant, List<PathStage> stages, int? eliminatedStage)
    {
        ArgumentNullException.ThrowIfNull(entrant);
        ArgumentNullException.ThrowIfNull(stages);
        bool isFinalist = stages.Any(s => s.IsFinal && s.IsComplete);
        int? finalPlace = isFinalist ? stages.Single(s => s.IsFinal).GroupRank : null;
        if (isFinalist && eliminatedStage.HasValue)
        {
            throw new InvalidOperationException($"Chaos Cup dossier draws stage {eliminatedStage.Value} elimination plus a Final for athlete {entrant.AthleteId}.");
        }

        if (eliminatedStage.HasValue)
        {
            EnsureNoDrawAfterElimination(stages, eliminatedStage.Value);
        }

        string? exitStage = isFinalist
            ? nameof(ChaosCupFormat.ChaosExitStage.Finalist)
            : eliminatedStage.HasValue
                ? ChaosCupFormat.ExitStageForElimination(eliminatedStage.Value).ToString()
                : null;
        return new Path(
            entrant.AthleteId,
            entrant.Name,
            entrant.SportingColorName,
            entrant.SourceLabel,
            entrant.IsPoolInvite,
            entrant.EffectiveBonusThousandths,
            stages.Count,
            isFinalist,
            eliminatedStage.HasValue,
            eliminatedStage,
            exitStage,
            finalPlace,
            stages);
    }

    internal static void EnsureNoDrawAfterElimination(List<PathStage> stages, int eliminatedStage)
    {
        ArgumentNullException.ThrowIfNull(stages);
        foreach (PathStage later in stages.Where(s => s.StageIndex > eliminatedStage))
        {
            throw new InvalidOperationException($"Chaos Cup dossier draws stage {later.StageIndex} after elimination in stage {eliminatedStage}.");
        }
    }

    internal static List<Standing> BuildFinalStandings(List<Stage> stages, Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(names);
        Stage final = stages.Single(s => s.IsFinal);
        if (!final.IsComplete || final.Groups.Count != 1)
        {
            return [];
        }

        List<Standing> ordered = final.Groups[0].Standings.OrderBy(s => s.GroupRank).ToList();
        if (ordered.Count != ChaosCupFormat.FinalFieldSize)
        {
            throw new InvalidOperationException("Chaos Cup dossier Final must hold exactly 32 official places.");
        }

        for (int rank = 1; rank <= ordered.Count; rank++)
        {
            if (ordered[rank - 1].GroupRank != rank)
            {
                throw new InvalidOperationException("Chaos Cup dossier Final must cover places 1..32 exactly once.");
            }
        }

        return ordered;
    }

    internal static ChampionRow? BuildChampion(List<Standing> finalStandings)
    {
        ArgumentNullException.ThrowIfNull(finalStandings);
        if (finalStandings.Count == 0)
        {
            return null;
        }

        Standing winner = finalStandings.OrderBy(s => s.GroupRank).First();
        if (winner.GroupRank != 1)
        {
            throw new InvalidOperationException("Chaos Cup dossier champion must hold Final place 1.");
        }

        return new ChampionRow(winner.AthleteId, winner.Name, 1, winner.GroupScoreThousandths, winner.RoundWins);
    }

    internal static IReadOnlyList<Breakdown> BuildByColor(List<ChaosCupEntrantEntity> entrants)
    {
        ArgumentNullException.ThrowIfNull(entrants);
        return Enum.GetValues<SportingColor>()
            .OrderBy(c => c.ToString(), StringComparer.Ordinal)
            .Select(color => new Breakdown(
                color.ToString(),
                color.ToString(),
                entrants.Count(e => e.SportingColor == (int)color)))
            .ToList();
    }

    internal static IReadOnlyList<Breakdown> BuildByDivision(List<ChaosCupEntrantEntity> entrants)
    {
        ArgumentNullException.ThrowIfNull(entrants);
        string[] order = ["Pool", "Superleague", "Feeder1", "Feeder2", "Feeder3"];
        return order
            .Select(key => new Breakdown(
                key,
                DivisionLabelFor(key),
                entrants.Count(e => string.Equals(DivisionKeyFor(e), key, StringComparison.Ordinal))))
            .ToList();
    }

    internal static string DivisionKeyFor(ChaosCupEntrantEntity entrant)
    {
        ArgumentNullException.ThrowIfNull(entrant);
        if (entrant.IsPoolInvite)
        {
            return "Pool";
        }

        if (entrant.SourceLeagueKind == (int)LeagueKind.Superleague)
        {
            return "Superleague";
        }

        return LeagueHierarchy.LevelForDivision((FeederDivision)entrant.SourceFeederDivision, isSuperleague: false) switch
        {
            LeagueLevel.Feeder1 => "Feeder1",
            LeagueLevel.Feeder2 => "Feeder2",
            LeagueLevel.Feeder3 => "Feeder3",
            _ => throw new InvalidOperationException($"Chaos Cup entrant {entrant.SaveAthleteId} has unknown division {entrant.SourceFeederDivision}."),
        };
    }

    internal static string DivisionLabelFor(string key) => key switch
    {
        "Pool" => "Pool",
        "Superleague" => "Superleague",
        "Feeder1" => "Feeder 1",
        "Feeder2" => "Feeder 2",
        "Feeder3" => "Feeder 3",
        _ => throw new ArgumentOutOfRangeException(nameof(key), $"Unknown Chaos division key '{key}'."),
    };

    internal static string SourceLabelFor(ChaosCupEntrantEntity entrant)
    {
        ArgumentNullException.ThrowIfNull(entrant);
        if (entrant.IsPoolInvite)
        {
            return "Pool";
        }

        if (entrant.SourceLeagueKind == (int)LeagueKind.Superleague)
        {
            return "Superleague";
        }

        return LeagueHierarchy.DisplayName(
            LeagueHierarchy.LevelForDivision((FeederDivision)entrant.SourceFeederDivision, isSuperleague: false));
    }

    private static class GetChaosCupEditionState
    {
        public const string NotDrawn = "NotDrawn";
    }
}
