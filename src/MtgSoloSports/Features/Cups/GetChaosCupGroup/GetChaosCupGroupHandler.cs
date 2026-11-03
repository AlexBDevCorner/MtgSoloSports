using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.RunChaosCupGroup;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Cups.GetChaosCupGroup;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one drawn Chaos Cup
/// group and one persisted game round without simulating, drawing or touching
/// sporting RNG: 32 entrants with frozen source tier/Pool, progress, standings
/// (official once complete, provisional before) with the P16/P17 cutoff, plus
/// single-round immutable replay. A stage without a persisted draw and a round
/// that has not been played yet 404 via
/// <see cref="GetChaosCupGroupNotFoundException"/> and are never portrayed as
/// complete. Reads never lock.
/// </summary>
public sealed class GetChaosCupGroupHandler
{
    private readonly SaveStore _store;

    public GetChaosCupGroupHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetChaosCupGroupResponse> HandleGroupAsync(
        Guid saveId,
        int stageIndex,
        int groupNumber,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        ValidateGroupRequest(saveId, stageIndex, groupNumber, sourceSeasonNumber);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        LoadedGroup loaded = await LoadGroupAsync(context, stageIndex, groupNumber, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        return MapGroupResponse(saveId, loaded);
    }

    public async Task<GetChaosCupRoundResponse> HandleRoundAsync(
        Guid saveId,
        int stageIndex,
        int groupNumber,
        int roundNumber,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        ValidateGroupRequest(saveId, stageIndex, groupNumber, sourceSeasonNumber);
        if (roundNumber < 1 || roundNumber > ChaosCupFormat.RoundsPerGroup)
        {
            throw new ArgumentException(
                $"Chaos Cup round {roundNumber} is out of range (1..{ChaosCupFormat.RoundsPerGroup}).", nameof(roundNumber));
        }

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        LoadedGroup loaded = await LoadGroupAsync(context, stageIndex, groupNumber, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        ChaosCupRoundPayloadDocument payload = loaded.Played.SingleOrDefault(p => p.RoundNumber == roundNumber)
            ?? throw new GetChaosCupGroupNotFoundException(
                $"Chaos Cup stage {stageIndex} group {groupNumber} round {roundNumber} has not been played yet.");
        Dictionary<int, string?> images = await LoadImagesAsync(context, payload, cancellationToken).ConfigureAwait(false);
        return MapRoundResponse(saveId, loaded, payload, images);
    }

    private static void ValidateGroupRequest(Guid saveId, int stageIndex, int groupNumber, int? sourceSeasonNumber)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        if (stageIndex < 0 || stageIndex >= ChaosCupFormat.StageCount)
        {
            throw new ArgumentException(
                $"Chaos Cup stage {stageIndex} is out of range (0..{ChaosCupFormat.StageCount - 1}).", nameof(stageIndex));
        }

        int groupCount = ChaosCupFormat.GroupCountForStage(stageIndex);
        if (groupNumber < 1 || groupNumber > groupCount)
        {
            throw new ArgumentException(
                $"Chaos Cup stage {stageIndex} group {groupNumber} is out of range (1..{groupCount}).", nameof(groupNumber));
        }
    }

    private sealed record LoadedGroup(
        SeasonEntity Source,
        ChaosCupEditionEntity Edition,
        int StageIndex,
        int GroupNumber,
        List<ChaosCupDrawEntity> GroupDraws,
        Dictionary<int, ChaosCupEntrantEntity> EntrantsByAthlete,
        Dictionary<int, string> NamesByAthlete,
        List<ChaosCupRoundPayloadDocument> Played,
        List<ChaosCupGroupStandingEntity> PersistedStandings);

    private static async Task<LoadedGroup> LoadGroupAsync(
        SaveDbContext context,
        int stageIndex,
        int groupNumber,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        ChaosCupEditionEntity edition = await LoadEditionAsync(context, source, cancellationToken).ConfigureAwait(false);
        ChaosCupInvariants.ValidateEdition(edition, rules);
        List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ChaosCupInvariants.ValidateEntrants(source, edition, entrants);
        (List<ChaosCupDrawEntity> stageDraws, List<ChaosCupDrawEntity> groupDraws) =
            await LoadDrawsAsync(context, source, edition, entrants, stageIndex, groupNumber, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> names = await LoadNamesAsync(context, groupDraws, cancellationToken).ConfigureAwait(false);
        List<ChaosCupRoundPayloadDocument> played = await LoadPlayedAsync(
            context, source, edition, rules, stageDraws, stageIndex, groupNumber, cancellationToken).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> persisted = await LoadPersistedStandingsAsync(
            context, source, stageIndex, groupNumber, played.Count, cancellationToken).ConfigureAwait(false);
        return new LoadedGroup(
            source,
            edition,
            stageIndex,
            groupNumber,
            groupDraws,
            entrants.ToDictionary(e => e.SaveAthleteId),
            names,
            played,
            persisted);
    }

    private static async Task<(List<ChaosCupDrawEntity> StageDraws, List<ChaosCupDrawEntity> GroupDraws)> LoadDrawsAsync(
        SaveDbContext context,
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        List<ChaosCupEntrantEntity> entrants,
        int stageIndex,
        int groupNumber,
        CancellationToken cancellationToken)
    {
        List<ChaosCupDrawEntity> stageDraws = await context.ChaosCupDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (stageDraws.Count == 0)
        {
            throw new GetChaosCupGroupNotFoundException(
                $"Chaos Cup stage {stageIndex} group {groupNumber} has no persisted draw yet for Season {source.SeasonNumber}.");
        }

        ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, stageDraws, stageIndex);
        List<ChaosCupDrawEntity> groupDraws = stageDraws
            .Where(d => d.GroupNumber == groupNumber)
            .OrderBy(d => d.DrawIndex)
            .ToList();
        if (groupDraws.Count != ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException(
                $"Chaos Cup stage {stageIndex} group {groupNumber} must draw exactly {ChaosCupFormat.GroupSize} athletes, was {groupDraws.Count}.");
        }

        return (stageDraws, groupDraws);
    }

    private static async Task<List<ChaosCupRoundPayloadDocument>> LoadPlayedAsync(
        SaveDbContext context,
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        SimulationKernel.Rules.RulesV1 rules,
        List<ChaosCupDrawEntity> stageDraws,
        int stageIndex,
        int groupNumber,
        CancellationToken cancellationToken)
    {
        List<ChaosCupRoundEntity> rows = await context.ChaosCupRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.GroupNumber == groupNumber)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ChaosCupInvariants.ValidateGroupRoundsContiguous(rows, stageIndex, groupNumber);
        List<ChaosCupRoundPayloadDocument> played = new(rows.Count);
        foreach (ChaosCupRoundEntity row in rows)
        {
            ChaosCupInvariants.ValidatePersistedRound(row, source, edition, rules);
            played.Add(ChaosCupRoundPayloadDocument.FromStored(row.PayloadJson));
        }

        if (played.Count > 0)
        {
            var groupStart = ChaosCupGroupSimulation.DeriveGroupRng(edition, stageDraws, stageIndex, groupNumber);
            if (played[0].RngBeforeState != groupStart.State || played[0].RngBeforeStream != groupStart.Stream)
            {
                throw new InvalidOperationException(
                    $"Chaos Cup stage {stageIndex} group {groupNumber} first round does not start from its isolated group RNG.");
            }
        }

        return played;
    }

