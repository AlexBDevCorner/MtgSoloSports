using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Cups.RunChaosCupGroup;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Simulates one 32-athlete Chaos Cup
/// group incrementally (play-next-round) or fully (finish-selected-group) with the
/// standard 77..1 base-point round, randomized positions, frozen edition-time
/// effective active bonus and deterministic group tie-breakers (MSS-101).
/// Each (stage, group) owns an isolated RNG derived only from persisted edition
/// plus stage-draw checksums, so group order and incremental-vs-complete calls
/// produce identical results and the global save RNG is never consumed.
/// Persists one immutable round row per game round plus final P1..P32 standings
/// on the 16th round; partial standings are derived from payloads and never
/// persisted. Retries resume from persisted rounds and never replay committed
/// rounds. Holds one per-save lock; reads never lock.
/// </summary>
public sealed class RunChaosCupGroupHandler
{
    private readonly SaveStore _store;
    private readonly MtgSoloSports.Features.Reports.GoogleDrivePublishing.DriveAutoPublishNotifier? _autoPublish;

    public RunChaosCupGroupHandler(SaveStore store, MtgSoloSports.Features.Reports.GoogleDrivePublishing.DriveAutoPublishNotifier? autoPublish = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _autoPublish = autoPublish;
    }

    public async Task<RunChaosCupGroupResponse> PlayNextAsync(
        Guid saveId,
        int stageIndex,
        int groupNumber,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(saveId, stageIndex, groupNumber, sourceSeasonNumber);
        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RunChaosCupGroupResponse response;
        try
        {
            response = await PlayNextUnderLockAsync(saveId, stageIndex, groupNumber, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // MSS-116: post-commit checkpoint note only (local file write, never
        // Google calls). Never throws, never fails the sporting command.
        if (_autoPublish is not null)
        {
            await _autoPublish.NotifyStageCheckpointAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        }

        return response;
    }

    public async Task<RunChaosCupGroupResponse> FinishGroupAsync(
        Guid saveId,
        int stageIndex,
        int groupNumber,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(saveId, stageIndex, groupNumber, sourceSeasonNumber);
        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        RunChaosCupGroupResponse response;
        try
        {
            response = await FinishUnderLockAsync(saveId, stageIndex, groupNumber, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // MSS-116: post-commit checkpoint note only (local file write, never
        // Google calls). Never throws, never fails the sporting command.
        if (_autoPublish is not null)
        {
            await _autoPublish.NotifyStageCheckpointAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        }

        return response;
    }

    public async Task<RunChaosCupGroupResponse> GetAsync(
        Guid saveId,
        int stageIndex,
        int groupNumber,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(saveId, stageIndex, groupNumber, sourceSeasonNumber);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        GroupState state = await LoadStateAsync(context, saveId, stageIndex, groupNumber, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        return MapResponse(saveId, state);
    }

    internal async Task<RunChaosCupGroupResponse> PlayNextUnderLockAsync(
        Guid saveId,
        int stageIndex,
        int groupNumber,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        GroupState state = await LoadStateAsync(context, saveId, stageIndex, groupNumber, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        if (state.Played.Count >= ChaosCupFormat.RoundsPerGroup)
        {
            throw new RunChaosCupGroupConflictException(
                $"Chaos Cup stage {stageIndex} group {groupNumber} is already complete with {ChaosCupFormat.RoundsPerGroup} rounds.");
        }

        PreservationSnapshot before = await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = state.Played.Count == 0
            ? state.GroupRngStart
            : new Pcg32State(state.Played[^1].RngAfterState, state.Played[^1].RngAfterStream);
        ChaosCupRoundPayloadDocument payload = PlayRound(state, state.Played, rngBefore);
        List<ChaosCupRoundPayloadDocument> all = [.. state.Played, payload];
        PersistRound(context, state, payload);
        bool complete = all.Count == ChaosCupFormat.RoundsPerGroup;
        if (complete)
        {
            PersistStandings(context, state, all);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await VerifyPreservationAsync(context, before, cancellationToken).ConfigureAwait(false);
        GroupState committed = await ReloadForResponseAsync(context, state, all, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MapResponse(saveId, committed);
    }

    internal async Task<RunChaosCupGroupResponse> FinishUnderLockAsync(
        Guid saveId,
        int stageIndex,
        int groupNumber,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        GroupState state = await LoadStateAsync(context, saveId, stageIndex, groupNumber, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        if (state.Played.Count == ChaosCupFormat.RoundsPerGroup)
        {
            bool hasStandings = await HasStandingsAsync(context, state, cancellationToken).ConfigureAwait(false);
            if (hasStandings)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return MapResponse(saveId, state);
            }
        }

        PreservationSnapshot before = await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);
        List<ChaosCupRoundPayloadDocument> all = new(state.Played);
        Pcg32State current = all.Count == 0
            ? state.GroupRngStart
            : new Pcg32State(all[^1].RngAfterState, all[^1].RngAfterStream);
        while (all.Count < ChaosCupFormat.RoundsPerGroup)
        {
            ChaosCupRoundPayloadDocument payload = PlayRound(state, all, current);
            all.Add(payload);
            PersistRound(context, state, payload);
            current = new Pcg32State(payload.RngAfterState, payload.RngAfterStream);
        }

        PersistStandings(context, state, all);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await VerifyPreservationAsync(context, before, cancellationToken).ConfigureAwait(false);
        GroupState committed = await ReloadForResponseAsync(context, state, all, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MapResponse(saveId, committed);
    }

    private static void ValidateRequest(Guid saveId, int stageIndex, int groupNumber, int? sourceSeasonNumber)
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
            throw new ArgumentException($"Chaos Cup stage {stageIndex} is out of range (0..{ChaosCupFormat.StageCount - 1}).", nameof(stageIndex));
        }

        int groupCount = ChaosCupFormat.GroupCountForStage(stageIndex);
        if (groupNumber < 1 || groupNumber > groupCount)
        {
            throw new ArgumentException($"Chaos Cup stage {stageIndex} group {groupNumber} is out of range (1..{groupCount}).", nameof(groupNumber));
        }
    }

    internal sealed record GroupState(
        RulesV1 Rules,
        SeasonEntity Source,
        ChaosCupEditionEntity Edition,
        int StageIndex,
        int GroupNumber,
        List<ChaosCupDrawEntity> StageDraws,
        List<ChaosCupDrawEntity> GroupDraws,
        List<AdvanceRoundHandler.MemberRow> Roster,
        Dictionary<int, Bonus> ActiveBonuses,
        Dictionary<int, string> NamesByAthlete,
        Dictionary<int, int> ColorByAthlete,
        Pcg32State GroupRngStart,
        List<ChaosCupRoundPayloadDocument> Played);

    private sealed record PreservationSnapshot(
        int StageStandings,
        int SeasonStandings,
        int LeagueRounds,
        long LifetimeBonus,
        long EffectiveBonus,
        long Championship,
        long RngState,
        long RngStream);

    internal static async Task<GroupState> LoadStateAsync(
        SaveDbContext context,
        Guid saveId,
        int stageIndex,
        int groupNumber,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        _ = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        ChaosCupEditionEntity edition = await LoadEditionAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<ChaosCupEntrantEntity> entrants = await LoadEntrantsAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<ChaosCupDrawEntity> stageDraws = await LoadStageDrawsAsync(context, source, stageIndex, cancellationToken).ConfigureAwait(false);
        if (stageDraws.Count == 0)
        {
            throw new RunChaosCupGroupConflictException(
                $"Chaos Cup stage {stageIndex} has no persisted draw yet for Season {source.SeasonNumber}.");
        }

        ChaosCupInvariants.ValidateEdition(edition, rules);
        ChaosCupInvariants.ValidateEntrants(source, edition, entrants);
        ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, stageDraws, stageIndex);
        ChaosCupFormat.ValidateFormatAgainstRules(rules);

        List<ChaosCupDrawEntity> groupDraws = stageDraws
            .Where(d => d.GroupNumber == groupNumber)
            .OrderBy(d => d.DrawIndex)
            .ToList();
        if (groupDraws.Count != ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {groupNumber} must draw exactly {ChaosCupFormat.GroupSize} athletes, was {groupDraws.Count}.");
        }

        Dictionary<int, string> names = await LoadNamesAsync(context, groupDraws, cancellationToken).ConfigureAwait(false);
        List<AdvanceRoundHandler.MemberRow> roster = BuildRoster(groupDraws, names);
        Dictionary<int, Bonus> bonuses = BuildActiveBonuses(entrants, groupDraws);
        Dictionary<int, int> colors = entrants.ToDictionary(e => e.SaveAthleteId, e => e.SportingColor);
        Pcg32State groupStart = ChaosCupGroupSimulation.DeriveGroupRng(edition, stageDraws, stageIndex, groupNumber);
        List<ChaosCupRoundEntity> rows = await LoadGroupRoundsAsync(context, source, stageIndex, groupNumber, cancellationToken).ConfigureAwait(false);
        List<ChaosCupRoundPayloadDocument> played = DecodeAndValidateRounds(rows, source, edition, rules, groupStart, stageIndex, groupNumber);
        await ValidateStandingsStateAsync(context, source, stageIndex, groupNumber, played, cancellationToken).ConfigureAwait(false);
        return new GroupState(rules, source, edition, stageIndex, groupNumber, stageDraws, groupDraws, roster, bonuses, names, colors, groupStart, played);
    }

    private static async Task<SeasonEntity> LoadSourceSeasonAsync(
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
                throw new RunChaosCupGroupConflictException($"Season {sourceSeasonNumber.Value} does not exist for Chaos Cup group simulation.");
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
            throw new RunChaosCupGroupConflictException("Chaos Cup field has not been selected yet for any season.");
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
        ChaosCupEditionEntity? edition = await context.ChaosCupEditions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (edition is null)
        {
            throw new RunChaosCupGroupConflictException(
                $"Chaos Cup field for Season {source.SeasonNumber} has not been selected yet.");
        }

        return edition;
    }

    private static async Task<List<ChaosCupEntrantEntity>> LoadEntrantsAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<List<ChaosCupDrawEntity>> LoadStageDrawsAsync(
        SaveDbContext context,
        SeasonEntity source,
        int stageIndex,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
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

        foreach ((int id, string name) in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"Save athlete {id} has an empty name.");
            }
        }

        return names;
    }

    internal static List<AdvanceRoundHandler.MemberRow> BuildRoster(
        List<ChaosCupDrawEntity> groupDraws,
        Dictionary<int, string> names)
    {
        List<AdvanceRoundHandler.MemberRow> roster = new(groupDraws.Count);
        foreach (ChaosCupDrawEntity draw in groupDraws)
        {
            roster.Add(new AdvanceRoundHandler.MemberRow(draw.SaveAthleteId, names[draw.SaveAthleteId], 0));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (AdvanceRoundHandler.MemberRow row in roster)
        {
            if (!seen.Add(row.Name))
            {
                throw new InvalidOperationException($"Chaos Cup group contains duplicate athlete '{row.Name}'.");
            }
        }

        roster.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return roster;
    }

    internal static Dictionary<int, Bonus> BuildActiveBonuses(
        List<ChaosCupEntrantEntity> entrants,
        List<ChaosCupDrawEntity> groupDraws)
    {
        Dictionary<int, int> frozen = entrants.ToDictionary(e => e.SaveAthleteId, e => e.EffectiveBonusThousandths);
        Dictionary<int, Bonus> bonuses = new(groupDraws.Count);
        foreach (ChaosCupDrawEntity draw in groupDraws)
        {
            if (!frozen.TryGetValue(draw.SaveAthleteId, out int thousandths))
            {
                throw new InvalidOperationException($"Chaos Cup group holds athlete {draw.SaveAthleteId} outside the frozen field.");
            }

            if (thousandths < 0)
            {
                throw new InvalidOperationException($"Chaos Cup entrant {draw.SaveAthleteId} has corrupt frozen bonus {thousandths}.");
            }

            bonuses[draw.SaveAthleteId] = Bonus.FromThousandths(thousandths);
        }

        return bonuses;
    }

    private static async Task<List<ChaosCupRoundEntity>> LoadGroupRoundsAsync(
        SaveDbContext context,
        SeasonEntity source,
        int stageIndex,
        int groupNumber,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.GroupNumber == groupNumber)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static List<ChaosCupRoundPayloadDocument> DecodeAndValidateRounds(
        List<ChaosCupRoundEntity> rows,
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        RulesV1 rules,
        Pcg32State groupStart,
        int stageIndex,
        int groupNumber)
    {
        ChaosCupInvariants.ValidateGroupRoundsContiguous(rows, stageIndex, groupNumber);
        List<ChaosCupRoundPayloadDocument> played = new(rows.Count);
        foreach (ChaosCupRoundEntity row in rows)
        {
            ChaosCupInvariants.ValidatePersistedRound(row, source, edition, rules);
            played.Add(ChaosCupRoundPayloadDocument.FromStored(row.PayloadJson));
        }

        if (played.Count > 0)
        {
            ChaosCupRoundPayloadDocument first = played[0];
            if (first.RngBeforeState != groupStart.State || first.RngBeforeStream != groupStart.Stream)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {groupNumber} first round does not start from its isolated group RNG.");
            }
        }

        return played;
    }

    private static async Task ValidateStandingsStateAsync(
        SaveDbContext context,
        SeasonEntity source,
        int stageIndex,
        int groupNumber,
        List<ChaosCupRoundPayloadDocument> played,
        CancellationToken cancellationToken)
    {
        List<ChaosCupGroupStandingEntity> existing = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.GroupNumber == groupNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (played.Count < ChaosCupFormat.RoundsPerGroup && existing.Count != 0)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {groupNumber} is partly played but already has standings.");
        }
    }

    private static async Task<bool> HasStandingsAsync(
        SaveDbContext context,
        GroupState state,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupGroupStandings.AnyAsync(
            e => e.SourceSeasonId == state.Source.Id && e.StageIndex == state.StageIndex && e.GroupNumber == state.GroupNumber,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Simulates exactly one group round from <paramref name="rngBefore"/>.</summary>
    internal static ChaosCupRoundPayloadDocument PlayRound(
        GroupState state,
        IReadOnlyList<ChaosCupRoundPayloadDocument> played,
        Pcg32State rngBefore)
    {
        Dictionary<int, Points> cumulative = new(state.Roster.Count);
        if (played.Count > 0)
        {
            foreach (RoundPayloadEntry entry in played[^1].Placements)
            {
                cumulative[entry.AthleteId] = Points.FromThousandths(entry.CumulativeAfterThousandths);
            }
        }

        int roundNumber = played.Count + 1;
        RoundSimulationResult simulation = AdvanceRoundHandler.SimulateRound(
            state.Roster, cumulative, state.ActiveBonuses, rngBefore, state.Rules);
        ChaosCupRoundPayloadDocument payload = new(
            ChaosCupRoundPayloadDocument.PayloadVersion,
            state.Rules.Version,
            ChaosCupFormat.ChaosFormatVersion,
            state.Source.SeasonNumber,
            state.StageIndex,
            state.GroupNumber,
            roundNumber,
            rngBefore.State,
            rngBefore.Stream,
            simulation.RngAfter.State,
            simulation.RngAfter.Stream,
            simulation.Checksum,
            ToPayloadEntries(simulation));
        ChaosCupInvariants.ValidateRoundPayload(payload, state.Rules, rngBefore.State, rngBefore.Stream);
        return payload;
    }

    internal static IReadOnlyList<RoundPayloadEntry> ToPayloadEntries(RoundSimulationResult simulation)
    {
        List<RoundPayloadEntry> entries = new(simulation.Placements.Count);
        foreach (RoundPlacement placement in simulation.Placements)
        {
            entries.Add(new RoundPayloadEntry(
                placement.AthleteId,
                placement.Name,
                placement.Position,
                placement.BasePoints.Thousandths,
                placement.ActiveBonus.Thousandths,
                placement.FinalPoints.Thousandths,
                placement.CumulativeBefore.Thousandths,
                placement.CumulativeAfter.Thousandths,
                placement.RankBefore,
                placement.RankAfter,
                placement.RankMovement));
        }

        return entries;
    }

    private static void PersistRound(SaveDbContext context, GroupState state, ChaosCupRoundPayloadDocument payload)
    {
        context.ChaosCupRounds.Add(new ChaosCupRoundEntity
        {
            SourceSeasonId = state.Source.Id,
            SourceSeasonNumber = state.Source.SeasonNumber,
            StageIndex = payload.StageIndex,
            GroupNumber = payload.GroupNumber,
            RoundNumber = payload.RoundNumber,
            RulesVersion = payload.RulesVersion,
            ChaosFormatVersion = payload.ChaosFormatVersion,
            RngBeforeState = unchecked((long)payload.RngBeforeState),
            RngBeforeStream = unchecked((long)payload.RngBeforeStream),
            RngAfterState = unchecked((long)payload.RngAfterState),
            RngAfterStream = unchecked((long)payload.RngAfterStream),
            PayloadJson = payload.ToStored(),
            PayloadChecksum = payload.Checksum,
        });
    }

    internal const int ChaosHonourLeagueId = -4;

    internal const int ChaosHonourLeagueKind = -5;

    internal const string ChaosHonourLeagueName = "Chaos Cup";

    private static void PersistStandings(
        SaveDbContext context,
        GroupState state,
        List<ChaosCupRoundPayloadDocument> all)
    {
        int stageIndex = all[0].StageIndex;
        int groupNumber = all[0].GroupNumber;
        bool existing = context.ChaosCupGroupStandings.Local.Any(
            e => e.SourceSeasonId == state.Source.Id && e.StageIndex == stageIndex && e.GroupNumber == groupNumber);
        if (existing)
        {
            return;
        }

        List<ChaosCupGroupStandingEntity> rows = BuildStandingRows(state, all);
        foreach (ChaosCupGroupStandingEntity row in rows)
        {
            context.ChaosCupGroupStandings.Add(row);
        }

        if (ChaosCupFormat.IsFinalStage(stageIndex))
        {
            PersistFinalHonours(context, state, rows);
        }
    }

    /// <summary>
    /// Records only the Final podium (P1..P3) as official honours. No group-win
    /// title, no bonus, no promotion. Runs in the caller's standings
    /// transaction so standings and honours commit atomically; retries reuse
    /// persisted standings and never duplicate honours.
    /// </summary>
    internal static void PersistFinalHonours(
        SaveDbContext context,
        GroupState state,
        List<ChaosCupGroupStandingEntity> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(rows);
        List<ChaosCupGroupStandingEntity> podium = rows
            .Where(r => r.GroupRank is >= 1 and <= 3)
            .OrderBy(r => r.GroupRank)
            .ToList();
        if (podium.Count != 3)
        {
            throw new InvalidOperationException($"Chaos Cup Final must hold exactly three podium finishers, was {podium.Count}.");
        }

        foreach (ChaosCupGroupStandingEntity standing in podium)
        {
            Features.Records.HonourKind kind =
                Features.Records.HonourKindMapper.FromChaosCupIndividualRank(standing.GroupRank);
            bool exists = context.Honours.Local.Any(e =>
                e.SeasonId == state.Source.Id
                && e.SeasonNumber == state.Source.SeasonNumber
                && e.LeagueId == ChaosHonourLeagueId
                && e.SaveAthleteId == standing.SaveAthleteId
                && e.Kind == (int)kind);
            if (exists)
            {
                continue;
            }

            context.Honours.Add(new HonourEntity
            {
                SeasonId = state.Source.Id,
                SeasonNumber = state.Source.SeasonNumber,
                LeagueId = ChaosHonourLeagueId,
                LeagueName = ChaosHonourLeagueName,
                LeagueKind = ChaosHonourLeagueKind,
                SaveAthleteId = standing.SaveAthleteId,
                Kind = (int)kind,
            });
        }
    }

    internal static List<ChaosCupGroupStandingEntity> BuildStandingRows(
        GroupState state,
        List<ChaosCupRoundPayloadDocument> all)
    {
        if (all.Count != ChaosCupFormat.RoundsPerGroup)
        {
            throw new InvalidOperationException($"Chaos Cup group needs exactly {ChaosCupFormat.RoundsPerGroup} rounds to rank standings, was {all.Count}.");
        }

        int stageIndex = all[0].StageIndex;
        int groupNumber = all[0].GroupNumber;
        GroupTotals totals = AccumulateGroupTotals(state, all, stageIndex, groupNumber);
        return RankGroupTotals(state, totals, stageIndex, groupNumber);
    }

    private sealed record GroupTotals(
        HashSet<int> Drawn,
        Dictionary<int, int> ScoreById,
        Dictionary<int, int> BaseById,
        Dictionary<int, int[]> CountsById);

    private static GroupTotals AccumulateGroupTotals(
        GroupState state,
        List<ChaosCupRoundPayloadDocument> all,
        int stageIndex,
        int groupNumber)
    {
        HashSet<int> drawn = state.GroupDraws.Select(d => d.SaveAthleteId).ToHashSet();
        Dictionary<int, int> scoreById = drawn.ToDictionary(id => id, _ => 0);
        Dictionary<int, int> baseById = drawn.ToDictionary(id => id, _ => 0);
        Dictionary<int, int[]> countsById = drawn.ToDictionary(id => id, _ => new int[ChaosCupFormat.GroupSize]);
        Dictionary<int, int> runningById = drawn.ToDictionary(id => id, _ => 0);
        foreach (ChaosCupRoundPayloadDocument payload in all.OrderBy(p => p.RoundNumber))
        {
            AccumulateSinglePayload(payload, drawn, scoreById, baseById, countsById, runningById, stageIndex, groupNumber);
        }

        return new GroupTotals(drawn, scoreById, baseById, countsById);
    }

    private static void AccumulateSinglePayload(
        ChaosCupRoundPayloadDocument payload,
        HashSet<int> drawn,
        Dictionary<int, int> scoreById,
        Dictionary<int, int> baseById,
        Dictionary<int, int[]> countsById,
        Dictionary<int, int> runningById,
        int stageIndex,
        int groupNumber)
    {
        if (payload.StageIndex != stageIndex || payload.GroupNumber != groupNumber)
        {
            throw new InvalidOperationException("Chaos Cup group payloads mix foreign stage/group rounds.");
        }

        HashSet<int> roundAthletes = payload.Placements.Select(p => p.AthleteId).ToHashSet();
        if (!roundAthletes.SetEquals(drawn))
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {groupNumber} round {payload.RoundNumber} must cover exactly its drawn athletes.");
        }

        foreach (RoundPayloadEntry placement in payload.Placements)
        {
            if (runningById[placement.AthleteId] != placement.CumulativeBeforeThousandths)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {groupNumber} cumulative total breaks the immutable chain.");
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

    private static List<ChaosCupGroupStandingEntity> RankGroupTotals(
        GroupState state,
        GroupTotals totals,
        int stageIndex,
        int groupNumber)
    {
        List<int> ordered = totals.Drawn.OrderBy(id => id).ToList();
        ordered.Sort((left, right) => CompareDerivedAthletes(left, right, totals.ScoreById, totals.BaseById, totals.CountsById));
        bool isFinal = ChaosCupFormat.IsFinalStage(stageIndex);
        List<ChaosCupGroupStandingEntity> rows = new(ordered.Count);
        for (int rank = 1; rank <= ordered.Count; rank++)
        {
            int athleteId = ordered[rank - 1];
            rows.Add(new ChaosCupGroupStandingEntity
            {
                SourceSeasonId = state.Source.Id,
                SourceSeasonNumber = state.Source.SeasonNumber,
                StageIndex = stageIndex,
                GroupNumber = groupNumber,
                SaveAthleteId = athleteId,
                GroupRank = rank,
                GroupScoreThousandths = totals.ScoreById[athleteId],
                BaseScoreThousandths = totals.BaseById[athleteId],
                RoundWins = totals.CountsById[athleteId][0],
                RoundPlaceCountsJson = JsonSerializer.Serialize(totals.CountsById[athleteId].ToList()),
                SportingColor = state.ColorByAthlete[athleteId],
                IsAdvanced = !isFinal && rank <= ChaosCupFormat.AdvancingPerGroup,
            });
        }

        return rows;
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

    private static async Task<PreservationSnapshot> CapturePreservationAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths, cancellationToken).ConfigureAwait(false);
        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths, cancellationToken).ConfigureAwait(false);
        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths, cancellationToken).ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        return new PreservationSnapshot(stages, seasons, rounds, lifetime, effective, championship, rng.State, rng.Stream);
    }

    private static async Task VerifyPreservationAsync(
        SaveDbContext context,
        PreservationSnapshot before,
        CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        if (stages != before.StageStandings)
        {
            throw new InvalidOperationException("Chaos Cup group must not create league stage standings; championship totals are preserved.");
        }

        if (seasons != before.SeasonStandings)
        {
            throw new InvalidOperationException("Chaos Cup group must not create league season standings; championship totals are preserved.");
        }

        if (rounds != before.LeagueRounds)
        {
            throw new InvalidOperationException("Chaos Cup group must not create league rounds; Cup history lives in Cup tables only.");
        }

        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths, cancellationToken).ConfigureAwait(false);
        if (lifetime != before.LifetimeBonus)
        {
            throw new InvalidOperationException("Chaos Cup group participation must not change career bonus.");
        }

        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths, cancellationToken).ConfigureAwait(false);
        if (effective != before.EffectiveBonus)
        {
            throw new InvalidOperationException("Chaos Cup group participation must not change effective bonus projections.");
        }

        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths, cancellationToken).ConfigureAwait(false);
        if (championship != before.Championship)
        {
            throw new InvalidOperationException("Chaos Cup group must not award league championship points.");
        }

        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        if (rng.State != before.RngState || rng.Stream != before.RngStream)
        {
            throw new InvalidOperationException("Chaos Cup group simulation must not advance the global save RNG; groups own isolated RNG.");
        }
    }

    private static async Task<GroupState> ReloadForResponseAsync(
        SaveDbContext context,
        GroupState state,
        List<ChaosCupRoundPayloadDocument> all,
        CancellationToken cancellationToken)
    {
        List<ChaosCupRoundEntity> rows = await LoadGroupRoundsAsync(context, state.Source, all[0].StageIndex, all[0].GroupNumber, cancellationToken).ConfigureAwait(false);
        List<ChaosCupRoundPayloadDocument> played = DecodeAndValidateRounds(rows, state.Source, state.Edition, state.Rules, state.GroupRngStart, all[0].StageIndex, all[0].GroupNumber);
        if (played.Count != all.Count)
        {
            throw new InvalidOperationException("Chaos Cup persisted result does not match the simulation.");
        }

        return state with { Played = played };
    }

    internal static RunChaosCupGroupResponse MapResponse(Guid saveId, GroupState state)
    {
        List<RunChaosCupGroupResponse.RoundView> rounds = state.Played
            .OrderBy(p => p.RoundNumber)
            .Select(p => new RunChaosCupGroupResponse.RoundView(
                p.RoundNumber, p.Checksum, p.RngBeforeState, p.RngBeforeStream, p.RngAfterState, p.RngAfterStream))
            .ToList();
        List<RunChaosCupGroupResponse.StandingView> standings = DeriveStandingViews(state);
        return new RunChaosCupGroupResponse(
            saveId,
            state.Source.SeasonNumber,
            state.Source.Id,
            state.StageIndex,
            state.GroupNumber,
            state.Played.Count,
            ChaosCupFormat.RoundsPerGroup,
            state.Played.Count == ChaosCupFormat.RoundsPerGroup,
            rounds,
            standings);
    }

    private static List<RunChaosCupGroupResponse.StandingView> DeriveStandingViews(GroupState state)
    {
        if (state.Played.Count == 0)
        {
            return state.GroupDraws
                .OrderBy(d => state.NamesByAthlete[d.SaveAthleteId], StringComparer.Ordinal)
                .Select((d, index) => new RunChaosCupGroupResponse.StandingView(
                    d.SaveAthleteId,
                    state.NamesByAthlete[d.SaveAthleteId],
                    index + 1,
                    0,
                    0,
                    0,
                    !ChaosCupFormat.IsFinalStage(state.StageIndex) && index + 1 <= ChaosCupFormat.AdvancingPerGroup))
                .ToList();
        }

        HashSet<int> drawn = state.GroupDraws.Select(d => d.SaveAthleteId).ToHashSet();
        Dictionary<int, int> scoreById = drawn.ToDictionary(id => id, _ => 0);
        Dictionary<int, int> baseById = drawn.ToDictionary(id => id, _ => 0);
        Dictionary<int, int[]> countsById = drawn.ToDictionary(id => id, _ => new int[ChaosCupFormat.GroupSize]);
        Dictionary<int, int> runningById = drawn.ToDictionary(id => id, _ => 0);
        foreach (ChaosCupRoundPayloadDocument payload in state.Played.OrderBy(p => p.RoundNumber))
        {
            foreach (RoundPayloadEntry placement in payload.Placements)
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
        bool isFinal = ChaosCupFormat.IsFinalStage(state.StageIndex);
        List<RunChaosCupGroupResponse.StandingView> views = new(ordered.Count);
        for (int rank = 1; rank <= ordered.Count; rank++)
        {
            int athleteId = ordered[rank - 1];
            views.Add(new RunChaosCupGroupResponse.StandingView(
                athleteId,
                state.NamesByAthlete[athleteId],
                rank,
                scoreById[athleteId],
                baseById[athleteId],
                countsById[athleteId][0],
                !isFinal && rank <= ChaosCupFormat.AdvancingPerGroup));
        }

        return views;
    }
}
