using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Cups.GetChaosCupAthleteJourney;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one athlete's Chaos Cup
/// journey from persisted draws and official group standings without simulating,
/// drawing or touching sporting RNG: each drawn stage's group, official
/// placement/score/advancement when the group is complete, provisional
/// membership when it is not, plus exit-stage category or official Final place.
/// Eliminated athletes never receive a fabricated global numeric rank. Throws
/// <see cref="GetChaosCupAthleteJourneyNotFoundException"/> (404) when the
/// season has no edition, the athlete is unknown, or the athlete never entered
/// the frozen field. Aborts on corrupt linkage. Reads never lock and use a
/// fixed small set of batched queries (never N+1).
/// </summary>
public sealed class GetChaosCupAthleteJourneyHandler
{
    private readonly SaveStore _store;

    public GetChaosCupAthleteJourneyHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetChaosCupAthleteJourneyResponse> HandleAsync(
        Guid saveId,
        int athleteId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (athleteId <= 0)
        {
            throw new ArgumentException("Athlete id must be positive.", nameof(athleteId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        ChaosCupEditionEntity edition = await context.ChaosCupEditions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GetChaosCupAthleteJourneyNotFoundException(
                $"Chaos Cup journey for Season {source.SeasonNumber} has not been resolved yet.");
        ChaosCupInvariants.ValidateEdition(edition, rules);

        SaveAthleteEntity athlete = await context.SaveAthletes
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == athleteId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GetChaosCupAthleteJourneyNotFoundException(
                $"Athlete {athleteId} does not exist in this save.");

        ChaosCupEntrantEntity entrant = await context.ChaosCupEntrants
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id && e.SaveAthleteId == athleteId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GetChaosCupAthleteJourneyNotFoundException(
                $"Athlete {athleteId} never entered the Chaos Cup for Season {source.SeasonNumber}.");
        ValidateEntrant(source, entrant);

        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.SaveAthleteId == athleteId)
            .OrderBy(e => e.StageIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> standings = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.SaveAthleteId == athleteId)
            .OrderBy(e => e.StageIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<(int Stage, int Group), int> roundsByGroup = await LoadRoundCountsAsync(context, source, cancellationToken).ConfigureAwait(false);

        return BuildResponse(saveId, source, athlete, entrant, draws, standings, roundsByGroup);
    }

    internal static async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            return explicitSeason
                ?? throw new GetChaosCupAthleteJourneyNotFoundException(
                    $"Chaos Cup journey for Season {sourceSeasonNumber.Value} has not been resolved yet.");
        }

        ChaosCupEditionEntity? latest = await context.ChaosCupEditions
            .AsNoTracking()
            .OrderByDescending(e => e.SourceSeasonNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new GetChaosCupAthleteJourneyNotFoundException("Chaos Cup journey has not been resolved yet.");
        }

        SeasonEntity? source = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latest.SourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
        return source ?? throw new InvalidOperationException("Chaos Cup edition references an unknown season.");
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

    internal static void ValidateEntrant(SeasonEntity source, ChaosCupEntrantEntity entrant)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entrant);
        if (entrant.SourceSeasonId != source.Id || entrant.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Chaos Cup entrant {entrant.SaveAthleteId} has corrupt source linkage.");
        }

        if (!Enum.IsDefined((SportingColor)entrant.SportingColor))
        {
            throw new InvalidOperationException($"Chaos Cup entrant {entrant.SaveAthleteId} has unknown sporting color {entrant.SportingColor}.");
        }

        if (entrant.IsPoolInvite != (entrant.SourceLeagueId is null))
        {
            throw new InvalidOperationException($"Chaos Cup entrant {entrant.SaveAthleteId} has inconsistent Pool invitation linkage.");
        }

        if (entrant.EffectiveBonusThousandths < 0)
        {
            throw new InvalidOperationException($"Chaos Cup entrant {entrant.SaveAthleteId} has corrupt frozen bonus {entrant.EffectiveBonusThousandths}.");
        }
    }

    internal static GetChaosCupAthleteJourneyResponse BuildResponse(
        Guid saveId,
        SeasonEntity source,
        SaveAthleteEntity athlete,
        ChaosCupEntrantEntity entrant,
        List<ChaosCupDrawEntity> draws,
        List<ChaosCupGroupStandingEntity> standings,
        Dictionary<(int Stage, int Group), int> roundsByGroup)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(athlete);
        ArgumentNullException.ThrowIfNull(entrant);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(roundsByGroup);

