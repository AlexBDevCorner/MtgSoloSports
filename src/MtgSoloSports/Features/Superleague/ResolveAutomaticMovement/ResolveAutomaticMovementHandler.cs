using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Implements fixed automatic
/// movement for a completed Superleague season (Season 2 onward): Superleague
/// 1-16 stay safe, 17-24 become qualifier incumbents, 25-32 are automatically
/// relegated to their returning-color feeders, and every feeder champion is
/// automatically promoted. Feeder ranks 2-4 become qualifier challengers.
/// Creates the consecutive next season (HasSuperleague, incomplete) with nine
/// leagues and full 2,048 memberships: the next Superleague provisionally
/// holds 16 safe + 8 promoted + 8 incumbents (32), feeders hold retained plus
/// returning athletes pending rebalancing, and the common-pool athlete set is
/// identical (pool draws are reserved for the rebalancing flow). Persists 48
/// movement rows (8 promotions, 8 relegations, 8 incumbents, 24 challengers),
/// keeps every athlete's sporting color and all bonus history (standing rows
/// untouched, no RNG consumed), and refreshes projections in one transaction.
/// <c>SaveMetadata.CurrentSeason</c> is left unchanged; a later slice advances
/// once rebalancing yields valid 32-per-league rosters. Holds one per-save
/// lock; read-only movement queries never lock. No Superleague color quota.
/// </summary>
public sealed class ResolveAutomaticMovementHandler
{
    private readonly SaveStore _store;

    public ResolveAutomaticMovementHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ResolveAutomaticMovementResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ResolveUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<ResolveAutomaticMovementResponse> ResolveUnderLockAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        var metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        ResolutionInputs inputs = await LoadResolutionInputsAsync(context, rules, cancellationToken).ConfigureAwait(false);
        ResolutionOutput output = await PersistResolutionAsync(context, inputs, rules, cancellationToken).ConfigureAwait(false);

        await EmitAutomaticStoriesAsync(context, inputs, output, cancellationToken).ConfigureAwait(false);

        metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.AutomaticMovementResolved);
        await Features.Athletes.Projections.AthleteProjectionUpdater.RebuildAllAsync(context, rules, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, output.Source, output.Next, output.NextSuperleague, output.Plan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits automatic promotion, automatic relegation and first-Superleague-
    /// appearance story events transactionally with movement resolution.
    /// Qualifier-candidate markers emit nothing yet; the qualifier slice emits
    /// qualifier-decided promotion/relegation. Idempotent via dedup keys.
    /// </summary>
    internal static async Task EmitAutomaticStoriesAsync(
        SaveDbContext context,
        ResolutionInputs inputs,
        ResolutionOutput output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(output);
        Dictionary<int, string> sourceNames = inputs.SourceFeeders.ToDictionary(l => l.Id, l => l.Name);
        sourceNames[inputs.SourceSuperleague.Id] = inputs.SourceSuperleague.Name;
        Dictionary<int, string> athleteNames = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        string movementDedup = Features.Stories.StoryEventEmitter.MovementDedup(
            inputs.Source.SeasonNumber, output.Next.SeasonNumber);
        bool emitted = await EmitAutomaticPromotionsAsync(context, inputs, output, sourceNames, athleteNames, movementDedup, cancellationToken).ConfigureAwait(false);
        emitted |= await EmitAutomaticRelegationsAsync(context, inputs, output, sourceNames, athleteNames, movementDedup, cancellationToken).ConfigureAwait(false);
        emitted |= await EmitAutomaticFirstAppearancesAsync(context, inputs, output, athleteNames, cancellationToken).ConfigureAwait(false);
        if (emitted)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> EmitAutomaticPromotionsAsync(
        SaveDbContext context,
        ResolutionInputs inputs,
        ResolutionOutput output,
        Dictionary<int, string> sourceNames,
        Dictionary<int, string> athleteNames,
        string movementDedup,
        CancellationToken cancellationToken)
    {
        bool emitted = false;
        foreach (AutomaticMovementSelection.AutomaticPick pick in output.Plan.Promotions.OrderBy(p => p.FromLeagueId).ThenBy(p => p.FromSeasonRank))
        {
            athleteNames.TryGetValue(pick.SaveAthleteId, out string? name);
            sourceNames.TryGetValue(pick.FromLeagueId, out string? fromName);
            emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
                context,
                pick.SaveAthleteId,
                Features.Stories.StoryEventType.Promotion,
                movementDedup,
                output.Next.SeasonNumber,
                null,
                new Features.Stories.StoryEventPayload(
                    name ?? $"Athlete {pick.SaveAthleteId}",
                    output.Next.SeasonNumber,
                    FromLeagueName: fromName ?? $"League {pick.FromLeagueId}",
                    ToLeagueName: output.NextSuperleague.Name,
                    FromSeasonNumber: inputs.Source.SeasonNumber,
                    ToSeasonNumber: output.Next.SeasonNumber,
                    FromSeasonRank: pick.FromSeasonRank),
                cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    internal static async Task<bool> EmitAutomaticRelegationsAsync(
        SaveDbContext context,
        ResolutionInputs inputs,
        ResolutionOutput output,
        Dictionary<int, string> sourceNames,
        Dictionary<int, string> athleteNames,
        string movementDedup,
        CancellationToken cancellationToken)
    {
        Dictionary<int, LeagueEntity> nextLeagues = (await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == output.Next.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToDictionary(l => l.Id);
        bool emitted = false;
        foreach (AutomaticMovementSelection.AutomaticPick pick in output.Plan.Relegations.OrderBy(p => p.FromLeagueId).ThenBy(p => p.FromSeasonRank))
        {
            athleteNames.TryGetValue(pick.SaveAthleteId, out string? name);
            sourceNames.TryGetValue(pick.FromLeagueId, out string? fromName);
            SeasonMembershipEntity? nextMembership = await context.SeasonMemberships
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    e => e.SeasonId == output.Next.Id && e.SaveAthleteId == pick.SaveAthleteId,
                    cancellationToken)
                .ConfigureAwait(false);
            string toName = nextMembership?.LeagueId is not null && nextLeagues.TryGetValue(nextMembership.LeagueId.Value, out LeagueEntity? to)
                ? to.Name
                : "the feeder league";
            emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
                context,
                pick.SaveAthleteId,
                Features.Stories.StoryEventType.Relegation,
                movementDedup,
                output.Next.SeasonNumber,
                null,
                new Features.Stories.StoryEventPayload(
                    name ?? $"Athlete {pick.SaveAthleteId}",
                    output.Next.SeasonNumber,
                    FromLeagueName: fromName ?? $"League {pick.FromLeagueId}",
                    ToLeagueName: toName,
                    FromSeasonNumber: inputs.Source.SeasonNumber,
                    ToSeasonNumber: output.Next.SeasonNumber,
                    FromSeasonRank: pick.FromSeasonRank),
                cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    internal static async Task<bool> EmitAutomaticFirstAppearancesAsync(
        SaveDbContext context,
        ResolutionInputs inputs,
        ResolutionOutput output,
        Dictionary<int, string> athleteNames,
        CancellationToken cancellationToken)
    {
        List<int> nextSuperIds = await context.SeasonMemberships
            .Where(e => e.SeasonId == output.Next.Id && e.LeagueId == output.NextSuperleague.Id)
            .Select(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool emitted = false;
        foreach (int athleteId in nextSuperIds.OrderBy(id => id))
        {
            bool hadPrior = await Features.Stories.StoryEventEmitter.HasPriorSuperleagueAppearanceAsync(
                context, athleteId, output.Next.Id, cancellationToken).ConfigureAwait(false);
            if (hadPrior)
            {
                continue;
            }

            athleteNames.TryGetValue(athleteId, out string? name);
            emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
                context,
                athleteId,
                Features.Stories.StoryEventType.FirstSuperleagueAppearance,
                Features.Stories.StoryEventEmitter.FirstDedup,
                output.Next.SeasonNumber,
                null,
                new Features.Stories.StoryEventPayload(
                    name ?? $"Athlete {athleteId}",
                    output.Next.SeasonNumber,
                    ToLeagueName: output.NextSuperleague.Name,
                    FromSeasonNumber: inputs.Source.SeasonNumber,
                    ToSeasonNumber: output.Next.SeasonNumber),
                cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    internal sealed record ResolutionInputs(
        SeasonEntity Source,
        List<LeagueEntity> SourceFeeders,
        LeagueEntity SourceSuperleague,
        List<SeasonStandingEntity> SuperStandings,
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> FeederStandings,
        List<SeasonMembershipEntity> SourceMemberships,
        AutomaticMovementSelection.AutomaticPlan Plan,
        FeederMovementSelection.FeederPlan? FeederPlan,
        ulong RngBefore,
        int StageCountBefore,
        int SeasonCountBefore);

    internal sealed record ResolutionOutput(
        SeasonEntity Source,
        SeasonEntity Next,
        List<LeagueEntity> NextFeeders,
        LeagueEntity NextSuperleague,
        AutomaticMovementSelection.AutomaticPlan Plan,
        FeederMovementSelection.FeederPlan? FeederPlan);

    internal async Task<ResolutionInputs> LoadResolutionInputsAsync(
        SaveDbContext context, RulesV1 rules, CancellationToken cancellationToken)
    {
        (ulong rngBefore, int stages, int seasons) = await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceSeasonAsync(context, rules, cancellationToken).ConfigureAwait(false);
        await EnsureNotAlreadyResolvedAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> feeders = await LoadSourceFeedersAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        LeagueEntity superleague = await LoadSourceSuperleagueAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> super = await LoadLeagueStandingsAsync(context, source, superleague, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederMap = await LoadFeederStandingsAsync(context, source, feeders, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> memberships = await LoadSourceMembershipsAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        AutomaticMovementSelection.AutomaticPlan plan = AutomaticMovementSelection.Select(super, feederMap, feeders, superleague, rules);
        AutomaticMovementInvariants.ValidateSelection(plan, super, feederMap, feeders, superleague, rules);
        Dictionary<int, SeasonMembershipEntity> byAthlete = memberships.ToDictionary(m => m.SaveAthleteId);
        ValidatePicksHaveMemberships(plan, byAthlete);
        ValidateNoDualActiveSource(memberships);

        FeederMovementSelection.FeederPlan? feederPlan = null;
        if (rules.FeederDivisionsPerColor == 3)
        {
            feederPlan = SelectFeederPlan(feeders, feederMap, rules);
            ValidateFeederPlan(feederPlan, feeders, feederMap, rules);
            ValidateNoOverlapWithSuperleague(plan, feederPlan);
            foreach (FeederMovementSelection.FeederPick pick in feederPlan.All)
            {
                if (!byAthlete.ContainsKey(pick.SaveAthleteId))
                {
                    throw new InvalidOperationException($"Feeder movement athlete {pick.SaveAthleteId} has no source membership.");
                }
            }
        }

        return new ResolutionInputs(source, feeders, superleague, super, feederMap, memberships, plan, feederPlan, rngBefore, stages, seasons);
    }

    internal static FeederMovementSelection.FeederPlan SelectFeederPlan(
        List<LeagueEntity> feeders,
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederMap,
        RulesV1 rules)
    {
        Dictionary<int, LeagueEntity> leaguesById = feeders.ToDictionary(l => l.Id);
        Dictionary<int, LeagueEntity> f1ByColor = feeders
            .Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First)
            .ToDictionary(l => l.SportingColor);
        Dictionary<int, LeagueEntity> f2ByColor = feeders
            .Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.Second)
            .ToDictionary(l => l.SportingColor);
        Dictionary<int, LeagueEntity> f3ByColor = feeders
            .Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.Third)
            .ToDictionary(l => l.SportingColor);
        if (f1ByColor.Count != rules.RegularLeagueCount
            || f2ByColor.Count != rules.RegularLeagueCount
            || f3ByColor.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException("Tiered transition must have 8 F1, 8 F2 and 8 F3 leagues.");
        }

        return FeederMovementSelection.Select(leaguesById, feederMap, f1ByColor, f2ByColor, f3ByColor, rules);
    }

    internal static void ValidateFeederPlan(
        FeederMovementSelection.FeederPlan feederPlan,
        List<LeagueEntity> feeders,
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> feederMap,
        RulesV1 rules)
    {
        Dictionary<int, LeagueEntity> f1ByColor = feeders
            .Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First)
            .ToDictionary(l => l.SportingColor);
        Dictionary<int, LeagueEntity> f2ByColor = feeders
            .Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.Second)
            .ToDictionary(l => l.SportingColor);
        Dictionary<int, LeagueEntity> f3ByColor = feeders
            .Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.Third)
            .ToDictionary(l => l.SportingColor);
        FeederMovementInvariants.ValidateSelection(feederPlan, f1ByColor, f2ByColor, f3ByColor, feederMap, rules);
    }

    internal static void ValidateNoOverlapWithSuperleague(
        AutomaticMovementSelection.AutomaticPlan superPlan,
        FeederMovementSelection.FeederPlan feederPlan)
    {
        HashSet<int> superIds = superPlan.All.Select(p => p.SaveAthleteId).ToHashSet();
        foreach (FeederMovementSelection.FeederPick pick in feederPlan.All)
        {
            if (superIds.Contains(pick.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Athlete {pick.SaveAthleteId} appears in both Superleague and feeder movement; rank bands must be non-overlapping and one tier per postseason.");
            }
        }
    }

    internal static async Task<ResolutionOutput> PersistResolutionAsync(
        SaveDbContext context, ResolutionInputs inputs, RulesV1 rules, CancellationToken cancellationToken)
    {
        SeasonEntity next = await CreateNextSeasonAsync(context, inputs.Source, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> nextFeeders = await CreateNextFeedersAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        LeagueEntity nextSuperleague = await CreateNextSuperleagueAsync(context, next, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> byAthlete = inputs.SourceMemberships.ToDictionary(m => m.SaveAthleteId);
        List<SeasonMembershipEntity> nextMemberships = BuildNextMemberships(
            inputs.SourceMemberships, byAthlete, inputs.Plan, inputs.FeederPlan,
            inputs.SuperStandings, inputs.FeederStandings, next, nextFeeders, nextSuperleague, rules, inputs.SourceFeeders);
        foreach (SeasonMembershipEntity membership in nextMemberships)
        {
            context.SeasonMemberships.Add(membership);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        List<MovementEntity> movements = BuildMovementsWithSource(
            inputs.Plan, inputs.FeederPlan, byAthlete, inputs.Source, next, nextFeeders, nextSuperleague,
            inputs.SourceFeeders, rules);
        foreach (MovementEntity movement in movements)
        {
            context.Movements.Add(movement);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await ValidatePersistedAsync(context, inputs, next, nextFeeders, nextSuperleague, rules, cancellationToken).ConfigureAwait(false);
        return new ResolutionOutput(inputs.Source, next, nextFeeders, nextSuperleague, inputs.Plan, inputs.FeederPlan);
    }

    internal static async Task ValidatePersistedAsync(
        SaveDbContext context,
        ResolutionInputs inputs,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> persistedNext = await context.SeasonMemberships
            .Where(e => e.SeasonId == next.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<MovementEntity> persistedMovements = await context.Movements
            .Where(e => e.FromSeasonId == inputs.Source.Id && e.ToSeasonId == next.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // Superleague invariants validate the 48 SL rows scoped to SL kinds;
        // feeder rows use distinct kinds and are validated separately below.
        List<MovementEntity> superMovements = persistedMovements
            .Where(m => m.Kind == (int)MovementKind.AutomaticPromotion
                || m.Kind == (int)MovementKind.AutomaticRelegation
                || m.Kind == (int)MovementKind.QualifierIncumbent
                || m.Kind == (int)MovementKind.QualifierChallenger)
            .ToList();
        // ValidateCreated expects only SL movements for feeder totals pending
        // rebalance; pass scoped lists so feeder autos don't break SL counts.
        // Full feeder totals are checked via feeder invariants below.
        AutomaticMovementInvariants.ValidateCreated(
            inputs.Source, next, nextSuperleague, nextFeeders, inputs.SourceMemberships, persistedNext, superMovements, rules);
        if (inputs.FeederPlan is not null)
        {
            await ValidateFeederPersistedAsync(context, inputs, next, nextFeeders, persistedNext, persistedMovements, rules, cancellationToken).ConfigureAwait(false);
        }

        await VerifyPreservationAsync(context, inputs.RngBefore, inputs.StageCountBefore, inputs.SeasonCountBefore, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ValidateFeederPersistedAsync(
        SaveDbContext context,
        ResolutionInputs inputs,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        List<SeasonMembershipEntity> persistedNext,
        List<MovementEntity> persistedMovements,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        if (inputs.FeederPlan is null)
        {
            throw new ArgumentNullException(nameof(inputs));
        }

        List<MovementEntity> feederMovements = GetFeederMovements(persistedMovements);
        CheckFeederMovementCount(feederMovements, rules);

        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        FeederMovementInvariants.ValidateAdjacentTiers(feederMovements, leaguesById);

        CheckNoSuperOverlap(persistedMovements, feederMovements);
        CheckFeederUniqueness(feederMovements);
        CheckTieredLeagueTotals(persistedNext, nextFeeders, rules);
    }

    private static List<MovementEntity> GetFeederMovements(List<MovementEntity> persistedMovements)
    {
        return persistedMovements
            .Where(m => m.Kind == (int)MovementKind.FeederAutomaticPromotion
                || m.Kind == (int)MovementKind.FeederAutomaticRelegation
                || m.Kind == (int)MovementKind.FeederQualifierIncumbent
                || m.Kind == (int)MovementKind.FeederQualifierChallenger)
            .ToList();
    }

    private static void CheckFeederMovementCount(List<MovementEntity> feederMovements, RulesV1 rules)
    {
        int expectedFeeder = rules.SportingColorCount * 8 * 8;
        if (feederMovements.Count != expectedFeeder)
        {
            throw new InvalidOperationException(
                $"Feeder movement must persist exactly {expectedFeeder} records, was {feederMovements.Count}.");
        }
    }

    private static void CheckNoSuperOverlap(
        List<MovementEntity> persistedMovements,
        List<MovementEntity> feederMovements)
    {
        // No athlete in contradictory roles across SL + feeder.
        HashSet<int> superIds = persistedMovements
            .Where(m => m.Kind == (int)MovementKind.AutomaticPromotion
                || m.Kind == (int)MovementKind.AutomaticRelegation
                || m.Kind == (int)MovementKind.QualifierIncumbent
                || m.Kind == (int)MovementKind.QualifierChallenger)
            .Select(m => m.SaveAthleteId)
            .ToHashSet();
        foreach (MovementEntity movement in feederMovements)
        {
            if (superIds.Contains(movement.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Athlete {movement.SaveAthleteId} appears in both Superleague and feeder movement.");
            }
        }
    }

    private static void CheckFeederUniqueness(List<MovementEntity> feederMovements)
    {
        HashSet<int> feederIds = feederMovements.Select(m => m.SaveAthleteId).ToHashSet();
        if (feederIds.Count != feederMovements.Count)
        {
            throw new InvalidOperationException("Feeder movement contains duplicate athlete ids.");
        }
    }

    private static void CheckTieredLeagueTotals(
        List<SeasonMembershipEntity> persistedNext,
        List<LeagueEntity> nextFeeders,
        RulesV1 rules)
    {
        // F2/F3 provisional totals must already be exact (32 each per color);
        // F1 varies pending SL exchange + qualifier + rebalance.
        Dictionary<int, int> countsByLeague = persistedNext
            .Where(m => m.LeagueId.HasValue)
            .GroupBy(m => m.LeagueId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (LeagueEntity league in nextFeeders)
        {
            if (league.FeederDivision != (int)SimulationKernel.Leagues.FeederDivision.Second
                && league.FeederDivision != (int)SimulationKernel.Leagues.FeederDivision.Third)
            {
                continue;
            }

            countsByLeague.TryGetValue(league.Id, out int count);
            if (count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"Tiered league '{league.Name}' must provisionally hold exactly {rules.LeagueSize} athletes after automatic movement, was {count}.");
            }
        }
    }

    internal static async Task<(ulong RngState, int StageCount, int SeasonCount)> CapturePreservationAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        unchecked
        {
            return ((ulong)rng.State, stages, seasons);
        }
    }

    internal static async Task VerifyPreservationAsync(
        SaveDbContext context,
        ulong rngBefore,
        int stageCountBefore,
        int seasonCountBefore,
        CancellationToken cancellationToken)
    {
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        unchecked
        {
            if ((ulong)rng.State != rngBefore)
            {
                throw new InvalidOperationException("Automatic movement must not consume sporting RNG.");
            }
        }

        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        if (stages != stageCountBefore)
        {
            throw new InvalidOperationException("Automatic movement must preserve stage bonus history.");
        }

        if (seasons != seasonCountBefore)
        {
            throw new InvalidOperationException("Automatic movement must preserve season bonus history.");
        }
    }

    internal static async Task<SeasonEntity> LoadSourceSeasonAsync(
        SaveDbContext context, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<SeasonEntity> candidates = await context.Seasons
            .Where(e => e.IsComplete && e.HasSuperleague)
            .OrderByDescending(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new ResolveAutomaticMovementConflictException(
                "No completed Superleague season is ready for automatic movement.");
        }

        foreach (SeasonEntity candidate in candidates.OrderByDescending(e => e.SeasonNumber))
        {
            bool hasSuccessor = await context.Seasons.AnyAsync(
                e => e.SeasonNumber == candidate.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
            if (!hasSuccessor)
            {
                return candidate;
            }
        }

        SeasonEntity latest = candidates.OrderByDescending(e => e.SeasonNumber).First();
        throw new ResolveAutomaticMovementConflictException(
            $"Automatic movement for Season {latest.SeasonNumber} has already been resolved.");
    }

    internal static async Task EnsureNotAlreadyResolvedAsync(
        SaveDbContext context, SeasonEntity source, CancellationToken cancellationToken)
    {
        bool hasSuccessor = await context.Seasons.AnyAsync(
            e => e.SeasonNumber == source.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
        if (hasSuccessor)
        {
            throw new ResolveAutomaticMovementConflictException(
                $"Automatic movement for Season {source.SeasonNumber} has already been resolved.");
        }

        int[] kinds =
        [
            (int)MovementKind.AutomaticPromotion,
            (int)MovementKind.AutomaticRelegation,
            (int)MovementKind.QualifierIncumbent,
            (int)MovementKind.QualifierChallenger,
        ];
        bool hasMovements = await context.Movements
            .AnyAsync(e => e.FromSeasonId == source.Id && kinds.Contains(e.Kind), cancellationToken)
            .ConfigureAwait(false);
        if (hasMovements)
        {
            throw new ResolveAutomaticMovementConflictException(
                $"Automatic movement for Season {source.SeasonNumber} has already been resolved.");
        }
    }

    internal static async Task<List<LeagueEntity>> LoadSourceFeedersAsync(
        SaveDbContext context, SeasonEntity source, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Feeder)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count != rules.TieredFeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season {source.SeasonNumber} must have exactly {rules.TieredFeederLeagueCount} feeder leagues, was {leagues.Count}.");
        }

        return leagues;
    }

    internal static async Task<LeagueEntity> LoadSourceSuperleagueAsync(
        SaveDbContext context, SeasonEntity source, CancellationToken cancellationToken)
    {
        LeagueEntity? league = await context.Leagues
            .SingleOrDefaultAsync(
                e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Superleague,
                cancellationToken)
            .ConfigureAwait(false);
        if (league is null)
        {
            throw new InvalidOperationException($"Season {source.SeasonNumber} has no Superleague.");
        }

        return league;
    }

    internal static async Task<List<SeasonStandingEntity>> LoadLeagueStandingsAsync(
        SaveDbContext context,
        SeasonEntity source,
        LeagueEntity league,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == league.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' must have exactly {rules.LeagueSize} final standings, was {rows.Count}.");
        }

        HashSet<int> ranks = rows.Select(r => r.SeasonRank).ToHashSet();
        if (!ranks.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
        {
            throw new InvalidOperationException($"League '{league.Name}' must cover ranks 1..{rules.LeagueSize} exactly once.");
        }

        return rows;
    }

    internal static async Task<Dictionary<int, IReadOnlyList<SeasonStandingEntity>>> LoadFeederStandingsAsync(
        SaveDbContext context,
        SeasonEntity source,
        List<LeagueEntity> feeders,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> map = new(feeders.Count);
        foreach (LeagueEntity feeder in feeders)
        {
            List<SeasonStandingEntity> rows = await LoadLeagueStandingsAsync(context, source, feeder, rules, cancellationToken).ConfigureAwait(false);
            map[feeder.Id] = rows;
        }

        return map;
    }

    internal static async Task<List<SeasonMembershipEntity>> LoadSourceMembershipsAsync(
        SaveDbContext context, SeasonEntity source, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Season {source.SeasonNumber} must have exactly {rules.TotalAthletesInSave} memberships, was {memberships.Count}.");
        }

        return memberships;
    }

    internal static void ValidatePicksHaveMemberships(
        AutomaticMovementSelection.AutomaticPlan plan,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete)
    {
        foreach (AutomaticMovementSelection.AutomaticPick pick in plan.All)
        {
            if (!membershipByAthlete.ContainsKey(pick.SaveAthleteId))
            {
                throw new InvalidOperationException($"Movement athlete {pick.SaveAthleteId} has no source membership.");
            }
        }
    }

    internal static void ValidateNoDualActiveSource(List<SeasonMembershipEntity> sourceMemberships)
    {
        HashSet<int> active = new();
        foreach (SeasonMembershipEntity membership in sourceMemberships)
        {
            if (membership.LeagueId is null)
            {
                continue;
            }

            if (!active.Add(membership.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Athlete {membership.SaveAthleteId} is active in two leagues in the source season.");
            }
        }
    }

    internal static async Task<SeasonEntity> CreateNextSeasonAsync(
        SaveDbContext context, SeasonEntity source, CancellationToken cancellationToken)
    {
        SeasonEntity next = new()
        {
            SeasonNumber = source.SeasonNumber + 1,
            HasSuperleague = true,
            IsComplete = false,
        };
        context.Seasons.Add(next);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return next;
    }

    internal static async Task<List<LeagueEntity>> CreateNextFeedersAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        return await CreateNextFeedersAsync(context, next, RulesV1.CreateDefault(), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<List<LeagueEntity>> CreateNextFeedersAsync(
        SaveDbContext context, SeasonEntity next, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = [];
        bool tiered = rules.FeederDivisionsPerColor == 3;
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            leagues.Add(new LeagueEntity
            {
                SeasonId = next.Id,
                SportingColor = (int)color,
                Kind = (int)LeagueKind.Feeder,
                FeederDivision = (int)SimulationKernel.Leagues.FeederDivision.First,
                Name = $"{color} League",
            });

            if (!tiered)
            {
                continue;
            }

            leagues.Add(new LeagueEntity
            {
                SeasonId = next.Id,
                SportingColor = (int)color,
                Kind = (int)LeagueKind.Feeder,
                FeederDivision = (int)SimulationKernel.Leagues.FeederDivision.Second,
                Name = $"{color} League F2",
            });
            leagues.Add(new LeagueEntity
            {
                SeasonId = next.Id,
                SportingColor = (int)color,
                Kind = (int)LeagueKind.Feeder,
                FeederDivision = (int)SimulationKernel.Leagues.FeederDivision.Third,
                Name = $"{color} League F3",
            });
        }

        foreach (LeagueEntity league in leagues)
        {
            context.Leagues.Add(league);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return leagues;
    }

    internal static async Task<LeagueEntity> CreateNextSuperleagueAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        LeagueEntity league = new()
        {
            SeasonId = next.Id,
            SportingColor = (int)SportingColor.White,
            Kind = (int)LeagueKind.Superleague,
            FeederDivision = (int)SimulationKernel.Leagues.FeederDivision.None,
            Name = "Superleague",
        };
        context.Leagues.Add(league);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return league;
    }

    internal static List<SeasonMembershipEntity> BuildNextMemberships(
        List<SeasonMembershipEntity> sourceMemberships,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete,
        AutomaticMovementSelection.AutomaticPlan plan,
        IReadOnlyList<SeasonStandingEntity> superStandings,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandings,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague,
        RulesV1 rules)
    {
        // Source feeders are not passed in the legacy 9-arg shape; resolve
        // divisions from draw-index ranges for tiered carryover where possible.
        // New code passes explicit source feeders via the 10-arg overload.
        return BuildNextMemberships(
            sourceMemberships, membershipByAthlete, plan, superStandings, feederStandings,
            next, nextFeeders, nextSuperleague, rules, sourceFeeders: null);
    }

    internal static List<SeasonMembershipEntity> BuildNextMemberships(
        List<SeasonMembershipEntity> sourceMemberships,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete,
        AutomaticMovementSelection.AutomaticPlan plan,
        IReadOnlyList<SeasonStandingEntity> superStandings,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandings,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague,
        RulesV1 rules,
        IReadOnlyList<LeagueEntity>? sourceFeeders)
    {
        return BuildNextMemberships(
            sourceMemberships, membershipByAthlete, plan, feederPlan: null,
            superStandings, feederStandings, next, nextFeeders, nextSuperleague, rules, sourceFeeders);
    }

    internal static List<SeasonMembershipEntity> BuildNextMemberships(
        List<SeasonMembershipEntity> sourceMemberships,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete,
        AutomaticMovementSelection.AutomaticPlan plan,
        FeederMovementSelection.FeederPlan? feederPlan,
        IReadOnlyList<SeasonStandingEntity> superStandings,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandings,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague,
        RulesV1 rules,
        IReadOnlyList<LeagueEntity>? sourceFeeders)
    {
        HashSet<int> promoted = plan.Promotions.Select(p => p.SaveAthleteId).ToHashSet();
        HashSet<int> relegated = plan.Relegations.Select(p => p.SaveAthleteId).ToHashSet();
        HashSet<int> incumbents = plan.QualifierIncumbents.Select(p => p.SaveAthleteId).ToHashSet();
        HashSet<int> challengers = plan.QualifierChallengers.Select(p => p.SaveAthleteId).ToHashSet();
        HashSet<int> safe = superStandings
            .Where(r => r.SeasonRank >= 1 && r.SeasonRank <= rules.SuperleagueSafeCount)
            .Select(r => r.SaveAthleteId)
            .ToHashSet();

        if (safe.Count != rules.SuperleagueSafeCount)
        {
            throw new InvalidOperationException(
                $"Superleague must hold exactly {rules.SuperleagueSafeCount} safe athletes, was {safe.Count}.");
        }

        bool tiered = rules.FeederDivisionsPerColor == 3;
        Dictionary<int, int> feederByColor = !tiered
            ? nextFeeders.ToDictionary(l => l.SportingColor, l => l.Id)
            : new Dictionary<int, int>();
        Dictionary<(int Color, int Division), int> feederByColorDivision = tiered
            ? nextFeeders.ToDictionary(l => (l.SportingColor, l.FeederDivision), l => l.Id)
            : new Dictionary<(int, int), int>();
        Dictionary<int, int> sourceDivisionByLeague = sourceFeeders is not null
            ? sourceFeeders.ToDictionary(l => l.Id, l => l.FeederDivision)
            : new Dictionary<int, int>();

        FeederSets? feederSets = feederPlan is not null ? FeederSets.FromPlan(feederPlan) : null;
        List<SeasonMembershipEntity> nextMemberships = new(sourceMemberships.Count);
        foreach (SeasonMembershipEntity source in sourceMemberships.OrderBy(m => m.SaveAthleteId))
        {
            int? leagueId = tiered
                ? ResolveNextLeagueTiered(
                    source, promoted, relegated, incumbents, challengers, safe, feederSets,
                    feederByColorDivision, sourceDivisionByLeague, nextSuperleague, rules)
                : ResolveNextLeague(source, promoted, relegated, incumbents, challengers, safe, feederByColor, nextSuperleague);
            nextMemberships.Add(new SeasonMembershipEntity
            {
                SeasonId = next.Id,
                LeagueId = leagueId,
                SaveAthleteId = source.SaveAthleteId,
                SportingColor = source.SportingColor,
                DrawIndex = source.DrawIndex,
            });
        }

        return nextMemberships;
    }

    internal sealed record FeederSets(
        HashSet<int> F1Incumbents,
        HashSet<int> F1Relegated,
        HashSet<int> F2Promoted,
        HashSet<int> F2Challengers,
        HashSet<int> F2Incumbents,
        HashSet<int> F2Relegated,
        HashSet<int> F3Promoted,
        HashSet<int> F3Challengers)
    {
        public static FeederSets FromPlan(FeederMovementSelection.FeederPlan plan)
        {
            HashSet<int> f1Inc = new();
            HashSet<int> f1Rel = new();
            HashSet<int> f2Pro = new();
            HashSet<int> f2Cha = new();
            HashSet<int> f2Inc = new();
            HashSet<int> f2Rel = new();
            HashSet<int> f3Pro = new();
            HashSet<int> f3Cha = new();
            foreach (FeederMovementSelection.ColorBoundaryPlan color in plan.PerColor)
            {
                foreach (var p in color.F1Incumbents)
                {
                    f1Inc.Add(p.SaveAthleteId);
                }

                foreach (var p in color.F1Relegated)
                {
                    f1Rel.Add(p.SaveAthleteId);
                }

                foreach (var p in color.F2PromotedToF1)
                {
                    f2Pro.Add(p.SaveAthleteId);
                }

                foreach (var p in color.F2Challengers)
                {
                    f2Cha.Add(p.SaveAthleteId);
                }

                foreach (var p in color.F2Incumbents)
                {
                    f2Inc.Add(p.SaveAthleteId);
                }

                foreach (var p in color.F2Relegated)
                {
                    f2Rel.Add(p.SaveAthleteId);
                }

                foreach (var p in color.F3PromotedToF2)
                {
                    f3Pro.Add(p.SaveAthleteId);
                }

                foreach (var p in color.F3Challengers)
                {
                    f3Cha.Add(p.SaveAthleteId);
                }
            }

            return new FeederSets(f1Inc, f1Rel, f2Pro, f2Cha, f2Inc, f2Rel, f3Pro, f3Cha);
        }
    }

    internal static int? ResolveNextLeagueTiered(
        SeasonMembershipEntity source,
        HashSet<int> promoted,
        HashSet<int> relegated,
        HashSet<int> incumbents,
        HashSet<int> challengers,
        HashSet<int> safe,
        Dictionary<(int Color, int Division), int> feederByColorDivision,
        Dictionary<int, int> sourceDivisionByLeague,
        LeagueEntity nextSuperleague,
        RulesV1 rules)
    {
        return ResolveNextLeagueTiered(
            source, promoted, relegated, incumbents, challengers, safe, feederSets: null,
            feederByColorDivision, sourceDivisionByLeague, nextSuperleague, rules);
    }

    internal static int? ResolveNextLeagueTiered(
        SeasonMembershipEntity source,
        HashSet<int> promoted,
        HashSet<int> relegated,
        HashSet<int> incumbents,
        HashSet<int> challengers,
        HashSet<int> safe,
        FeederSets? feederSets,
        Dictionary<(int Color, int Division), int> feederByColorDivision,
        Dictionary<int, int> sourceDivisionByLeague,
        LeagueEntity nextSuperleague,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(feederByColorDivision);
        ArgumentNullException.ThrowIfNull(sourceDivisionByLeague);
        ArgumentNullException.ThrowIfNull(nextSuperleague);
        ArgumentNullException.ThrowIfNull(rules);
        if (source.LeagueId is null)
        {
            return null;
        }

        int? superTarget = TryResolveSuperTarget(source, promoted, safe, incumbents, nextSuperleague);
        if (superTarget.HasValue)
        {
            return superTarget.Value;
        }

        int? f1Target = TryResolveRelegatedTarget(source, relegated, challengers, feederByColorDivision);
        if (f1Target.HasValue)
        {
            return f1Target.Value;
        }

        // MSS-058 feeder automatic movement: adjacent tiers only, one level per
        // postseason, same sporting color. Provisional qualifier markers remain
        // in their source tier; the qualifier decides the final adjacent move.
        if (feederSets is not null)
        {
            int? feederTarget = ResolveFeederTarget(source, feederSets, feederByColorDivision);
            if (feederTarget.HasValue)
            {
                return feederTarget.Value;
            }
        }

        return ResolveRetainedTarget(source, feederSets, feederByColorDivision, sourceDivisionByLeague);
    }

    private static int? TryResolveSuperTarget(
        SeasonMembershipEntity source,
        HashSet<int> promoted,
        HashSet<int> safe,
        HashSet<int> incumbents,
        LeagueEntity nextSuperleague)
    {
        if (promoted.Contains(source.SaveAthleteId)
            || safe.Contains(source.SaveAthleteId)
            || incumbents.Contains(source.SaveAthleteId))
        {
            return nextSuperleague.Id;
        }

        return null;
    }

    private static int? TryResolveRelegatedTarget(
        SeasonMembershipEntity source,
        HashSet<int> relegated,
        HashSet<int> challengers,
        Dictionary<(int Color, int Division), int> feederByColorDivision)
    {
        // Relegated Superleague athletes return to F1 (never directly to F2/F3).
        if (relegated.Contains(source.SaveAthleteId) || challengers.Contains(source.SaveAthleteId))
        {
            if (!feederByColorDivision.TryGetValue(
                (source.SportingColor, (int)SimulationKernel.Leagues.FeederDivision.First), out int f1Id))
            {
                throw new InvalidOperationException($"Unknown sporting color {source.SportingColor} F1.");
            }

            return f1Id;
        }

        return null;
    }

    private static int ResolveRetainedTarget(
        SeasonMembershipEntity source,
        FeederSets? feederSets,
        Dictionary<(int Color, int Division), int> feederByColorDivision,
        Dictionary<int, int> sourceDivisionByLeague)
    {
        // Retained F1 stays F1; retained F3 stays F3. F2 has no retained band:
        // every F2 rank is in an automatic or qualifier band, so reaching here
        // from F2 means the source season is corrupt.
        if (!sourceDivisionByLeague.TryGetValue(source.LeagueId!.Value, out int sourceDivision))
        {
            throw new InvalidOperationException(
                $"Athlete {source.SaveAthleteId} source league {source.LeagueId.Value} is not a feeder; Superleague members must be in safe/incumbent/relegated bands.");
        }

        if (sourceDivision == (int)SimulationKernel.Leagues.FeederDivision.Second && feederSets is not null)
        {
            throw new InvalidOperationException(
                $"Athlete {source.SaveAthleteId} from F2 has no automatic movement band; F2 ranks must be 1-8 auto-up, 9-16 challenger, 17-24 incumbent, 25-32 auto-down.");
        }

        if (sourceDivision != (int)SimulationKernel.Leagues.FeederDivision.First &&
            sourceDivision != (int)SimulationKernel.Leagues.FeederDivision.Second &&
            sourceDivision != (int)SimulationKernel.Leagues.FeederDivision.Third)
        {
            throw new InvalidOperationException($"Athlete {source.SaveAthleteId} has corrupt source division {sourceDivision}.");
        }

        if (!feederByColorDivision.TryGetValue((source.SportingColor, sourceDivision), out int retainedId))
        {
            throw new InvalidOperationException($"Unknown sporting color {source.SportingColor} division {sourceDivision}.");
        }

        return retainedId;
    }

    internal static int? ResolveFeederTarget(
        SeasonMembershipEntity source,
        FeederSets feederSets,
        Dictionary<(int Color, int Division), int> feederByColorDivision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(feederSets);
        ArgumentNullException.ThrowIfNull(feederByColorDivision);

        // Automatic promotions: F2 1-8 → F1, F3 1-8 → F2.
        if (feederSets.F2Promoted.Contains(source.SaveAthleteId))
        {
            return RequireFeederLeague(feederByColorDivision, source.SportingColor, SimulationKernel.Leagues.FeederDivision.First);
        }

        if (feederSets.F3Promoted.Contains(source.SaveAthleteId))
        {
            return RequireFeederLeague(feederByColorDivision, source.SportingColor, SimulationKernel.Leagues.FeederDivision.Second);
        }

        // Automatic relegations: F1 25-32 → F2, F2 25-32 → F3.
        if (feederSets.F1Relegated.Contains(source.SaveAthleteId))
        {
            return RequireFeederLeague(feederByColorDivision, source.SportingColor, SimulationKernel.Leagues.FeederDivision.Second);
        }

        if (feederSets.F2Relegated.Contains(source.SaveAthleteId))
        {
            return RequireFeederLeague(feederByColorDivision, source.SportingColor, SimulationKernel.Leagues.FeederDivision.Third);
        }

        // Provisional qualifier markers remain in source tier.
        if (feederSets.F1Incumbents.Contains(source.SaveAthleteId))
        {
            return RequireFeederLeague(feederByColorDivision, source.SportingColor, SimulationKernel.Leagues.FeederDivision.First);
        }

        if (feederSets.F2Challengers.Contains(source.SaveAthleteId)
            || feederSets.F2Incumbents.Contains(source.SaveAthleteId))
        {
            return RequireFeederLeague(feederByColorDivision, source.SportingColor, SimulationKernel.Leagues.FeederDivision.Second);
        }

        if (feederSets.F3Challengers.Contains(source.SaveAthleteId))
        {
            return RequireFeederLeague(feederByColorDivision, source.SportingColor, SimulationKernel.Leagues.FeederDivision.Third);
        }

        return null;
    }

    internal static int RequireFeederLeague(
        Dictionary<(int Color, int Division), int> feederByColorDivision,
        int sportingColor,
        SimulationKernel.Leagues.FeederDivision division)
    {
        if (!feederByColorDivision.TryGetValue((sportingColor, (int)division), out int leagueId))
        {
            throw new InvalidOperationException($"Unknown sporting color {sportingColor} division {division}.");
        }

        return leagueId;
    }

    internal static int? ResolveNextLeague(
        SeasonMembershipEntity source,
        HashSet<int> promoted,
        HashSet<int> relegated,
        HashSet<int> incumbents,
        HashSet<int> challengers,
        HashSet<int> safe,
        Dictionary<int, int> feederByColor,
        LeagueEntity nextSuperleague)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(promoted);
        ArgumentNullException.ThrowIfNull(relegated);
        ArgumentNullException.ThrowIfNull(incumbents);
        ArgumentNullException.ThrowIfNull(challengers);
        ArgumentNullException.ThrowIfNull(safe);
        ArgumentNullException.ThrowIfNull(feederByColor);
        ArgumentNullException.ThrowIfNull(nextSuperleague);

        if (source.LeagueId is null)
        {
            return null;
        }

        if (promoted.Contains(source.SaveAthleteId)
            || safe.Contains(source.SaveAthleteId)
            || incumbents.Contains(source.SaveAthleteId))
        {
            return nextSuperleague.Id;
        }

        if (relegated.Contains(source.SaveAthleteId) || challengers.Contains(source.SaveAthleteId))
        {
            if (!feederByColor.TryGetValue(source.SportingColor, out int feederId))
            {
                throw new InvalidOperationException($"Unknown sporting color {source.SportingColor}.");
            }

            return feederId;
        }

        if (!feederByColor.TryGetValue(source.SportingColor, out int retainedId))
        {
            throw new InvalidOperationException($"Unknown sporting color {source.SportingColor}.");
        }

        return retainedId;
    }

    internal static List<MovementEntity> BuildMovements(
        AutomaticMovementSelection.AutomaticPlan plan,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete,
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague)
    {
        return BuildMovements(plan, feederPlan: null, membershipByAthlete, source, next, nextFeeders, nextSuperleague, RulesV1.CreateDefault());
    }

    internal static List<MovementEntity> BuildMovements(
        AutomaticMovementSelection.AutomaticPlan plan,
        FeederMovementSelection.FeederPlan? feederPlan,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete,
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague,
        RulesV1 rules)
    {
        // Tiered saves carry 24 feeders; relegated/challengers always return to
        // F1 (never directly to F2/F3), so resolve via F1-only map when tiered.
        // Feeder autos need source-division context; ResolutionInputs carries
        // source feeders, but this overload lacks them for legacy callers.
        // Legacy v1 callers pass feederPlan null, so no source context needed.
        return BuildMovementsWithSource(
            plan, feederPlan, membershipByAthlete, source, next, nextFeeders, nextSuperleague,
            sourceFeeders: null, rules);
    }

    internal static List<MovementEntity> BuildMovementsWithSource(
        AutomaticMovementSelection.AutomaticPlan plan,
        FeederMovementSelection.FeederPlan? feederPlan,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete,
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague,
        IReadOnlyList<LeagueEntity>? sourceFeeders,
        RulesV1 rules)
    {
        // Tiered saves carry 24 feeders; relegated/challengers always return to
        // F1 (never directly to F2/F3), so resolve via F1-only map when tiered.
        // Feeder autos need source-division context; ResolutionInputs carries
        // source feeders, but this overload lacks them for legacy callers.
        // Legacy v1 callers pass feederPlan null, so no source context needed.
        Dictionary<int, int> feederByColor = BuildFeederByColor(nextFeeders);
        Dictionary<(int Color, int Division), int> nextByColorDivision = BuildNextByColorDivision(nextFeeders);
        Dictionary<int, int> sourceDivisionByLeague = BuildSourceDivisionByLeague(sourceFeeders);

        List<MovementEntity> movements = new(plan.All.Count + (feederPlan?.All.Count ?? 0));
        AppendSuperleagueMovements(movements, plan, membershipByAthlete, source, next, nextSuperleague, feederByColor);
        AppendFeederMovements(movements, feederPlan, membershipByAthlete, source, next, nextByColorDivision, sourceDivisionByLeague);

        _ = rules;
        return movements
            .OrderBy(m => m.Kind)
            .ThenBy(m => m.FromLeagueId)
            .ThenBy(m => m.FromSeasonRank)
            .ToList();
    }

    private static Dictionary<int, int> BuildFeederByColor(List<LeagueEntity> nextFeeders)
    {
        if (nextFeeders.Count == 8)
        {
            return nextFeeders.ToDictionary(l => l.SportingColor, l => l.Id);
        }

        return nextFeeders
            .Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First)
            .ToDictionary(l => l.SportingColor, l => l.Id);
    }

    private static Dictionary<(int Color, int Division), int> BuildNextByColorDivision(List<LeagueEntity> nextFeeders)
    {
        return nextFeeders.Count == 24
            ? nextFeeders.ToDictionary(l => (l.SportingColor, l.FeederDivision), l => l.Id)
            : new Dictionary<(int, int), int>();
    }

    private static Dictionary<int, int> BuildSourceDivisionByLeague(IReadOnlyList<LeagueEntity>? sourceFeeders)
    {
        return sourceFeeders is not null
            ? sourceFeeders.ToDictionary(l => l.Id, l => l.FeederDivision)
            : new Dictionary<int, int>();
    }

    private static void AppendSuperleagueMovements(
        List<MovementEntity> movements,
        AutomaticMovementSelection.AutomaticPlan plan,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete,
        SeasonEntity source,
        SeasonEntity next,
        LeagueEntity nextSuperleague,
        Dictionary<int, int> feederByColor)
    {
        foreach (AutomaticMovementSelection.AutomaticPick pick in plan.All)
        {
            if (!membershipByAthlete.TryGetValue(pick.SaveAthleteId, out SeasonMembershipEntity? origin))
            {
                throw new InvalidOperationException($"Movement athlete {pick.SaveAthleteId} has no source membership.");
            }

            int toLeague = ResolveSuperMovementTarget(pick, origin, nextSuperleague, feederByColor);
            movements.Add(new MovementEntity
            {
                SaveAthleteId = pick.SaveAthleteId,
                FromSeasonId = source.Id,
                ToSeasonId = next.Id,
                FromLeagueId = pick.FromLeagueId,
                ToLeagueId = toLeague,
                Kind = (int)pick.Kind,
                FromSeasonRank = pick.FromSeasonRank,
                SportingColor = origin.SportingColor,
            });
        }
    }

    private static int ResolveSuperMovementTarget(
        AutomaticMovementSelection.AutomaticPick pick,
        SeasonMembershipEntity origin,
        LeagueEntity nextSuperleague,
        Dictionary<int, int> feederByColor)
    {
        return pick.Kind switch
        {
            MovementKind.AutomaticPromotion => nextSuperleague.Id,
            MovementKind.QualifierIncumbent => nextSuperleague.Id,
            MovementKind.AutomaticRelegation => feederByColor.TryGetValue(origin.SportingColor, out int relegatedFeeder)
                ? relegatedFeeder
                : throw new InvalidOperationException($"Unknown sporting color {origin.SportingColor}."),
            MovementKind.QualifierChallenger => feederByColor.TryGetValue(origin.SportingColor, out int challengerFeeder)
                ? challengerFeeder
                : throw new InvalidOperationException($"Unknown sporting color {origin.SportingColor}."),
            _ => throw new InvalidOperationException($"Unexpected movement kind {pick.Kind}."),
        };
    }

    private static void AppendFeederMovements(
        List<MovementEntity> movements,
        FeederMovementSelection.FeederPlan? feederPlan,
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete,
        SeasonEntity source,
        SeasonEntity next,
        Dictionary<(int Color, int Division), int> nextByColorDivision,
        Dictionary<int, int> sourceDivisionByLeague)
    {
        if (feederPlan is null)
        {
            return;
        }

        foreach (FeederMovementSelection.FeederPick pick in feederPlan.All)
        {
            if (!membershipByAthlete.TryGetValue(pick.SaveAthleteId, out SeasonMembershipEntity? origin))
            {
                throw new InvalidOperationException($"Feeder movement athlete {pick.SaveAthleteId} has no source membership.");
            }

            int toLeague = ResolveFeederMovementTarget(pick, nextByColorDivision, sourceDivisionByLeague);
            movements.Add(new MovementEntity
            {
                SaveAthleteId = pick.SaveAthleteId,
                FromSeasonId = source.Id,
                ToSeasonId = next.Id,
                FromLeagueId = pick.FromLeagueId,
                ToLeagueId = toLeague,
                Kind = (int)pick.Kind,
                FromSeasonRank = pick.FromSeasonRank,
                SportingColor = origin.SportingColor,
            });
        }
    }

    internal static int ResolveFeederMovementTarget(
        FeederMovementSelection.FeederPick pick,
        Dictionary<(int Color, int Division), int> nextByColorDivision,
        Dictionary<int, int> sourceDivisionByLeague)
    {
        if (!sourceDivisionByLeague.TryGetValue(pick.FromLeagueId, out int sourceDivision))
        {
            throw new InvalidOperationException(
                $"Feeder pick {pick.SaveAthleteId} references unknown source league {pick.FromLeagueId}.");
        }

        int targetDivision = pick.Kind switch
        {
            MovementKind.FeederAutomaticPromotion => sourceDivision switch
            {
                (int)SimulationKernel.Leagues.FeederDivision.Second => (int)SimulationKernel.Leagues.FeederDivision.First,
                (int)SimulationKernel.Leagues.FeederDivision.Third => (int)SimulationKernel.Leagues.FeederDivision.Second,
                _ => throw new InvalidOperationException(
                    $"Feeder promotion {pick.SaveAthleteId} has corrupt source division {sourceDivision}."),
            },
            MovementKind.FeederAutomaticRelegation => sourceDivision switch
            {
                (int)SimulationKernel.Leagues.FeederDivision.First => (int)SimulationKernel.Leagues.FeederDivision.Second,
                (int)SimulationKernel.Leagues.FeederDivision.Second => (int)SimulationKernel.Leagues.FeederDivision.Third,
                _ => throw new InvalidOperationException(
                    $"Feeder relegation {pick.SaveAthleteId} has corrupt source division {sourceDivision}."),
            },
            MovementKind.FeederQualifierIncumbent => sourceDivision,
            MovementKind.FeederQualifierChallenger => sourceDivision,
            _ => throw new InvalidOperationException($"Unexpected feeder movement kind {pick.Kind}."),
        };

        if (!nextByColorDivision.TryGetValue((pick.SportingColor, targetDivision), out int toLeague))
        {
            throw new InvalidOperationException(
                $"Unknown sporting color {pick.SportingColor} division {targetDivision} for feeder movement.");
        }

        return toLeague;
    }

    internal static async Task<ResolveAutomaticMovementResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        LeagueEntity nextSuperleague,
        AutomaticMovementSelection.AutomaticPlan plan,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        Dictionary<int, string> names = await LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string?> images = await LoadImagesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await LoadLeaguesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> nextByAthlete = await LoadNextMembershipsAsync(context, next, cancellationToken).ConfigureAwait(false);
        LeagueEntity sourceSuperleague = await context.Leagues
            .AsNoTracking()
            .SingleAsync(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken)
            .ConfigureAwait(false);
        List<SeasonStandingEntity> sourceSuperRows = await LoadSourceSuperRowsAsync(context, source, sourceSuperleague, cancellationToken).ConfigureAwait(false);
        int pool = await LoadPoolCountAsync(context, next, cancellationToken).ConfigureAwait(false);
        int movements = await LoadSuperleagueMovementCountAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        return MapResponse(saveId, source, next, nextSuperleague, plan, names, images, leaguesById, nextByAthlete, sourceSuperleague, sourceSuperRows, pool, movements);
    }

    private static async Task<Dictionary<int, string>> LoadNamesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, string?>> LoadImagesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.ImageUrl, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, LeagueEntity>> LoadLeaguesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, SeasonMembershipEntity>> LoadNextMembershipsAsync(
        SaveDbContext context,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        return await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.LeagueId != null)
            .ToDictionaryAsync(e => e.SaveAthleteId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<List<SeasonStandingEntity>> LoadSourceSuperRowsAsync(
        SaveDbContext context,
        SeasonEntity source,
        LeagueEntity sourceSuperleague,
        CancellationToken cancellationToken)
    {
        return await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == sourceSuperleague.Id)
            .OrderBy(e => e.SeasonRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> LoadPoolCountAsync(
        SaveDbContext context,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        return await context.SeasonMemberships
            .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> LoadSuperleagueMovementCountAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        // Superleague response stays scoped to Superleague kinds (48) for backward
        // compatibility; feeder movements (MSS-058) use distinct kinds and are
        // exposed via feeder APIs with tier provenance.
        return await context.Movements
            .CountAsync(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.AutomaticPromotion
                    || e.Kind == (int)MovementKind.AutomaticRelegation
                    || e.Kind == (int)MovementKind.QualifierIncumbent
                    || e.Kind == (int)MovementKind.QualifierChallenger),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ResolveAutomaticMovementResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        LeagueEntity nextSuperleague,
        AutomaticMovementSelection.AutomaticPlan plan,
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        LeagueEntity sourceSuperleague,
        List<SeasonStandingEntity> sourceSuperRows,
        int pool,
        int movements)
    {
        List<AutomaticMovementMember> safe = MapSafe(
            names, images, leaguesById, nextByAthlete, sourceSuperleague, nextSuperleague, sourceSuperRows);
        List<AutomaticMovementMember> promoted = MapPicks(names, images, leaguesById, nextByAthlete, plan.Promotions);
        List<AutomaticMovementMember> relegated = MapPicks(names, images, leaguesById, nextByAthlete, plan.Relegations);
        List<AutomaticMovementMember> incumbents = MapPicks(names, images, leaguesById, nextByAthlete, plan.QualifierIncumbents);
        List<AutomaticMovementMember> challengers = MapPicks(names, images, leaguesById, nextByAthlete, plan.QualifierChallengers);
        return new ResolveAutomaticMovementResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            nextSuperleague.Id,
            nextSuperleague.Name,
            safe,
            promoted,
            relegated,
            incumbents,
            challengers,
            pool,
            movements);
    }

    private static List<AutomaticMovementMember> MapSafe(
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        LeagueEntity sourceSuperleague,
        LeagueEntity nextSuperleague,
        List<SeasonStandingEntity> sourceSuperRows)
    {
        _ = leaguesById;
        string level = LeagueEntityLevels.GetLevel(sourceSuperleague).ToString();
        List<AutomaticMovementMember> members = new(16);
        foreach (SeasonStandingEntity row in sourceSuperRows.Where(r => r.SeasonRank >= 1 && r.SeasonRank <= 16).OrderBy(r => r.SeasonRank))
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            images.TryGetValue(row.SaveAthleteId, out string? imageUrl);
            nextByAthlete.TryGetValue(row.SaveAthleteId, out SeasonMembershipEntity? nextMembership);
            string color = nextMembership is null ? "Unknown" : ((SportingColor)nextMembership.SportingColor).ToString();
            members.Add(new AutomaticMovementMember(
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}",
                color,
                sourceSuperleague.Id,
                sourceSuperleague.Name,
                row.SeasonRank,
                nextSuperleague.Id,
                nextSuperleague.Name,
                "Safe",
                imageUrl,
                level,
                level));
        }

        return members;
    }

    private static List<AutomaticMovementMember> MapPicks(
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        IReadOnlyList<AutomaticMovementSelection.AutomaticPick> picks)
    {
        List<AutomaticMovementMember> members = new(picks.Count);
        foreach (AutomaticMovementSelection.AutomaticPick pick in picks.OrderBy(p => p.FromLeagueId).ThenBy(p => p.FromSeasonRank))
        {
            names.TryGetValue(pick.SaveAthleteId, out string? name);
            images.TryGetValue(pick.SaveAthleteId, out string? imageUrl);
            leaguesById.TryGetValue(pick.FromLeagueId, out LeagueEntity? from);
            nextByAthlete.TryGetValue(pick.SaveAthleteId, out SeasonMembershipEntity? nextMembership);
            int toLeague = nextMembership?.LeagueId ?? 0;
            string toName = nextMembership?.LeagueId is not null && leaguesById.TryGetValue(nextMembership.LeagueId.Value, out LeagueEntity? to)
                ? to.Name
                : string.Empty;
            string? toLevel = nextMembership?.LeagueId is not null && leaguesById.TryGetValue(nextMembership.LeagueId.Value, out LeagueEntity? toLeagueEntity)
                ? LeagueEntityLevels.GetLevel(toLeagueEntity).ToString()
                : null;
            string color = nextMembership is null ? "Unknown" : ((SportingColor)nextMembership.SportingColor).ToString();
            members.Add(new AutomaticMovementMember(
                pick.SaveAthleteId,
                name ?? $"Athlete {pick.SaveAthleteId}",
                color,
                pick.FromLeagueId,
                from?.Name ?? $"League {pick.FromLeagueId}",
                pick.FromSeasonRank,
                toLeague,
                toName,
                pick.Kind.ToString(),
                imageUrl,
                from is null ? null : LeagueEntityLevels.GetLevel(from).ToString(),
                toLevel));
        }

        return members;
    }
}
