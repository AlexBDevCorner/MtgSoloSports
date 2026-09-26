using Microsoft.EntityFrameworkCore;
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
        await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        ResolutionInputs inputs = await LoadResolutionInputsAsync(context, rules, cancellationToken).ConfigureAwait(false);
        ResolutionOutput output = await PersistResolutionAsync(context, inputs, rules, cancellationToken).ConfigureAwait(false);

        await Features.Athletes.Projections.AthleteProjectionUpdater.RebuildAllAsync(context, rules, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, output.Source, output.Next, output.NextSuperleague, output.Plan, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record ResolutionInputs(
        SeasonEntity Source,
        List<LeagueEntity> SourceFeeders,
        LeagueEntity SourceSuperleague,
        List<SeasonStandingEntity> SuperStandings,
        Dictionary<int, IReadOnlyList<SeasonStandingEntity>> FeederStandings,
        List<SeasonMembershipEntity> SourceMemberships,
        AutomaticMovementSelection.AutomaticPlan Plan,
        ulong RngBefore,
        int StageCountBefore,
        int SeasonCountBefore);

    internal sealed record ResolutionOutput(
        SeasonEntity Source,
        SeasonEntity Next,
        List<LeagueEntity> NextFeeders,
        LeagueEntity NextSuperleague,
        AutomaticMovementSelection.AutomaticPlan Plan);

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
        return new ResolutionInputs(source, feeders, superleague, super, feederMap, memberships, plan, rngBefore, stages, seasons);
    }

    internal static async Task<ResolutionOutput> PersistResolutionAsync(
        SaveDbContext context, ResolutionInputs inputs, RulesV1 rules, CancellationToken cancellationToken)
    {
        SeasonEntity next = await CreateNextSeasonAsync(context, inputs.Source, cancellationToken).ConfigureAwait(false);
        List<LeagueEntity> nextFeeders = await CreateNextFeedersAsync(context, next, cancellationToken).ConfigureAwait(false);
        LeagueEntity nextSuperleague = await CreateNextSuperleagueAsync(context, next, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> byAthlete = inputs.SourceMemberships.ToDictionary(m => m.SaveAthleteId);
        List<SeasonMembershipEntity> nextMemberships = BuildNextMemberships(
            inputs.SourceMemberships, byAthlete, inputs.Plan, inputs.SuperStandings, inputs.FeederStandings, next, nextFeeders, nextSuperleague, rules);
        foreach (SeasonMembershipEntity membership in nextMemberships)
        {
            context.SeasonMemberships.Add(membership);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        List<MovementEntity> movements = BuildMovements(inputs.Plan, byAthlete, inputs.Source, next, nextFeeders, nextSuperleague);
        foreach (MovementEntity movement in movements)
        {
            context.Movements.Add(movement);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await ValidatePersistedAsync(context, inputs, next, nextFeeders, nextSuperleague, rules, cancellationToken).ConfigureAwait(false);
        return new ResolutionOutput(inputs.Source, next, nextFeeders, nextSuperleague, inputs.Plan);
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
        AutomaticMovementInvariants.ValidateCreated(
            inputs.Source, next, nextSuperleague, nextFeeders, inputs.SourceMemberships, persistedNext, persistedMovements, rules);
        await VerifyPreservationAsync(context, inputs.RngBefore, inputs.StageCountBefore, inputs.SeasonCountBefore, cancellationToken).ConfigureAwait(false);
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
        if (leagues.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season {source.SeasonNumber} must have exactly {rules.RegularLeagueCount} feeder leagues, was {leagues.Count}.");
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
        List<LeagueEntity> leagues = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            leagues.Add(new LeagueEntity
            {
                SeasonId = next.Id,
                SportingColor = (int)color,
                Kind = (int)LeagueKind.Feeder,
                Name = $"{color} League",
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

        Dictionary<int, int> feederByColor = nextFeeders.ToDictionary(l => l.SportingColor, l => l.Id);
        List<SeasonMembershipEntity> nextMemberships = new(sourceMemberships.Count);
        foreach (SeasonMembershipEntity source in sourceMemberships.OrderBy(m => m.SaveAthleteId))
        {
            int? leagueId = ResolveNextLeague(source, promoted, relegated, incumbents, challengers, safe, feederByColor, nextSuperleague);
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
        Dictionary<int, int> feederByColor = nextFeeders.ToDictionary(l => l.SportingColor, l => l.Id);
        List<MovementEntity> movements = new(plan.All.Count);
        foreach (AutomaticMovementSelection.AutomaticPick pick in plan.All)
        {
            if (!membershipByAthlete.TryGetValue(pick.SaveAthleteId, out SeasonMembershipEntity? origin))
            {
                throw new InvalidOperationException($"Movement athlete {pick.SaveAthleteId} has no source membership.");
            }

            int toLeague = pick.Kind switch
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

        return movements
            .OrderBy(m => m.Kind)
            .ThenBy(m => m.FromLeagueId)
            .ThenBy(m => m.FromSeasonRank)
            .ToList();
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
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> nextByAthlete = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.LeagueId != null)
            .ToDictionaryAsync(e => e.SaveAthleteId, cancellationToken)
            .ConfigureAwait(false);

        LeagueEntity sourceSuperleague = await context.Leagues
            .AsNoTracking()
            .SingleAsync(e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken)
            .ConfigureAwait(false);
        List<SeasonStandingEntity> sourceSuperRows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == sourceSuperleague.Id)
            .OrderBy(e => e.SeasonRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<AutomaticMovementMember> safe = MapSafe(
            names, leaguesById, nextByAthlete, sourceSuperleague, nextSuperleague, sourceSuperRows);
        List<AutomaticMovementMember> promoted = MapPicks(names, leaguesById, nextByAthlete, plan.Promotions);
        List<AutomaticMovementMember> relegated = MapPicks(names, leaguesById, nextByAthlete, plan.Relegations);
        List<AutomaticMovementMember> incumbents = MapPicks(names, leaguesById, nextByAthlete, plan.QualifierIncumbents);
        List<AutomaticMovementMember> challengers = MapPicks(names, leaguesById, nextByAthlete, plan.QualifierChallengers);
        int pool = await context.SeasonMemberships
            .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == null, cancellationToken)
            .ConfigureAwait(false);
        int movements = await context.Movements
            .CountAsync(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken)
            .ConfigureAwait(false);

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
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        LeagueEntity sourceSuperleague,
        LeagueEntity nextSuperleague,
        List<SeasonStandingEntity> sourceSuperRows)
    {
        _ = leaguesById;
        List<AutomaticMovementMember> members = new(16);
        foreach (SeasonStandingEntity row in sourceSuperRows.Where(r => r.SeasonRank >= 1 && r.SeasonRank <= 16).OrderBy(r => r.SeasonRank))
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
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
                "Safe"));
        }

        return members;
    }

    private static List<AutomaticMovementMember> MapPicks(
        Dictionary<int, string> names,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        IReadOnlyList<AutomaticMovementSelection.AutomaticPick> picks)
    {
        List<AutomaticMovementMember> members = new(picks.Count);
        foreach (AutomaticMovementSelection.AutomaticPick pick in picks.OrderBy(p => p.FromLeagueId).ThenBy(p => p.FromSeasonRank))
        {
            names.TryGetValue(pick.SaveAthleteId, out string? name);
            leaguesById.TryGetValue(pick.FromLeagueId, out LeagueEntity? from);
            nextByAthlete.TryGetValue(pick.SaveAthleteId, out SeasonMembershipEntity? nextMembership);
            int toLeague = nextMembership?.LeagueId ?? 0;
            string toName = nextMembership?.LeagueId is not null && leaguesById.TryGetValue(nextMembership.LeagueId.Value, out LeagueEntity? to)
                ? to.Name
                : string.Empty;
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
                pick.Kind.ToString()));
        }

        return members;
    }
}