        Dictionary<int, ChaosCupDrawEntity> drawByStage = ToDrawMap(draws);
        Dictionary<int, ChaosCupGroupStandingEntity> standingByStage = ToStandingMap(standings, drawByStage);
        (List<GetChaosCupAthleteJourneyResponse.StageEntry> stages, int? eliminatedStage) =
            BuildStages(drawByStage, standingByStage, roundsByGroup);
        EnsureNoDrawAfterElimination(drawByStage, eliminatedStage);
        return MapJourney(saveId, source, athlete, entrant, drawByStage, standingByStage, stages, eliminatedStage);
    }

    internal static Dictionary<int, ChaosCupDrawEntity> ToDrawMap(List<ChaosCupDrawEntity> draws)
    {
        ArgumentNullException.ThrowIfNull(draws);
        Dictionary<int, ChaosCupDrawEntity> drawByStage = new();
        foreach (ChaosCupDrawEntity draw in draws)
        {
            if (draw.StageIndex < 0 || draw.StageIndex >= ChaosCupFormat.StageCount)
            {
                throw new InvalidOperationException($"Chaos Cup journey stage {draw.StageIndex} is out of range.");
            }

            if (!drawByStage.TryAdd(draw.StageIndex, draw))
            {
                throw new InvalidOperationException($"Chaos Cup journey holds duplicate stage {draw.StageIndex} draw.");
            }

            int groupCount = ChaosCupFormat.GroupCountForStage(draw.StageIndex);
            if (draw.GroupNumber < 1 || draw.GroupNumber > groupCount)
            {
                throw new InvalidOperationException($"Chaos Cup journey stage {draw.StageIndex} group {draw.GroupNumber} is out of range.");
            }
        }

        return drawByStage;
    }

    internal static Dictionary<int, ChaosCupGroupStandingEntity> ToStandingMap(
        List<ChaosCupGroupStandingEntity> standings,
        Dictionary<int, ChaosCupDrawEntity> drawByStage)
    {
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(drawByStage);
        Dictionary<int, ChaosCupGroupStandingEntity> standingByStage = new();
        foreach (ChaosCupGroupStandingEntity standing in standings)
        {
            if (!drawByStage.TryGetValue(standing.StageIndex, out ChaosCupDrawEntity? draw))
            {
                throw new InvalidOperationException($"Chaos Cup journey stage {standing.StageIndex} ranks an undrawn athlete.");
            }

            CheckStandingLinkage(standing, draw);
            if (!standingByStage.TryAdd(standing.StageIndex, standing))
            {
                throw new InvalidOperationException($"Chaos Cup journey holds duplicate stage {standing.StageIndex} standing.");
            }
        }

        return standingByStage;
    }

    internal static void CheckStandingLinkage(ChaosCupGroupStandingEntity standing, ChaosCupDrawEntity draw)
    {
        ArgumentNullException.ThrowIfNull(standing);
        ArgumentNullException.ThrowIfNull(draw);
        if (standing.GroupNumber != draw.GroupNumber)
        {
            throw new InvalidOperationException($"Chaos Cup journey stage {standing.StageIndex} standing group {standing.GroupNumber} mismatches draw group {draw.GroupNumber}.");
        }

        if (standing.GroupRank < 1 || standing.GroupRank > ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException($"Chaos Cup journey stage {standing.StageIndex} rank {standing.GroupRank} is out of range.");
        }

        bool isFinal = ChaosCupFormat.IsFinalStage(standing.StageIndex);
        bool shouldAdvance = !isFinal && standing.GroupRank <= ChaosCupFormat.AdvancingPerGroup;
        if (standing.IsAdvanced != shouldAdvance)
        {
            throw new InvalidOperationException($"Chaos Cup journey stage {standing.StageIndex} rank {standing.GroupRank} has corrupt advancement flag.");
        }
    }

    internal static (List<GetChaosCupAthleteJourneyResponse.StageEntry> Stages, int? EliminatedStage) BuildStages(
        Dictionary<int, ChaosCupDrawEntity> drawByStage,
        Dictionary<int, ChaosCupGroupStandingEntity> standingByStage,
        Dictionary<(int Stage, int Group), int> roundsByGroup)
    {
        ArgumentNullException.ThrowIfNull(drawByStage);
        ArgumentNullException.ThrowIfNull(standingByStage);
        ArgumentNullException.ThrowIfNull(roundsByGroup);
        List<GetChaosCupAthleteJourneyResponse.StageEntry> stages = new(drawByStage.Count);
        int? eliminatedStage = null;
        foreach (int stage in drawByStage.Keys.OrderBy(s => s))
        {
            ChaosCupDrawEntity draw = drawByStage[stage];
            standingByStage.TryGetValue(stage, out ChaosCupGroupStandingEntity? standing);
            roundsByGroup.TryGetValue((stage, draw.GroupNumber), out int roundsPlayed);
            eliminatedStage = UpdateElimination(eliminatedStage, stage, standing);
            stages.Add(MapStage(stage, draw, standing, roundsPlayed));
        }

        return (stages, eliminatedStage);
    }

    internal static int? UpdateElimination(int? eliminatedStage, int stage, ChaosCupGroupStandingEntity? standing)
    {
        if (standing is null)
        {
            return eliminatedStage;
        }

        if (!ChaosCupFormat.IsFinalStage(stage) && !standing.IsAdvanced && eliminatedStage is null)
        {
            return stage;
        }

        return eliminatedStage;
    }

    internal static GetChaosCupAthleteJourneyResponse.StageEntry MapStage(
        int stage,
        ChaosCupDrawEntity draw,
        ChaosCupGroupStandingEntity? standing,
        int roundsPlayed)
    {
        ArgumentNullException.ThrowIfNull(draw);
        bool isFinal = ChaosCupFormat.IsFinalStage(stage);
        if (roundsPlayed is < 0 || roundsPlayed > ChaosCupFormat.RoundsPerGroup)
        {
            throw new InvalidOperationException($"Chaos Cup journey stage {stage} group {draw.GroupNumber} holds corrupt round count {roundsPlayed}.");
        }

        if (standing is null)
        {
            return MapProvisionalStage(stage, draw, isFinal, roundsPlayed);
        }

        if (roundsPlayed != ChaosCupFormat.RoundsPerGroup)
        {
            throw new InvalidOperationException($"Chaos Cup journey stage {stage} group {draw.GroupNumber} has standings but {roundsPlayed} rounds.");
        }

        return new GetChaosCupAthleteJourneyResponse.StageEntry(
            stage,
            ChaosCupFormat.GroupCountForStage(stage),
            draw.GroupNumber,
            isFinal,
            true,
            true,
            false,
            true,
            standing.GroupRank,
            standing.GroupScoreThousandths,
            standing.BaseScoreThousandths,
            standing.RoundWins,
            isFinal ? null : standing.IsAdvanced,
            roundsPlayed,
            ChaosCupFormat.RoundsPerGroup);
    }

    internal static GetChaosCupAthleteJourneyResponse.StageEntry MapProvisionalStage(
        int stage,
        ChaosCupDrawEntity draw,
        bool isFinal,
        int roundsPlayed)
    {
        ArgumentNullException.ThrowIfNull(draw);
        return new GetChaosCupAthleteJourneyResponse.StageEntry(
            stage,
            ChaosCupFormat.GroupCountForStage(stage),
            draw.GroupNumber,
            isFinal,
            true,
            false,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            roundsPlayed,
            ChaosCupFormat.RoundsPerGroup);
    }

    internal static void EnsureNoDrawAfterElimination(
        Dictionary<int, ChaosCupDrawEntity> drawByStage,
        int? eliminatedStage)
    {
        ArgumentNullException.ThrowIfNull(drawByStage);
        if (eliminatedStage.HasValue)
        {
            foreach (int later in drawByStage.Keys.Where(s => s > eliminatedStage.Value))
            {
                throw new InvalidOperationException($"Chaos Cup journey draws stage {later} after elimination in stage {eliminatedStage.Value}.");
            }
        }
    }

    internal static GetChaosCupAthleteJourneyResponse MapJourney(
        Guid saveId,
        SeasonEntity source,
        SaveAthleteEntity athlete,
        ChaosCupEntrantEntity entrant,
        Dictionary<int, ChaosCupDrawEntity> drawByStage,
        Dictionary<int, ChaosCupGroupStandingEntity> standingByStage,
        List<GetChaosCupAthleteJourneyResponse.StageEntry> stages,
        int? eliminatedStage)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(athlete);
        ArgumentNullException.ThrowIfNull(entrant);
        ArgumentNullException.ThrowIfNull(drawByStage);
        ArgumentNullException.ThrowIfNull(standingByStage);
        ArgumentNullException.ThrowIfNull(stages);
        bool isFinalist = standingByStage.ContainsKey(ChaosCupFormat.StageCount - 1);
        int? finalPlace = isFinalist ? standingByStage[ChaosCupFormat.StageCount - 1].GroupRank : null;
        string? exitStage = isFinalist
            ? nameof(ChaosCupFormat.ChaosExitStage.Finalist)
            : eliminatedStage.HasValue
                ? ChaosCupFormat.ExitStageForElimination(eliminatedStage.Value).ToString()
                : null;

        SportingColor color = (SportingColor)entrant.SportingColor;
        return new GetChaosCupAthleteJourneyResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            athlete.Id,
            athlete.Name,
            athlete.ImageUrl,
            entrant.SportingColor,
            color.ToString(),
            entrant.SourceLeagueId,
            entrant.SourceLeagueKind,
            entrant.SourceFeederDivision,
            entrant.IsPoolInvite,
            SourceLabelFor(entrant),
            entrant.EffectiveBonusThousandths,
            drawByStage.Count,
            isFinalist,
            eliminatedStage.HasValue,
            eliminatedStage,
            exitStage,
            finalPlace,
            stages);
    }

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
}
