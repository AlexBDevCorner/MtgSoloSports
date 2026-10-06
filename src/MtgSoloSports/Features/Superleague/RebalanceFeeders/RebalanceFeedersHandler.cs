using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Returns every feeder league to
/// exactly 32 athletes after all promotion/relegation/qualifier outcomes are known.
/// For each sporting color, starts from the provisional next-season feeder roster
/// (previous feeder roster minus Superleague entrants plus returning athletes),
/// displaces the lowest-ranked retained athletes when over 32, or draws
/// equal-probability replacements from that color's common pool with the versioned
/// simulation RNG when under 32. Pool vacancies are never filled before movement
/// and qualifier outcomes resolve; former stars and pool athletes are eligible
/// immediately with no cooldown or weighting. Persists structured
/// <see cref="MovementKind.RebalanceDraw"/> and
/// <see cref="MovementKind.RebalanceDisplacement"/> rows and commits the RNG-after
/// state in the same transaction as memberships and refreshed projections.
/// <c>SaveMetadata.CurrentSeason</c> is left unchanged; a later slice advances once
/// the roster is valid. Holds one per-save lock; read-only queries never lock.
/// </summary>
public sealed class RebalanceFeedersHandler
{
    public const int PoolSentinelLeagueId = 0;

    private readonly SaveStore _store;

    public RebalanceFeedersHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<RebalanceFeedersResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RebalanceUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<RebalanceFeedersResponse> RebalanceUnderLockAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        var metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = rngRow.ToState();

        (SeasonEntity source, SeasonEntity next, bool isInaugural) =
            await LoadPendingTransitionAsync(context, cancellationToken).ConfigureAwait(false);
        await EnsureNotAlreadyRebalancedAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        await EnsurePrerequisitesResolvedAsync(context, source, next, isInaugural, rules, cancellationToken).ConfigureAwait(false);