    private static async Task<List<ChaosCupGroupStandingEntity>> LoadPersistedStandingsAsync(
        SaveDbContext context,
        SeasonEntity source,
        int stageIndex,
        int groupNumber,
        int playedCount,
        CancellationToken cancellationToken)
    {
        if (playedCount != ChaosCupFormat.RoundsPerGroup)
        {
            return [];
        }

        List<ChaosCupGroupStandingEntity> persisted = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.GroupNumber == groupNumber)
            .OrderBy(e => e.GroupRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (persisted.Count != ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException(
                $"Chaos Cup stage {stageIndex} group {groupNumber} is complete but misses standings.");
        }

        return persisted;
    }

    private static async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null)
            {
                throw new GetChaosCupGroupNotFoundException(
                    $"Chaos Cup group for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            return explicitSeason;
        }

        ChaosCupEditionEntity? latest = await context.ChaosCupEditions
            .AsNoTracking()
            .OrderByDescending(e => e.SourceSeasonNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new GetChaosCupGroupNotFoundException("Chaos Cup group has not been resolved yet.");
        }

        SeasonEntity? source = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latest.SourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
        return source ?? throw new InvalidOperationException("Chaos Cup edition references an unknown season.");
    }

    private static async Task<ChaosCupEditionEntity> LoadEditionAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupEditions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new GetChaosCupGroupNotFoundException(
                $"Chaos Cup group for Season {source.SeasonNumber} has not been resolved yet.");
    }

    private static async Task<Dictionary<int, string>> LoadNamesAsync(
        SaveDbContext context,
        List<ChaosCupDrawEntity> groupDraws,
        CancellationToken cancellationToken)
    {
        List<int> ids = groupDraws.Select(d => d.SaveAthleteId).ToList();
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        if (names.Count != ids.Count)
        {
            throw new InvalidOperationException("Chaos Cup group references unknown athletes.");
        }

        return names;
    }

    private static async Task<Dictionary<int, string?>> LoadImagesAsync(
        SaveDbContext context,
        ChaosCupRoundPayloadDocument payload,
        CancellationToken cancellationToken)
    {
        List<int> ids = payload.Placements.Select(p => p.AthleteId).ToList();
        return await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.ImageUrl, cancellationToken)
            .ConfigureAwait(false);
    }

    private static GetChaosCupGroupResponse MapGroupResponse(Guid saveId, LoadedGroup loaded)
    {
        bool isFinal = ChaosCupFormat.IsFinalStage(loaded.StageIndex);
        bool isComplete = loaded.Played.Count == ChaosCupFormat.RoundsPerGroup;
        List<GetChaosCupGroupResponse.Standing> standings = isComplete
            ? MapOfficialStandings(loaded)
            : DeriveProvisionalStandings(loaded);
        GetChaosCupGroupResponse.CutoffInfo? cutoff = isFinal ? null : BuildCutoff(standings, isComplete);
        return new GetChaosCupGroupResponse(
            saveId,
            loaded.Source.SeasonNumber,
            loaded.Source.Id,
            loaded.StageIndex,
            loaded.GroupNumber,
            isFinal,
            isComplete ? CupEditionState.Completed : CupEditionState.InProgress,
            loaded.Played.Count,
            ChaosCupFormat.RoundsPerGroup,
            isComplete,
            !isComplete,
            loaded.GroupDraws[0].DrawChecksum,
            loaded.GroupDraws.Select(d => MapEntrant(d, loaded)).ToList(),
            standings,
            cutoff,
            loaded.Played
                .OrderBy(p => p.RoundNumber)
                .Select(p => new GetChaosCupGroupResponse.RoundSummary(p.RoundNumber, p.Checksum))
                .ToList());
    }

    private static GetChaosCupGroupResponse.Entrant MapEntrant(ChaosCupDrawEntity draw, LoadedGroup loaded)
    {
        ChaosCupEntrantEntity entrant = loaded.EntrantsByAthlete[draw.SaveAthleteId];
        SportingColor color = (SportingColor)entrant.SportingColor;
        return new GetChaosCupGroupResponse.Entrant(
            draw.SaveAthleteId,
            loaded.NamesByAthlete[draw.SaveAthleteId],
            entrant.SportingColor,
            color.ToString(),
            entrant.SourceLeagueId,
            entrant.SourceLeagueKind,
            entrant.SourceFeederDivision,
            entrant.IsPoolInvite,
            SourceLabelFor(entrant),
            entrant.EffectiveBonusThousandths);
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

    private static List<GetChaosCupGroupResponse.Standing> MapOfficialStandings(LoadedGroup loaded)
    {
        HashSet<int> drawn = loaded.GroupDraws.Select(d => d.SaveAthleteId).ToHashSet();
        HashSet<int> ranks = new();
        bool isFinal = ChaosCupFormat.IsFinalStage(loaded.StageIndex);
        List<GetChaosCupGroupResponse.Standing> standings = new(loaded.PersistedStandings.Count);
        foreach (ChaosCupGroupStandingEntity row in loaded.PersistedStandings.OrderBy(s => s.GroupRank))
        {
            if (!drawn.Contains(row.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Chaos Cup stage {loaded.StageIndex} group {loaded.GroupNumber} ranks athlete {row.SaveAthleteId} outside its draw.");
            }

            if (!ranks.Add(row.GroupRank))
            {
                throw new InvalidOperationException(
                    $"Chaos Cup stage {loaded.StageIndex} group {loaded.GroupNumber} contains duplicate rank {row.GroupRank}.");
            }

            bool shouldAdvance = !isFinal && row.GroupRank <= ChaosCupFormat.AdvancingPerGroup;
            if (row.IsAdvanced != shouldAdvance)
            {
                throw new InvalidOperationException(
                    $"Chaos Cup stage {loaded.StageIndex} group {loaded.GroupNumber} rank {row.GroupRank} has corrupt advancement flag.");
            }

            standings.Add(new GetChaosCupGroupResponse.Standing(
                row.SaveAthleteId,
                loaded.NamesByAthlete[row.SaveAthleteId],
                row.GroupRank,
                row.GroupScoreThousandths,
                row.BaseScoreThousandths,
                row.RoundWins,
                row.IsAdvanced));
        }

        if (!ranks.SetEquals(Enumerable.Range(1, ChaosCupFormat.GroupSize)))
        {
            throw new InvalidOperationException(
                $"Chaos Cup stage {loaded.StageIndex} group {loaded.GroupNumber} must cover ranks 1..{ChaosCupFormat.GroupSize} exactly once.");
        }

        return standings;
    }

    private static List<GetChaosCupGroupResponse.Standing> DeriveProvisionalStandings(LoadedGroup loaded)
    {
        if (loaded.Played.Count == 0)
        {
            bool isFinal = ChaosCupFormat.IsFinalStage(loaded.StageIndex);
            return loaded.GroupDraws
                .OrderBy(d => loaded.NamesByAthlete[d.SaveAthleteId], StringComparer.Ordinal)
                .Select((d, index) => new GetChaosCupGroupResponse.Standing(
                    d.SaveAthleteId,
                    loaded.NamesByAthlete[d.SaveAthleteId],
                    index + 1,
                    0,
                    0,
                    0,
                    !isFinal && index + 1 <= ChaosCupFormat.AdvancingPerGroup))
                .ToList();
        }

        HashSet<int> drawn = loaded.GroupDraws.Select(d => d.SaveAthleteId).ToHashSet();
        Dictionary<int, int> scoreById = drawn.ToDictionary(id => id, _ => 0);
        Dictionary<int, int> baseById = drawn.ToDictionary(id => id, _ => 0);
        Dictionary<int, int[]> countsById = drawn.ToDictionary(id => id, _ => new int[ChaosCupFormat.GroupSize]);
        Dictionary<int, int> runningById = drawn.ToDictionary(id => id, _ => 0);
        foreach (ChaosCupRoundPayloadDocument payload in loaded.Played.OrderBy(p => p.RoundNumber))
        {
            foreach (var placement in payload.Placements)
            {
                if (runningById[placement.AthleteId] != placement.CumulativeBeforeThousandths)
                {
                    throw new InvalidOperationException("Chaos Cup group cumulative total breaks the immutable chain.");
                }

                checked
                {
                    scoreById[placement.AthleteId] += placement.FinalThousandths;
                    baseById[placement.AthleteId] += placement.BaseThousandths;
                    runningById[placement.AthleteId] = placement.CumulativeAfterThousandths;
                }

                countsById[placement.AthleteId][placement.Position - 1]++;
            }
        }

        List<int> ordered = drawn.OrderBy(id => id).ToList();
        ordered.Sort((left, right) => CompareDerivedAthletes(left, right, scoreById, baseById, countsById));
        bool final = ChaosCupFormat.IsFinalStage(loaded.StageIndex);
        List<GetChaosCupGroupResponse.Standing> standings = new(ordered.Count);
        for (int rank = 1; rank <= ordered.Count; rank++)
        {
            int athleteId = ordered[rank - 1];
            standings.Add(new GetChaosCupGroupResponse.Standing(
                athleteId,
                loaded.NamesByAthlete[athleteId],
                rank,
                scoreById[athleteId],
                baseById[athleteId],
                countsById[athleteId][0],
                !final && rank <= ChaosCupFormat.AdvancingPerGroup));
        }

        return standings;
    }

    private static int CompareDerivedAthletes(
        int left,
        int right,
        Dictionary<int, int> scoreById,
        Dictionary<int, int> baseById,
        Dictionary<int, int[]> countsById)
    {
        int score = scoreById[right].CompareTo(scoreById[left]);
        if (score != 0)
        {
            return score;
        }

        int[] leftCounts = countsById[left];
        int[] rightCounts = countsById[right];
        for (int i = 0; i < leftCounts.Length; i++)
        {
            int place = rightCounts[i].CompareTo(leftCounts[i]);
            if (place != 0)
            {
                return place;
            }
        }

        int baseScore = baseById[right].CompareTo(baseById[left]);
        if (baseScore != 0)
        {
            return baseScore;
        }

        return left.CompareTo(right);
    }

    private static GetChaosCupGroupResponse.CutoffInfo BuildCutoff(
        List<GetChaosCupGroupResponse.Standing> standings, bool isComplete)
    {
        if (standings.Count != ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException(
                $"Chaos Cup group must hold exactly {ChaosCupFormat.GroupSize} standings, was {standings.Count}.");
        }

        GetChaosCupGroupResponse.Standing sixteenth = standings[ChaosCupFormat.AdvancingPerGroup - 1];
        GetChaosCupGroupResponse.Standing seventeenth = standings[ChaosCupFormat.AdvancingPerGroup];
        return new GetChaosCupGroupResponse.CutoffInfo(
            new GetChaosCupGroupResponse.CutoffEntry(
                sixteenth.SaveAthleteId, sixteenth.Name, sixteenth.GroupRank, sixteenth.GroupScoreThousandths),
            new GetChaosCupGroupResponse.CutoffEntry(
                seventeenth.SaveAthleteId, seventeenth.Name, seventeenth.GroupRank, seventeenth.GroupScoreThousandths),
            checked(sixteenth.GroupScoreThousandths - seventeenth.GroupScoreThousandths),
            !isComplete,
            isComplete);
    }

    private static GetChaosCupRoundResponse MapRoundResponse(
        Guid saveId,
        LoadedGroup loaded,
        ChaosCupRoundPayloadDocument payload,
        Dictionary<int, string?> images)
    {
        return new GetChaosCupRoundResponse(
            saveId,
            loaded.Source.SeasonNumber,
            loaded.Source.Id,
            loaded.StageIndex,
            loaded.GroupNumber,
            payload.RoundNumber,
            payload.RulesVersion,
            payload.ChaosFormatVersion,
            payload.Checksum,
            payload.RngBeforeState,
            payload.RngBeforeStream,
            payload.RngAfterState,
            payload.RngAfterStream,
            payload.Placements
                .OrderBy(p => p.Position)
                .Select(p => new GetChaosCupRoundResponse.Placement(
                    p.AthleteId,
                    p.Name,
                    p.Position,
                    p.BaseThousandths,
                    p.ActiveBonusThousandths,
                    p.FinalThousandths,
                    p.CumulativeBeforeThousandths,
                    p.CumulativeAfterThousandths,
                    p.RankBefore,
                    p.RankAfter,
                    p.RankMovement,
                    images.GetValueOrDefault(p.AthleteId)))
                .ToList());
    }
}