        (int stagesBefore, int seasonsBefore, int roundsBefore, int qualifierRoundsBefore, int qualifierStandingsBefore) =
            await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);
        HashSet<int> superleagueBefore = await LoadSuperleagueAthletesAsync(context, next, rules, cancellationToken).ConfigureAwait(false);

        List<LeagueEntity> sourceFeeders = await LoadFeedersAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> nextFeeders = await LoadFeedersAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        LeagueEntity nextSuperleague = await LoadSuperleagueAsync(context, next, cancellationToken).ConfigureAwait(false);
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> sourceStandings =
            await LoadSourceStandingsAsync(context, source, sourceFeeders, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> sourceMemberships =
            await LoadMembershipsAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> nextMemberships =
            await LoadMembershipsAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> names = await LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);

        List<RebalanceFeedersSelection.ColorInput> inputs = BuildColorInputs(
            sourceFeeders, nextFeeders, sourceStandings, sourceMemberships, nextMemberships, names, rules);

        Pcg32V1 rng = Pcg32V1.Restore(rngBefore);
        RebalanceFeedersSelection.RebalancePlan plan = RebalanceFeedersSelection.Select(inputs, rng, rules);
        RebalanceFeedersInvariants.ValidatePlan(plan, inputs, rules);
        Pcg32State rngAfter = rng.Snapshot();

        ApplyPlan(context, next, nextFeeders, nextMemberships, plan, sourceStandings);
        await PersistRebalanceMovementsAsync(context, source, next, nextFeeders, nextMemberships, plan, rngAfter, metadata, cancellationToken).ConfigureAwait(false);

        await Features.Athletes.Projections.AthleteProjectionUpdater.RebuildAllAsync(context, rules, cancellationToken).ConfigureAwait(false);

        await EmitReturnFromPoolStoriesAsync(context, source, next, nextFeeders, plan, cancellationToken).ConfigureAwait(false);

        await ValidatePersistedAsync(
            context, source, next, nextSuperleague, nextFeeders, plan,
            rules, rngBefore, rngAfter,
            stagesBefore, seasonsBefore, roundsBefore, qualifierRoundsBefore, qualifierStandingsBefore,
            superleagueBefore, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, source, next, plan, rngBefore, rngAfter, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<List<MovementEntity>> PersistRebalanceMovementsAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        List<SeasonMembershipEntity> nextMemberships,
        RebalanceFeedersSelection.RebalancePlan plan,
        Pcg32State rngAfter,
        SaveMetadataEntity metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(nextFeeders);
        ArgumentNullException.ThrowIfNull(nextMemberships);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(metadata);
        List<MovementEntity> movements = BuildMovements(source, next, nextFeeders, plan, nextMemberships);
        foreach (MovementEntity movement in movements)
        {
            context.Movements.Add(movement);
        }

        context.ApplyRngState(rngAfter);
        metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.Rebalanced);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return movements;
    }

    /// <summary>
    /// Emits return-from-pool story events transactionally with feeder
    /// rebalancing. One event per pool draw (pool to feeder); displacements
    /// (feeder to pool) emit nothing at this stage. Idempotent via dedup keys.
    /// </summary>
    internal static async Task EmitReturnFromPoolStoriesAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        RebalanceFeedersSelection.RebalancePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(nextFeeders);
        ArgumentNullException.ThrowIfNull(plan);
        Dictionary<string, string> feederNames = nextFeeders.ToDictionary(
            l => ((SportingColor)l.SportingColor).ToString(), l => l.Name, StringComparer.Ordinal);
        string movementDedup = Features.Stories.StoryEventEmitter.MovementDedup(
            source.SeasonNumber, next.SeasonNumber);
        bool emitted = false;
        foreach (RebalanceFeedersSelection.ColorPlan colorPlan in plan.PerColor.OrderBy(p => p.Color))
        {
            feederNames.TryGetValue(colorPlan.Color.ToString(), out string? feederName);
            foreach (RebalanceFeedersSelection.PoolCandidate draw in colorPlan.Draws.OrderBy(d => d.SaveAthleteId))
            {
                emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
                    context,
                    draw.SaveAthleteId,
                    Features.Stories.StoryEventType.ReturnFromPool,
                    movementDedup,
                    next.SeasonNumber,
                    null,
                    new Features.Stories.StoryEventPayload(
                        draw.Name,
                        next.SeasonNumber,
                        ToLeagueName: feederName ?? $"{colorPlan.Color} League",
                        FromSeasonNumber: source.SeasonNumber,
                        ToSeasonNumber: next.SeasonNumber,
                        SportingColor: colorPlan.Color.ToString()),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (emitted)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<(SeasonEntity Source, SeasonEntity Next, bool IsInaugural)> LoadPendingTransitionAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        List<SeasonEntity> completed = await context.Seasons
            .Where(e => e.IsComplete)
            .OrderByDescending(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (SeasonEntity candidate in completed)
        {
            SeasonEntity? successor = await context.Seasons.SingleOrDefaultAsync(
                e => e.SeasonNumber == candidate.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
            if (successor is null || successor.IsComplete || !successor.HasSuperleague)
            {
                continue;
            }

            if (candidate.SeasonNumber == 1 && !candidate.HasSuperleague)
            {
                return (candidate, successor, true);
            }

            if (candidate.HasSuperleague)
            {
                return (candidate, successor, false);
            }
        }

        bool anyRebalanced = await context.Movements.AnyAsync(
            e => e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement,
            cancellationToken).ConfigureAwait(false);
        if (anyRebalanced)
        {
            throw new RebalanceFeedersConflictException(
                "Feeder leagues have already been rebalanced for the pending transition.");
        }

        throw new RebalanceFeedersConflictException(
            "No pending postseason transition is ready for feeder rebalancing.");
    }

    internal static async Task EnsureNotAlreadyRebalancedAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        bool exists = await context.Movements.AnyAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement),
            cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            throw new RebalanceFeedersConflictException(
                $"Feeder leagues for Season {source.SeasonNumber} have already been rebalanced.");
        }
    }

    internal static async Task EnsurePrerequisitesResolvedAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        bool isInaugural,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        if (isInaugural)
        {
            int inaugural = await context.Movements.CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.InauguralPromotion,
                cancellationToken).ConfigureAwait(false);
            if (inaugural != rules.SuperleagueSize)
            {
                throw new RebalanceFeedersConflictException(
                    "The inaugural Superleague must be resolved before feeder rebalancing can run.");
            }

            return;
        }

        int promotions = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.AutomaticPromotion,
            cancellationToken).ConfigureAwait(false);
        int relegations = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.AutomaticRelegation,
            cancellationToken).ConfigureAwait(false);
        int incumbents = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.QualifierIncumbent,
            cancellationToken).ConfigureAwait(false);
        int challengers = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.QualifierChallenger,
            cancellationToken).ConfigureAwait(false);
        if (promotions != rules.FeederAutoPromotedCount
            || relegations != rules.SuperleagueRelegatedCount
            || incumbents != rules.SuperleagueQualifierIncumbentCount
            || challengers != rules.FeederQualifierCount)
        {
            throw new RebalanceFeedersConflictException(
                "Automatic movement must be resolved before feeder rebalancing can run.");
        }

        int qualifierStandings = await context.QualifierStandings.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        if (qualifierStandings != rules.QualifierSize || qualifierRounds != rules.QualifierRounds)
        {
            throw new RebalanceFeedersConflictException(
                "The Superleague qualifier must be resolved before feeder rebalancing can run.");
        }
    }

    internal static async Task<(int Stages, int Seasons, int Rounds, int QualifierRounds, int QualifierStandings)> CapturePreservationAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierStandings = await context.QualifierStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        return (stages, seasons, rounds, qualifierRounds, qualifierStandings);
    }

    internal static async Task<HashSet<int>> LoadSuperleagueAthletesAsync(
        SaveDbContext context, SeasonEntity next, RulesV1 rules, CancellationToken cancellationToken)
    {
        LeagueEntity superleague = await LoadSuperleagueAsync(context, next, cancellationToken).ConfigureAwait(false);
        List<int> ids = await context.SeasonMemberships
            .Where(e => e.SeasonId == next.Id && e.LeagueId == superleague.Id)
            .Select(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (ids.Count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Next Superleague must contain exactly {rules.SuperleagueSize} athletes before rebalancing, was {ids.Count}.");
        }

        return ids.ToHashSet();
    }

    internal static async Task<List<LeagueEntity>> LoadFeedersAsync(
        SaveDbContext context, SeasonEntity season, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == season.Id && e.Kind == (int)LeagueKind.Feeder)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count != rules.TieredFeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season {season.SeasonNumber} must have exactly {rules.TieredFeederLeagueCount} feeder leagues, was {leagues.Count}.");
        }

        return leagues;
    }

    internal static async Task<LeagueEntity> LoadSuperleagueAsync(
        SaveDbContext context, SeasonEntity season, CancellationToken cancellationToken)
    {
        LeagueEntity? league = await context.Leagues.SingleOrDefaultAsync(
            e => e.SeasonId == season.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken).ConfigureAwait(false);
        if (league is null)
        {
            throw new InvalidOperationException($"Season {season.SeasonNumber} has no Superleague.");
        }

        return league;
    }

    internal static async Task<Dictionary<int, IReadOnlyList<SeasonStandingEntity>>> LoadSourceStandingsAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<LeagueEntity> sourceFeeders,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> map = new(sourceFeeders.Count);
        foreach (LeagueEntity feeder in sourceFeeders)
        {
            List<SeasonStandingEntity> rows = await context.SeasonStandings
                .AsNoTracking()
                .Where(e => e.SeasonId == source.Id && e.LeagueId == feeder.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (rows.Count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{feeder.Name}' must have exactly {rules.LeagueSize} final standings, was {rows.Count}.");
            }

            HashSet<int> ranks = rows.Select(r => r.SeasonRank).ToHashSet();
            if (!ranks.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
            {
                throw new InvalidOperationException($"League '{feeder.Name}' must cover ranks 1..{rules.LeagueSize} exactly once.");
            }

            map[feeder.Id] = rows;
        }

        return map;
    }

    internal static async Task<List<SeasonMembershipEntity>> LoadMembershipsAsync(
        SaveDbContext context, SeasonEntity season, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == season.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Season {season.SeasonNumber} must have exactly {rules.TotalAthletesInSave} memberships, was {memberships.Count}.");
        }

        return memberships;
    }

    internal static async Task<Dictionary<int, string>> LoadAthleteNamesAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static List<RebalanceFeedersSelection.ColorInput> BuildColorInputs(
        List<LeagueEntity> sourceFeeders,
        List<LeagueEntity> nextFeeders,
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> sourceStandings,
        List<SeasonMembershipEntity> sourceMemberships,
        List<SeasonMembershipEntity> nextMemberships,
        Dictionary<int, string> names,
        RulesV1 rules)
    {
        bool tiered = rules.FeederDivisionsPerColor == 3;
        List<LeagueEntity> sourceF1 = tiered
            ? sourceFeeders.Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First).ToList()
            : sourceFeeders;
        List<LeagueEntity> nextF1 = tiered
            ? nextFeeders.Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First).ToList()
            : nextFeeders;
        if (tiered)
        {
            ValidateTieredLowerDivisions(sourceFeeders, nextFeeders, nextMemberships, rules);
        }

        Dictionary<int, LeagueEntity> sourceByColor = sourceF1.ToDictionary(l => l.SportingColor);
        Dictionary<int, LeagueEntity> nextByColor = nextF1.ToDictionary(l => l.SportingColor);
        Dictionary<int, SeasonMembershipEntity> sourceByAthlete = sourceMemberships.ToDictionary(m => m.SaveAthleteId);
        Dictionary<int, List<SeasonMembershipEntity>> nextByLeague = nextMemberships
            .Where(m => m.LeagueId.HasValue)
            .GroupBy(m => m.LeagueId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        Dictionary<int, List<SeasonMembershipEntity>> nextPoolByColor = nextMemberships
            .Where(m => m.LeagueId is null)
            .GroupBy(m => m.SportingColor)
            .ToDictionary(g => g.Key, g => g.ToList());

        List<RebalanceFeedersSelection.ColorInput> inputs = new(rules.RegularLeagueCount);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            inputs.Add(BuildSingleColorInput(
                color, sourceByColor, nextByColor, sourceStandings,
                sourceByAthlete, nextByLeague, nextPoolByColor, names));
        }

        return inputs;
    }

    internal static void ValidateTieredLowerDivisions(
        List<LeagueEntity> sourceFeeders,
        List<LeagueEntity> nextFeeders,
        List<SeasonMembershipEntity> nextMemberships,
        RulesV1 rules)
    {
        // MSS-057 scope: normal F1↔F2↔F3 movement is out of scope, so F2/F3
        // carry over unchanged at 32 each. Rebalancing only fills F1 vacancies
        // from the pool; F2/F3 must already be at 32 or the save is corrupt.
        Dictionary<int, int> counts = nextMemberships
            .Where(m => m.LeagueId.HasValue)
            .GroupBy(m => m.LeagueId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (LeagueEntity league in nextFeeders)
        {
            if (league.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First)
            {
                continue;
            }

            counts.TryGetValue(league.Id, out int count);
            if (count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"Tiered league '{league.Name}' must hold exactly {rules.LeagueSize} athletes before F1 rebalancing, was {count}.");
            }
        }

        int f1Source = sourceFeeders.Count(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First);
        int f1Next = nextFeeders.Count(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First);
        if (f1Source != rules.RegularLeagueCount || f1Next != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Tiered transition must have exactly {rules.RegularLeagueCount} F1 leagues per season, was {f1Source}/{f1Next}.");
        }
    }

    internal static RebalanceFeedersSelection.ColorInput BuildSingleColorInput(
        SportingColor color,
        Dictionary<int, LeagueEntity> sourceByColor,
        Dictionary<int, LeagueEntity> nextByColor,
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> sourceStandings,
        Dictionary<int, SeasonMembershipEntity> sourceByAthlete,
        Dictionary<int, List<SeasonMembershipEntity>> nextByLeague,
        Dictionary<int, List<SeasonMembershipEntity>> nextPoolByColor,
        Dictionary<int, string> names)
    {
        (LeagueEntity sourceFeeder, LeagueEntity nextFeeder, Dictionary<int, int> rankByAthlete) =
            ResolveColorLeagues(color, sourceByColor, nextByColor, sourceStandings);
        List<RebalanceFeedersSelection.ProvisionalMember> provisionalMembers = BuildProvisionalMembers(
            color, sourceFeeder, nextFeeder, rankByAthlete, sourceByAthlete, nextByLeague, names);
        List<RebalanceFeedersSelection.PoolCandidate> candidates = BuildPoolCandidates(
            color, nextPoolByColor, names);
        return new RebalanceFeedersSelection.ColorInput(color, provisionalMembers, candidates);
    }

    internal static (LeagueEntity SourceFeeder, LeagueEntity NextFeeder, Dictionary<int, int> RankByAthlete) ResolveColorLeagues(
        SportingColor color,
        Dictionary<int, LeagueEntity> sourceByColor,
        Dictionary<int, LeagueEntity> nextByColor,
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> sourceStandings)
    {
        if (!sourceByColor.TryGetValue((int)color, out LeagueEntity? sourceFeeder))
        {
            throw new InvalidOperationException($"Source season is missing the {color} feeder.");
        }

        if (!nextByColor.TryGetValue((int)color, out LeagueEntity? nextFeeder))
        {
            throw new InvalidOperationException($"Next season is missing the {color} feeder.");
        }

        if (!sourceStandings.TryGetValue(sourceFeeder.Id, out IReadOnlyList<SeasonStandingEntity>? standings))
        {
            throw new InvalidOperationException($"League '{sourceFeeder.Name}' has no final standings.");
        }

        return (sourceFeeder, nextFeeder, standings.ToDictionary(r => r.SaveAthleteId, r => r.SeasonRank));
    }

    internal static List<RebalanceFeedersSelection.ProvisionalMember> BuildProvisionalMembers(
        SportingColor color,
        LeagueEntity sourceFeeder,
        LeagueEntity nextFeeder,
        Dictionary<int, int> rankByAthlete,
        Dictionary<int, SeasonMembershipEntity> sourceByAthlete,
        Dictionary<int, List<SeasonMembershipEntity>> nextByLeague,
        Dictionary<int, string> names)
    {
        List<SeasonMembershipEntity> provisional = nextByLeague.TryGetValue(nextFeeder.Id, out List<SeasonMembershipEntity>? members)
            ? members
            : [];
        List<RebalanceFeedersSelection.ProvisionalMember> provisionalMembers = new(provisional.Count);
        foreach (SeasonMembershipEntity membership in provisional.OrderBy(m => m.SaveAthleteId))
        {
            provisionalMembers.Add(MapProvisionalMember(color, sourceFeeder, rankByAthlete, sourceByAthlete, names, membership));
        }

        return provisionalMembers;
    }

    internal static RebalanceFeedersSelection.ProvisionalMember MapProvisionalMember(
        SportingColor color,
        LeagueEntity sourceFeeder,
        Dictionary<int, int> rankByAthlete,
        Dictionary<int, SeasonMembershipEntity> sourceByAthlete,
        Dictionary<int, string> names,
        SeasonMembershipEntity membership)
    {
        if (membership.SportingColor != (int)color)
        {
            throw new InvalidOperationException(
                $"Athlete {membership.SaveAthleteId} color {membership.SportingColor} does not match feeder {color}.");
        }

        if (!sourceByAthlete.TryGetValue(membership.SaveAthleteId, out SeasonMembershipEntity? source))
        {
            throw new InvalidOperationException($"Athlete {membership.SaveAthleteId} has no source membership.");
        }

        if (source.SportingColor != membership.SportingColor)
        {
            throw new InvalidOperationException(
                $"Athlete {membership.SaveAthleteId} changed sporting color; returning athletes keep their color.");
        }

        bool isRetained = source.LeagueId == sourceFeeder.Id;
        if (!isRetained && source.LeagueId is null)
        {
            throw new InvalidOperationException(
                $"Athlete {membership.SaveAthleteId} entered the {color} feeder from the common pool before rebalancing; pool vacancies fill only here.");
        }

        int sourceRank = ResolveRetainedRank(membership, isRetained, rankByAthlete);
        names.TryGetValue(membership.SaveAthleteId, out string? name);
        return new RebalanceFeedersSelection.ProvisionalMember(
            membership.SaveAthleteId, name ?? $"Athlete {membership.SaveAthleteId}", isRetained, sourceRank);
    }

    internal static int ResolveRetainedRank(
        SeasonMembershipEntity membership,
        bool isRetained,
        Dictionary<int, int> rankByAthlete)
    {
        if (!isRetained)
        {
            return 0;
        }

        if (!rankByAthlete.TryGetValue(membership.SaveAthleteId, out int sourceRank))
        {
            throw new InvalidOperationException(
                $"Retained athlete {membership.SaveAthleteId} has no source feeder rank.");
        }

        return sourceRank;
    }

    internal static List<RebalanceFeedersSelection.PoolCandidate> BuildPoolCandidates(
        SportingColor color,
        Dictionary<int, List<SeasonMembershipEntity>> nextPoolByColor,
        Dictionary<int, string> names)
    {
        List<SeasonMembershipEntity> pool = nextPoolByColor.TryGetValue((int)color, out List<SeasonMembershipEntity>? poolMembers)
            ? poolMembers
            : [];
        List<RebalanceFeedersSelection.PoolCandidate> candidates = new(pool.Count);
        foreach (SeasonMembershipEntity membership in pool)
        {
            if (membership.SportingColor != (int)color)
            {
                throw new InvalidOperationException(
                    $"Pool athlete {membership.SaveAthleteId} color {membership.SportingColor} does not match pool {color}.");
            }

            names.TryGetValue(membership.SaveAthleteId, out string? name);
            candidates.Add(new RebalanceFeedersSelection.PoolCandidate(
                membership.SaveAthleteId, name ?? $"Athlete {membership.SaveAthleteId}", color));
        }

        return candidates;
    }

    internal static void ApplyPlan(
        SaveDbContext context,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        List<SeasonMembershipEntity> nextMemberships,
        RebalanceFeedersSelection.RebalancePlan plan,
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> sourceStandings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(nextFeeders);
        ArgumentNullException.ThrowIfNull(nextMemberships);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sourceStandings);

        Dictionary<int, SeasonMembershipEntity> byAthlete = nextMemberships.ToDictionary(m => m.SaveAthleteId);
        Dictionary<string, int> feederByColor = nextFeeders.ToDictionary(
            l => ((SportingColor)l.SportingColor).ToString(), l => l.Id, StringComparer.Ordinal);

        foreach (RebalanceFeedersSelection.ColorPlan colorPlan in plan.PerColor)
        {
            ApplySingleColor(byAthlete, feederByColor, colorPlan);
        }
    }

    internal static void ApplySingleColor(
        Dictionary<int, SeasonMembershipEntity> byAthlete,
        Dictionary<string, int> feederByColor,
        RebalanceFeedersSelection.ColorPlan colorPlan)
    {
        if (!feederByColor.TryGetValue(colorPlan.Color.ToString(), out int feederId))
        {
            throw new InvalidOperationException($"Next season is missing the {colorPlan.Color} feeder.");
        }

        foreach (RebalanceFeedersSelection.RetainedCandidate displaced in colorPlan.Displaced)
        {
            if (!byAthlete.TryGetValue(displaced.SaveAthleteId, out SeasonMembershipEntity? membership))
            {
                throw new InvalidOperationException($"Displaced athlete {displaced.SaveAthleteId} has no next-season membership.");
            }

            if (membership.LeagueId != feederId)
            {
                throw new InvalidOperationException($"Displaced athlete {displaced.SaveAthleteId} is not in the {colorPlan.Color} feeder.");
            }

            membership.LeagueId = null;
        }

        foreach (RebalanceFeedersSelection.PoolCandidate draw in colorPlan.Draws)
        {
            if (!byAthlete.TryGetValue(draw.SaveAthleteId, out SeasonMembershipEntity? membership))
            {
                throw new InvalidOperationException($"Drawn athlete {draw.SaveAthleteId} has no next-season membership.");
            }

            if (membership.LeagueId is not null)
            {
                throw new InvalidOperationException($"Drawn athlete {draw.SaveAthleteId} is not in the common pool.");
            }

            if (membership.SportingColor != (int)colorPlan.Color)
            {
                throw new InvalidOperationException($"Drawn athlete {draw.SaveAthleteId} has the wrong sporting color.");
            }

            membership.LeagueId = feederId;
        }
    }

    internal static List<MovementEntity> BuildMovements(
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        RebalanceFeedersSelection.RebalancePlan plan,
        List<SeasonMembershipEntity> nextMemberships)
    {
        Dictionary<string, int> feederByColor = nextFeeders.ToDictionary(
            l => ((SportingColor)l.SportingColor).ToString(), l => l.Id, StringComparer.Ordinal);
        Dictionary<int, int> colorByAthlete = nextMemberships.ToDictionary(m => m.SaveAthleteId, m => m.SportingColor);
        List<MovementEntity> movements = new(plan.AllDraws.Count + plan.AllDisplaced.Count);

        foreach (RebalanceFeedersSelection.ColorPlan colorPlan in plan.PerColor.OrderBy(p => p.Color))
        {
            if (!feederByColor.TryGetValue(colorPlan.Color.ToString(), out int feederId))
            {
                throw new InvalidOperationException($"Next season is missing the {colorPlan.Color} feeder.");
            }

            foreach (RebalanceFeedersSelection.PoolCandidate draw in colorPlan.Draws.OrderBy(d => d.SaveAthleteId))
            {
                movements.Add(new MovementEntity
                {
                    SaveAthleteId = draw.SaveAthleteId,
                    FromSeasonId = source.Id,
                    ToSeasonId = next.Id,
                    FromLeagueId = PoolSentinelLeagueId,
                    ToLeagueId = feederId,
                    Kind = (int)MovementKind.RebalanceDraw,
                    FromSeasonRank = 0,
                    SportingColor = colorByAthlete.TryGetValue(draw.SaveAthleteId, out int color) ? color : (int)colorPlan.Color,
                });
            }

            foreach (RebalanceFeedersSelection.RetainedCandidate displaced in colorPlan.Displaced.OrderBy(d => d.SaveAthleteId))
            {
                movements.Add(new MovementEntity
                {
                    SaveAthleteId = displaced.SaveAthleteId,
                    FromSeasonId = source.Id,
                    ToSeasonId = next.Id,
                    FromLeagueId = feederId,
                    ToLeagueId = PoolSentinelLeagueId,
                    Kind = (int)MovementKind.RebalanceDisplacement,
                    FromSeasonRank = displaced.SourceRank,
                    SportingColor = colorByAthlete.TryGetValue(displaced.SaveAthleteId, out int color) ? color : (int)colorPlan.Color,
                });
            }
        }

        return movements
            .OrderBy(m => m.Kind)
            .ThenBy(m => m.FromLeagueId)
            .ThenBy(m => m.SaveAthleteId)
            .ToList();
    }

    internal static async Task ValidatePersistedAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        LeagueEntity nextSuperleague,
        List<LeagueEntity> nextFeeders,
        RebalanceFeedersSelection.RebalancePlan plan,
        RulesV1 rules,
        Pcg32State rngBefore,
        Pcg32State rngAfter,
        int stagesBefore,
        int seasonsBefore,
        int roundsBefore,
        int qualifierRoundsBefore,
        int qualifierStandingsBefore,
        HashSet<int> superleagueBefore,
        CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> persistedNext = await context.SeasonMemberships
            .Where(e => e.SeasonId == next.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<MovementEntity> persistedMovements = await context.Movements
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        RebalanceFeedersInvariants.ValidateCreated(
            source, next, nextSuperleague, nextFeeders, persistedNext, persistedMovements, plan, rules);

        HashSet<int> superleagueAfter = persistedNext
            .Where(m => m.LeagueId == nextSuperleague.Id)
            .Select(m => m.SaveAthleteId)
            .ToHashSet();
        if (!superleagueBefore.SetEquals(superleagueAfter))
        {
            throw new InvalidOperationException("Rebalancing must not alter the next Superleague roster.");
        }

        bool needsRng = plan.AllDraws.Count > 0;
        bool rngAdvanced = rngAfter.State != rngBefore.State || rngAfter.Stream != rngBefore.Stream;
        if (needsRng && !rngAdvanced)
        {
            throw new InvalidOperationException("Rebalancing pool draws must advance the save RNG.");
        }

        if (!needsRng && rngAdvanced)
        {
            throw new InvalidOperationException("Rebalancing without pool draws must not consume sporting RNG.");
        }

        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        Pcg32State persisted = rng.ToState();
        if (persisted.State != rngAfter.State || persisted.Stream != rngAfter.Stream)
        {
            throw new InvalidOperationException("Persisted RNG state does not match the rebalancing draw chain.");
        }

        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierStandings = await context.QualifierStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        if (stages != stagesBefore || seasons != seasonsBefore || rounds != roundsBefore)
        {
            throw new InvalidOperationException("Rebalancing must preserve league stage/season/round history.");
        }

        if (qualifierRounds != qualifierRoundsBefore || qualifierStandings != qualifierStandingsBefore)
        {
            throw new InvalidOperationException("Rebalancing must preserve qualifier history.");
        }
    }

    internal static async Task<RebalanceFeedersResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RebalanceFeedersSelection.RebalancePlan plan,
        Pcg32State rngBefore,
        Pcg32State rngAfter,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        Dictionary<int, string> names = await LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string?> images = await LoadAthleteImagesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        List<MovementEntity> movements = await LoadRebalanceMovementsAsync(context, source, next, cancellationToken).ConfigureAwait(false);

        List<RebalanceMovementMember> draws = MapMovements(movements, MovementKind.RebalanceDraw, names, leaguesById, images);
        List<RebalanceMovementMember> displaced = MapMovements(movements, MovementKind.RebalanceDisplacement, names, leaguesById, images);
        (IReadOnlyList<RebalanceMovementMember> departed, IReadOnlyList<RebalanceMovementMember> returned) =
            await LoadSuperleagueTransfersAsync(context, source, next, names, images, leaguesById, cancellationToken).ConfigureAwait(false);
        List<RebalanceColorResult> colors = await MapColorResultsAsync(context, next, plan, departed, returned, cancellationToken).ConfigureAwait(false);

        int pool = await context.SeasonMemberships
            .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == null, cancellationToken)
            .ConfigureAwait(false);

        return new RebalanceFeedersResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            colors,
            draws,
            displaced,
            departed,
            returned,
            draws.Count,
            displaced.Count,
            departed.Count,
            returned.Count,
            pool,
            movements.Count,
            rngBefore.State,
            rngBefore.Stream,
            rngAfter.State,
            rngAfter.Stream);
    }

    internal static async Task<Dictionary<int, string?>> LoadAthleteImagesAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.ImageUrl, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<(IReadOnlyList<RebalanceMovementMember> Departed, IReadOnlyList<RebalanceMovementMember> Returned)> LoadSuperleagueTransfersAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        Dictionary<int, LeagueEntity> leaguesById,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> sourceFeeders = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        LeagueEntity? sourceSuperleague = await context.Leagues
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken)
            .ConfigureAwait(false);
        List<LeagueEntity> nextFeeders = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        LeagueEntity nextSuperleague = await LoadSuperleagueAsync(context, next, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> sourceMemberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<SeasonMembershipEntity> nextMemberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> rankByAthlete = await LoadSourceRanksAsync(context, source, sourceFeeders, sourceSuperleague, cancellationToken).ConfigureAwait(false);
        return RebalanceSuperleagueTransfers.Map(
            sourceFeeders, sourceSuperleague, nextFeeders, nextSuperleague,
            sourceMemberships, nextMemberships, rankByAthlete, names, images, leaguesById);
    }

    internal static async Task<Dictionary<int, int>> LoadSourceRanksAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<LeagueEntity> sourceFeeders,
        LeagueEntity? sourceSuperleague,
        CancellationToken cancellationToken)
    {
        Dictionary<int, int> ranks = new();
        List<LeagueEntity> leagues = [.. sourceFeeders];
        if (sourceSuperleague is not null)
        {
            leagues.Add(sourceSuperleague);
        }

        foreach (LeagueEntity league in leagues)
        {
            List<SeasonStandingEntity> rows = await context.SeasonStandings
                .AsNoTracking()
                .Where(e => e.SeasonId == source.Id && e.LeagueId == league.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (SeasonStandingEntity row in rows)
            {
                ranks[row.SaveAthleteId] = row.SeasonRank;
            }
        }

        return ranks;
    }

    internal static async Task<List<RebalanceColorResult>> MapColorResultsAsync(
        SaveDbContext context,
        SeasonEntity next,
        RebalanceFeedersSelection.RebalancePlan plan,
        IReadOnlyList<RebalanceMovementMember> departed,
        IReadOnlyList<RebalanceMovementMember> returned,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> nextFeeders = await LoadNextFeedersForResultsAsync(context, next, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> counts = await LoadActiveCountsAsync(context, next, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, (int Departed, int Returned)> transfersByColor =
            RebalanceSuperleagueTransfers.CountsByColor(departed, returned);

        List<RebalanceColorResult> colors = new(nextFeeders.Count);
        foreach (LeagueEntity feeder in nextFeeders)
        {
            colors.Add(MapSingleColorResult(feeder, plan, counts, transfersByColor));
        }

        return colors;
    }

    internal static async Task<List<LeagueEntity>> LoadNextFeedersForResultsAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        return await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .OrderBy(e => e.SportingColor)
            .ThenBy(e => e.FeederDivision)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, int>> LoadActiveCountsAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        return await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.LeagueId != null)
            .GroupBy(e => e.LeagueId!.Value)
            .ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static RebalanceColorResult MapSingleColorResult(
        LeagueEntity feeder,
        RebalanceFeedersSelection.RebalancePlan plan,
        Dictionary<int, int> counts,
        IReadOnlyDictionary<string, (int Departed, int Returned)> transfersByColor)
    {
        bool isF1 = feeder.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First;
        RebalanceFeedersSelection.ColorPlan colorPlan = plan.PerColor.Single(p => p.Color == (SportingColor)feeder.SportingColor);
        counts.TryGetValue(feeder.Id, out int finalCount);
        string colorName = ((SportingColor)feeder.SportingColor).ToString();
        transfersByColor.TryGetValue(colorName, out (int Departed, int Returned) transfers);
        if (!isF1)
        {
            return MapLowerDivisionResult(feeder, colorName, finalCount);
        }

        return MapF1Result(feeder, colorPlan, colorName, transfers, finalCount);
    }

    internal static RebalanceColorResult MapLowerDivisionResult(
        LeagueEntity feeder, string colorName, int finalCount)
    {
        // MSS-057: F2/F3 carry over at 32 with no Superleague transfers;
        // provisional is 32 and no draws/displacements occur.
        return new RebalanceColorResult(
            feeder.Id,
            feeder.Name,
            colorName,
            32,
            0,
            0,
            32,
            0,
            0,
            finalCount);
    }

    internal static RebalanceColorResult MapF1Result(
        LeagueEntity feeder,
        RebalanceFeedersSelection.ColorPlan colorPlan,
        string colorName,
        (int Departed, int Returned) transfers,
        int finalCount)
    {
        int expectedProvisional = 32 - transfers.Departed + transfers.Returned;
        if (expectedProvisional != colorPlan.ProvisionalCount)
        {
            throw new InvalidOperationException(
                $"League '{feeder.Name}' provisional count {colorPlan.ProvisionalCount} does not match Superleague transfers (32 - {transfers.Departed} + {transfers.Returned} = {expectedProvisional}).");
        }

        return new RebalanceColorResult(
            feeder.Id,
            feeder.Name,
            colorName,
            32,
            transfers.Departed,
            transfers.Returned,
            colorPlan.ProvisionalCount,
            colorPlan.Displaced.Count,
            colorPlan.Draws.Count,
            finalCount);
    }

    internal static async Task<List<MovementEntity>> LoadRebalanceMovementsAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        return await context.Movements
            .AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static List<RebalanceMovementMember> MapMovements(
        List<MovementEntity> movements,
        MovementKind kind,
        Dictionary<int, string> names,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, string?>? images = null)
    {
        List<RebalanceMovementMember> members = new();
        foreach (MovementEntity movement in movements
            .Where(m => m.Kind == (int)kind)
            .OrderBy(m => m.SaveAthleteId))
        {
            names.TryGetValue(movement.SaveAthleteId, out string? name);
            string? imageUrl = null;
            images?.TryGetValue(movement.SaveAthleteId, out imageUrl);
            string fromName = movement.FromLeagueId == PoolSentinelLeagueId
                ? "Common Pool"
                : leaguesById.TryGetValue(movement.FromLeagueId, out LeagueEntity? from) ? from.Name : $"League {movement.FromLeagueId}";
            string toName = movement.ToLeagueId == PoolSentinelLeagueId
                ? "Common Pool"
                : leaguesById.TryGetValue(movement.ToLeagueId, out LeagueEntity? to) ? to.Name : $"League {movement.ToLeagueId}";
            members.Add(new RebalanceMovementMember(
                movement.SaveAthleteId,
                name ?? $"Athlete {movement.SaveAthleteId}",
                ((SportingColor)movement.SportingColor).ToString(),
                movement.FromLeagueId,
                fromName,
                movement.ToLeagueId,
                toName,
                kind.ToString(),
                movement.FromSeasonRank,
                imageUrl));
        }

        return members;
    }
}
