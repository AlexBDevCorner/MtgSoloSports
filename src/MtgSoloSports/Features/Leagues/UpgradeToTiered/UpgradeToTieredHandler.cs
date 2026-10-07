using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Leagues.UpgradeToTiered;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Explicit sporting-rule
/// upgrade from v1 (single feeder tier) to v3 (F1/F2/F3 with tiered prestige)
/// at a safe season boundary. Never hidden inside EF schema migration; persists
/// the new rules snapshot, new F2/F3 league rows, membership updates, RNG-after
/// state and 512 upgrade-seed movement rows atomically. Historical Season/Stage/Round/
/// Standing/Bonus/Cup/Movement rows are never rewritten and historical v1
/// feeders are never reinterpreted. Existing Superleague and F1 rosters stay
/// authoritative; F2/F3 are seeded from the target season's pool with the
/// versioned RNG (equal-probability, no bonus weighting, no F1 demotion).
/// Retry/reload is idempotent and RNG-safe: an already-upgraded target
/// returns the persisted result without consuming RNG twice. Saves not at a
/// safe boundary stay on v1 via conflict, never partially upgraded.
/// Already-tiered v2 saves stay on v2 with their original prestige model and
/// never silently adopt v3 prestige.
/// </summary>
public sealed class UpgradeToTieredHandler
{
    private readonly SaveStore _store;

    public UpgradeToTieredHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<UpgradeToTieredResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await UpgradeUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<UpgradeToTieredResponse> UpgradeUnderLockAsync(
        Guid saveId, CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = rngRow.ToState();

        (SeasonEntity source, SeasonEntity? next) = await LoadSeasonsAsync(context, metadata, cancellationToken).ConfigureAwait(false);
        if (next is null)
        {
            throw new UpgradeToTieredConflictException(
                $"Save is not at a safe tier-upgrade boundary: season {source.SeasonNumber} has no pending next season. Complete the season and resolve movement/rebalance/Cup to the CupComplete boundary first; the save stays on rules v{rules.Version}.");
        }

        if (rules.Version == RulesV2.RulesVersion || rules.Version == RulesV3.RulesVersion)
        {
            return await HandleAlreadyTieredAsync(context, saveId, source, next, rules, rngBefore, cancellationToken).ConfigureAwait(false);
        }

        if (rules.Version != RulesV1.RulesVersion)
        {
            throw new InvalidOperationException($"Unsupported rules version {rules.Version}.");
        }

        await EnsureSafeBoundaryAsync(context, source, next, rules, cancellationToken).ConfigureAwait(false);

        (int stagesBefore, int seasonsBefore, int roundsBefore, int qualifierRoundsBefore, int qualifierStandingsBefore, int movementsBefore) =
            await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);
        HashSet<int> f1Before = await LoadF1AthletesAsync(context, next, cancellationToken).ConfigureAwait(false);

        List<LeagueEntity> nextFeeders = await LoadNextFeedersAsync(context, next, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> nextMemberships = await LoadMembershipsAsync(context, next, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> names = await LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);

        List<UpgradeToTieredSelection.ColorInput> inputs = BuildColorInputs(nextMemberships, names, rules);
        HashSet<int> f1Ids = f1Before;
        Pcg32V1 rng = Pcg32V1.Restore(rngBefore);
        UpgradeToTieredSelection.UpgradePlan plan = UpgradeToTieredSelection.Select(inputs, rng, rules);
        UpgradeToTieredInvariants.ValidatePlan(plan, inputs, f1Ids, rules);
        Pcg32State rngAfter = rng.Snapshot();

        List<LeagueEntity> created = await CreateTierLeaguesAsync(context, next, cancellationToken).ConfigureAwait(false);
        ApplyPlan(context, nextMemberships, plan, created);
        await PersistUpgradeAsync(context, source, next, nextFeeders, created, plan, rngAfter, metadata, rules, cancellationToken).ConfigureAwait(false);

        await ValidatePersistedAsync(
            context, source, next, nextFeeders, created, nextMemberships, plan,
            rules, rngBefore, rngAfter, f1Before,
            stagesBefore, seasonsBefore, roundsBefore, qualifierRoundsBefore, qualifierStandingsBefore, movementsBefore,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(_store, saveId, source, next, plan, created, rules, rngBefore, rngAfter, alreadyApplied: false, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<(SeasonEntity Source, SeasonEntity? Next)> LoadSeasonsAsync(
        SaveDbContext context, SaveMetadataEntity metadata, CancellationToken cancellationToken)
    {
        SeasonEntity? source = await context.Seasons
            .SingleOrDefaultAsync(e => e.SeasonNumber == metadata.CurrentSeason, cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            throw new InvalidOperationException($"Save has no season {metadata.CurrentSeason}.");
        }

        SeasonEntity? next = await context.Seasons
            .SingleOrDefaultAsync(e => e.SeasonNumber == source.SeasonNumber + 1, cancellationToken)
            .ConfigureAwait(false);
        return (source, next);
    }

    internal static async Task<UpgradeToTieredResponse> HandleAlreadyTieredAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        Pcg32State rngBefore,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> feeders = await LoadNextFeedersAsync(context, next, cancellationToken).ConfigureAwait(false);
        if (feeders.Count != rules.TieredFeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Tiered save target season {next.SeasonNumber} must have exactly {rules.TieredFeederLeagueCount} feeders, was {feeders.Count}; partial tier creation fails loudly.");
        }

        List<MovementEntity> seeds = await context.Movements
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.TierUpgradeSeed)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (seeds.Count != rules.RegularLeagueCount * rules.LeagueSize * 2)
        {
            throw new InvalidOperationException(
                $"Tiered upgrade for {source.SeasonNumber}->{next.SeasonNumber} must hold exactly {rules.RegularLeagueCount * rules.LeagueSize * 2} seed rows, was {seeds.Count}.");
        }

        UpgradeToTieredSelection.UpgradePlan plan = await RebuildPlanFromPersistedAsync(
            context, next, feeders, seeds, cancellationToken).ConfigureAwait(false);
        Pcg32State current = (await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false)).ToState();
        return await BuildResponseAsync(
            context, saveId, source, next, plan, feeders, rules, current, current, alreadyApplied: true, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<UpgradeToTieredSelection.UpgradePlan> RebuildPlanFromPersistedAsync(
        SaveDbContext context,
        SeasonEntity next,
        List<LeagueEntity> feeders,
        List<MovementEntity> seeds,
        CancellationToken cancellationToken)
    {
        Dictionary<int, LeagueEntity> byLeague = feeders.ToDictionary(l => l.Id);
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, cancellationToken)
            .ConfigureAwait(false);

        List<UpgradeToTieredSelection.ColorPlan> perColor = new(8);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            perColor.Add(RebuildSingleColor(color, feeders, seeds, byLeague, names, memberships));
        }

        List<UpgradeToTieredSelection.PoolCandidate> f2 = perColor.SelectMany(p => p.F2).ToList();
        List<UpgradeToTieredSelection.PoolCandidate> f3 = perColor.SelectMany(p => p.F3).ToList();
        return new UpgradeToTieredSelection.UpgradePlan(perColor, f2, f3);
    }

    internal static UpgradeToTieredSelection.ColorPlan RebuildSingleColor(
        SportingColor color,
        List<LeagueEntity> feeders,
        List<MovementEntity> seeds,
        Dictionary<int, LeagueEntity> byLeague,
        Dictionary<int, string> names,
        Dictionary<int, SeasonMembershipEntity> memberships)
    {
        int f2Id = feeders.Single(l => l.SportingColor == (int)color && l.FeederDivision == (int)FeederDivision.Second).Id;
        int f3Id = feeders.Single(l => l.SportingColor == (int)color && l.FeederDivision == (int)FeederDivision.Third).Id;
        HashSet<int> f2Ids = seeds.Where(m => m.ToLeagueId == f2Id).Select(m => m.SaveAthleteId).ToHashSet();
        HashSet<int> f3Ids = seeds.Where(m => m.ToLeagueId == f3Id).Select(m => m.SaveAthleteId).ToHashSet();

        List<UpgradeToTieredSelection.PoolCandidate> f2 = MapSeeds(f2Ids, color, names, memberships);
        List<UpgradeToTieredSelection.PoolCandidate> f3 = MapSeeds(f3Ids, color, names, memberships);
        return new UpgradeToTieredSelection.ColorPlan(color, f2, f3);
    }

    internal static List<UpgradeToTieredSelection.PoolCandidate> MapSeeds(
        HashSet<int> ids,
        SportingColor color,
        Dictionary<int, string> names,
        Dictionary<int, SeasonMembershipEntity> memberships)
    {
        List<UpgradeToTieredSelection.PoolCandidate> list = new(ids.Count);
        foreach (int id in ids.OrderBy(i => i))
        {
            names.TryGetValue(id, out string? name);
            memberships.TryGetValue(id, out SeasonMembershipEntity? membership);
            list.Add(new UpgradeToTieredSelection.PoolCandidate(
                id, name ?? $"Athlete {id}", color, membership?.DrawIndex ?? 0));
        }

        return list;
    }

    internal static async Task EnsureSafeBoundaryAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        if (!source.IsComplete)
        {
            throw new UpgradeToTieredConflictException(
                $"Season {source.SeasonNumber} is still in progress; tier upgrade runs only at a safe season boundary (CupComplete ready to start season {next.SeasonNumber}). The save stays on rules v1.");
        }

        if (next.IsComplete || !next.HasSuperleague)
        {
            throw new UpgradeToTieredConflictException(
                $"Target season {next.SeasonNumber} is not a pending Superleague season; tier upgrade runs only before the next season starts. The save stays on rules v1.");
        }

        await EnsureNextIsV1ShapeAsync(context, next, cancellationToken).ConfigureAwait(false);
        await EnsureNextHasNoSimulationAsync(context, next, cancellationToken).ConfigureAwait(false);
        await EnsureCupCompleteAsync(context, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task EnsureNextIsV1ShapeAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        List<LeagueEntity> feeders = await context.Leagues
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (feeders.Count != 8)
        {
            throw new UpgradeToTieredConflictException(
                $"Target season {next.SeasonNumber} has {feeders.Count} feeders, not the v1 shape (8); tier upgrade runs only from a v1 pending season. The save stays on v1.");
        }

        foreach (LeagueEntity feeder in feeders)
        {
            int count = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == feeder.Id, cancellationToken)
                .ConfigureAwait(false);
            if (count != 32)
            {
                throw new UpgradeToTieredConflictException(
                    $"League '{feeder.Name}' holds {count} athletes, not 32; resolve feeder rebalancing before tier upgrade. The save stays on v1.");
            }
        }
    }

    internal static async Task EnsureNextHasNoSimulationAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        int stages = await context.Stages.CountAsync(e => e.SeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        int stageStandings = await context.StageStandings.CountAsync(e => e.SeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        int seasonStandings = await context.SeasonStandings.CountAsync(e => e.SeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(e => e.SeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        if (stages != 0 || stageStandings != 0 || seasonStandings != 0 || rounds != 0)
        {
            throw new UpgradeToTieredConflictException(
                $"Target season {next.SeasonNumber} already has simulation results; tier upgrade runs only before the next season starts. The save stays on v1.");
        }
    }

    internal static async Task EnsureCupCompleteAsync(
        SaveDbContext context, SeasonEntity source, CancellationToken cancellationToken)
    {
        string expected = CupExtensionPoint.ExpectedCupForSource(source.SeasonNumber);
        CupExtensionPoint.CupState state = await CupExtensionPoint
            .LoadCupStateAsync(context, source, expected, cancellationToken)
            .ConfigureAwait(false);
        if (!state.Complete)
        {
            throw new UpgradeToTieredConflictException(
                $"Post-season {expected} for season {source.SeasonNumber} is not complete (selection {state.SelectionResolved}, individual {state.IndividualResolved}, team {state.TeamResolved}); tier upgrade runs only at CupComplete. The save stays on v1.");
        }
    }

    internal static async Task<(int Stages, int Seasons, int Rounds, int QualifierRounds, int QualifierStandings, int Movements)> CapturePreservationAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierStandings = await context.QualifierStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int movements = await context.Movements.CountAsync(cancellationToken).ConfigureAwait(false);
        return (stages, seasons, rounds, qualifierRounds, qualifierStandings, movements);
    }

    internal static async Task<HashSet<int>> LoadF1AthletesAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        List<LeagueEntity> f1 = await context.Leagues
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        // v1 shape has 8 feeders; all are F1 (division First or legacy None).
        List<int> ids = await context.SeasonMemberships
            .Where(e => e.SeasonId == next.Id && e.LeagueId != null && f1.Select(l => l.Id).Contains(e.LeagueId.Value))
            .Select(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ids.ToHashSet();
    }

    internal static async Task<List<LeagueEntity>> LoadNextFeedersAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        return await context.Leagues
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
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

    internal static List<UpgradeToTieredSelection.ColorInput> BuildColorInputs(
        List<SeasonMembershipEntity> nextMemberships,
        Dictionary<int, string> names,
        RulesV1 rules)
    {
        _ = rules;
        Dictionary<int, List<SeasonMembershipEntity>> poolByColor = nextMemberships
            .Where(m => m.LeagueId is null)
            .GroupBy(m => m.SportingColor)
            .ToDictionary(g => g.Key, g => g.ToList());

        List<UpgradeToTieredSelection.ColorInput> inputs = new(8);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            List<SeasonMembershipEntity> pool = poolByColor.TryGetValue((int)color, out List<SeasonMembershipEntity>? members)
                ? members
                : [];
            List<UpgradeToTieredSelection.PoolCandidate> candidates = new(pool.Count);
            foreach (SeasonMembershipEntity membership in pool)
            {
                names.TryGetValue(membership.SaveAthleteId, out string? name);
                candidates.Add(new UpgradeToTieredSelection.PoolCandidate(
                    membership.SaveAthleteId, name ?? $"Athlete {membership.SaveAthleteId}", color, membership.DrawIndex));
            }

            inputs.Add(new UpgradeToTieredSelection.ColorInput(color, candidates));
        }

        return inputs;
    }

    internal static async Task<List<LeagueEntity>> CreateTierLeaguesAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        List<LeagueEntity> created = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            created.Add(new LeagueEntity
            {
                SeasonId = next.Id,
                SportingColor = (int)color,
                Kind = (int)LeagueKind.Feeder,
                FeederDivision = (int)FeederDivision.Second,
                Name = $"{color} League F2",
            });
            created.Add(new LeagueEntity
            {
                SeasonId = next.Id,
                SportingColor = (int)color,
                Kind = (int)LeagueKind.Feeder,
                FeederDivision = (int)FeederDivision.Third,
                Name = $"{color} League F3",
            });
        }

        foreach (LeagueEntity league in created)
        {
            context.Leagues.Add(league);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    internal static void ApplyPlan(
        SaveDbContext context,
        List<SeasonMembershipEntity> nextMemberships,
        UpgradeToTieredSelection.UpgradePlan plan,
        List<LeagueEntity> created)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nextMemberships);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(created);

        Dictionary<int, SeasonMembershipEntity> byAthlete = nextMemberships.ToDictionary(m => m.SaveAthleteId);
        Dictionary<(SportingColor Color, int Division), int> leagueByDivision = created.ToDictionary(
            l => ((SportingColor)l.SportingColor, l.FeederDivision), l => l.Id);

        foreach (UpgradeToTieredSelection.ColorPlan colorPlan in plan.PerColor)
        {
            ApplySingleColor(byAthlete, leagueByDivision, colorPlan);
        }
    }

    internal static void ApplySingleColor(
        Dictionary<int, SeasonMembershipEntity> byAthlete,
        Dictionary<(SportingColor Color, int Division), int> leagueByDivision,
        UpgradeToTieredSelection.ColorPlan colorPlan)
    {
        foreach (UpgradeToTieredSelection.PoolCandidate seed in colorPlan.F2)
        {
            AssignSeed(byAthlete, leagueByDivision, colorPlan.Color, FeederDivision.Second, seed);
        }

        foreach (UpgradeToTieredSelection.PoolCandidate seed in colorPlan.F3)
        {
            AssignSeed(byAthlete, leagueByDivision, colorPlan.Color, FeederDivision.Third, seed);
        }
    }

    internal static void AssignSeed(
        Dictionary<int, SeasonMembershipEntity> byAthlete,
        Dictionary<(SportingColor Color, int Division), int> leagueByDivision,
        SportingColor color,
        FeederDivision division,
        UpgradeToTieredSelection.PoolCandidate seed)
    {
        if (!byAthlete.TryGetValue(seed.SaveAthleteId, out SeasonMembershipEntity? membership))
        {
            throw new InvalidOperationException($"Seeded athlete {seed.SaveAthleteId} has no target-season membership.");
        }

        if (membership.LeagueId is not null)
        {
            throw new InvalidOperationException($"Seeded athlete {seed.SaveAthleteId} is not in the common pool.");
        }

        if (!leagueByDivision.TryGetValue((color, (int)division), out int leagueId))
        {
            throw new InvalidOperationException($"Missing {color} division {division} league for upgrade.");
        }

        membership.LeagueId = leagueId;
    }

    internal static async Task PersistUpgradeAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> existingFeeders,
        List<LeagueEntity> created,
        UpgradeToTieredSelection.UpgradePlan plan,
        Pcg32State rngAfter,
        SaveMetadataEntity metadata,
        RulesV1 oldRules,
        CancellationToken cancellationToken)
    {
        NormalizeF1Divisions(context, existingFeeders);
        List<MovementEntity> movements = BuildMovements(source, next, created, plan, context);
        foreach (MovementEntity movement in movements)
        {
            context.Movements.Add(movement);
        }

        RulesV3 upgraded = RulesV3.FromV1(oldRules);
        string json = RulesSnapshotCodec.Encode(upgraded);
        RulesSnapshotEntity snapshot = await context.RulesSnapshots.SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        snapshot.RulesVersion = upgraded.Version;
        snapshot.RulesJson = json;

        context.ApplyRngState(rngAfter);
        _ = metadata;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static void NormalizeF1Divisions(SaveDbContext context, List<LeagueEntity> existingFeeders)
    {
        foreach (LeagueEntity feeder in existingFeeders)
        {
            if (feeder.FeederDivision == (int)FeederDivision.None)
            {
                feeder.FeederDivision = (int)FeederDivision.First;
            }
        }
    }

    internal static List<MovementEntity> BuildMovements(
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> created,
        UpgradeToTieredSelection.UpgradePlan plan,
        SaveDbContext context)
    {
        Dictionary<int, int> colorByAthlete = context.SeasonMemberships.Local
            .Where(m => m.SeasonId == next.Id)
            .ToDictionary(m => m.SaveAthleteId, m => m.SportingColor);
        Dictionary<(SportingColor Color, int Division), int> leagueByDivision = created.ToDictionary(
            l => ((SportingColor)l.SportingColor, l.FeederDivision), l => l.Id);

        List<MovementEntity> movements = new(plan.AllF2.Count + plan.AllF3.Count);
        foreach (UpgradeToTieredSelection.ColorPlan colorPlan in plan.PerColor.OrderBy(p => p.Color))
        {
            AddSeedMovements(movements, source, next, colorPlan.F2, colorPlan.Color, FeederDivision.Second, leagueByDivision, colorByAthlete);
            AddSeedMovements(movements, source, next, colorPlan.F3, colorPlan.Color, FeederDivision.Third, leagueByDivision, colorByAthlete);
        }

        return movements.OrderBy(m => m.SaveAthleteId).ToList();
    }

    internal static void AddSeedMovements(
        List<MovementEntity> movements,
        SeasonEntity source,
        SeasonEntity next,
        IReadOnlyList<UpgradeToTieredSelection.PoolCandidate> seeds,
        SportingColor color,
        FeederDivision division,
        Dictionary<(SportingColor Color, int Division), int> leagueByDivision,
        Dictionary<int, int> colorByAthlete)
    {
        if (!leagueByDivision.TryGetValue((color, (int)division), out int leagueId))
        {
            throw new InvalidOperationException($"Missing {color} division {division} league for upgrade movements.");
        }

        foreach (UpgradeToTieredSelection.PoolCandidate seed in seeds.OrderBy(s => s.SaveAthleteId))
        {
            movements.Add(new MovementEntity
            {
                SaveAthleteId = seed.SaveAthleteId,
                FromSeasonId = source.Id,
                ToSeasonId = next.Id,
                FromLeagueId = 0,
                ToLeagueId = leagueId,
                Kind = (int)MovementKind.TierUpgradeSeed,
                FromSeasonRank = 0,
                SportingColor = colorByAthlete.TryGetValue(seed.SaveAthleteId, out int c) ? c : (int)color,
            });
        }
    }

    internal static async Task ValidatePersistedAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        List<LeagueEntity> existingFeeders,
        List<LeagueEntity> created,
        List<SeasonMembershipEntity> nextMemberships,
        UpgradeToTieredSelection.UpgradePlan plan,
        RulesV1 oldRules,
        Pcg32State rngBefore,
        Pcg32State rngAfter,
        HashSet<int> f1Before,
        int stagesBefore,
        int seasonsBefore,
        int roundsBefore,
        int qualifierRoundsBefore,
        int qualifierStandingsBefore,
        int movementsBefore,
        CancellationToken cancellationToken)
    {
        RulesV3 upgraded = RulesV3.FromV1(oldRules);
        List<LeagueEntity> allFeeders = [.. existingFeeders, .. created];
        List<SeasonMembershipEntity> persistedNext = await context.SeasonMemberships
            .Where(e => e.SeasonId == next.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        LeagueEntity superleague = await context.Leagues
            .SingleAsync(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Superleague, cancellationToken)
            .ConfigureAwait(false);
        List<MovementEntity> persistedSeeds = await context.Movements
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.TierUpgradeSeed)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        UpgradeToTieredInvariants.ValidateCreated(
            source, next, superleague, allFeeders, persistedNext, persistedSeeds, plan, upgraded);

        await EnsureF1PreservedAsync(context, next, f1Before, cancellationToken).ConfigureAwait(false);
        await EnsureHistoryPreservedAsync(
            context, stagesBefore, seasonsBefore, roundsBefore, qualifierRoundsBefore, qualifierStandingsBefore, movementsBefore,
            persistedSeeds.Count, cancellationToken).ConfigureAwait(false);
        EnsureRngPersisted(context, rngAfter);
        EnsureRngAdvanced(rngBefore, rngAfter, plan);
    }

    internal static async Task EnsureF1PreservedAsync(
        SaveDbContext context, SeasonEntity next, HashSet<int> f1Before, CancellationToken cancellationToken)
    {
        List<LeagueEntity> f1 = await context.Leagues
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder && e.FeederDivision == (int)FeederDivision.First)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        HashSet<int> f1After = (await context.SeasonMemberships
            .Where(e => e.SeasonId == next.Id && e.LeagueId != null && f1.Select(l => l.Id).Contains(e.LeagueId.Value))
            .Select(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();
        if (!f1Before.SetEquals(f1After))
        {
            throw new InvalidOperationException("Tier upgrade must preserve existing F1 rosters; F1 athletes were changed.");
        }
    }

    internal static async Task EnsureHistoryPreservedAsync(
        SaveDbContext context,
        int stagesBefore,
        int seasonsBefore,
        int roundsBefore,
        int qualifierRoundsBefore,
        int qualifierStandingsBefore,
        int movementsBefore,
        int newSeeds,
        CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierStandings = await context.QualifierStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int movements = await context.Movements.CountAsync(cancellationToken).ConfigureAwait(false);
        if (stages != stagesBefore || seasons != seasonsBefore || rounds != roundsBefore)
        {
            throw new InvalidOperationException("Tier upgrade must preserve league stage/season/round history.");
        }

        if (qualifierRounds != qualifierRoundsBefore || qualifierStandings != qualifierStandingsBefore)
        {
            throw new InvalidOperationException("Tier upgrade must preserve qualifier history.");
        }

        if (movements != movementsBefore + newSeeds)
        {
            throw new InvalidOperationException(
                $"Tier upgrade must add exactly {newSeeds} movement rows, was {movements - movementsBefore}.");
        }
    }

    internal static void EnsureRngPersisted(SaveDbContext context, Pcg32State rngAfter)
    {
        RngStateEntity tracked = context.RngStates.Local.SingleOrDefault(e => e.Id == 1)
            ?? throw new InvalidOperationException("RNG state row is not tracked.");
        Pcg32State persisted = new(unchecked((ulong)tracked.State), unchecked((ulong)tracked.Stream));
        if (persisted.State != rngAfter.State || persisted.Stream != rngAfter.Stream)
        {
            throw new InvalidOperationException("Persisted RNG state does not match the tier-upgrade draw chain.");
        }
    }

    internal static void EnsureRngAdvanced(Pcg32State before, Pcg32State after, UpgradeToTieredSelection.UpgradePlan plan)
    {
        bool advanced = after.State != before.State || after.Stream != before.Stream;
        if (plan.AllF2.Count > 0 && !advanced)
        {
            throw new InvalidOperationException("Tier-upgrade pool draws must advance the save RNG.");
        }
    }

    internal static string ComputeChecksum(UpgradeToTieredSelection.UpgradePlan plan)
    {
        List<string> lines = new(plan.AllF2.Count + plan.AllF3.Count);
        foreach (UpgradeToTieredSelection.ColorPlan colorPlan in plan.PerColor.OrderBy(p => p.Color))
        {
            foreach (UpgradeToTieredSelection.PoolCandidate seed in colorPlan.F2.OrderBy(s => s.DrawIndex))
            {
                lines.Add($"{(int)colorPlan.Color}:{(int)FeederDivision.Second}:{seed.DrawIndex}:{seed.Name}\n");
            }

            foreach (UpgradeToTieredSelection.PoolCandidate seed in colorPlan.F3.OrderBy(s => s.DrawIndex))
            {
                lines.Add($"{(int)colorPlan.Color}:{(int)FeederDivision.Third}:{seed.DrawIndex}:{seed.Name}\n");
            }
        }

        StringBuilder builder = new();
        foreach (string line in lines.OrderBy(l => l, StringComparer.Ordinal))
        {
            builder.Append(line);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    internal static async Task<UpgradeToTieredResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        UpgradeToTieredSelection.UpgradePlan plan,
        List<LeagueEntity> createdOrAll,
        RulesV1 oldRules,
        Pcg32State rngBefore,
        Pcg32State rngAfter,
        bool alreadyApplied,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await BuildResponseAsync(context, saveId, source, next, plan, createdOrAll, oldRules, rngBefore, rngAfter, alreadyApplied, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<UpgradeToTieredResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        UpgradeToTieredSelection.UpgradePlan plan,
        List<LeagueEntity> createdOrAll,
        RulesV1 oldRules,
        Pcg32State rngBefore,
        Pcg32State rngAfter,
        bool alreadyApplied,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> created = createdOrAll
            .Where(l => l.FeederDivision == (int)FeederDivision.Second || l.FeederDivision == (int)FeederDivision.Third)
            .OrderBy(l => l.SportingColor)
            .ThenBy(l => l.FeederDivision)
            .ToList();
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string?> images = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.ImageUrl, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, int> counts = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.LeagueId != null)
            .GroupBy(e => e.LeagueId!.Value)
            .ToDictionaryAsync(g => g.Key, g => g.Count(), cancellationToken)
            .ConfigureAwait(false);

        List<UpgradeTierLeague> leagues = MapCreatedLeagues(created, counts);
        List<UpgradeTierSeed> f2 = await MapSeedsAsync(context, next, plan.AllF2, FeederDivision.Second, names, images, cancellationToken).ConfigureAwait(false);
        List<UpgradeTierSeed> f3 = await MapSeedsAsync(context, next, plan.AllF3, FeederDivision.Third, names, images, cancellationToken).ConfigureAwait(false);
        List<UpgradePoolCount> pools = await MapPoolsAsync(context, next, cancellationToken).ConfigureAwait(false);
        int poolTotal = pools.Sum(p => p.Count);
        int movements = await context.Movements
            .CountAsync(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.TierUpgradeSeed, cancellationToken)
            .ConfigureAwait(false);
        string checksum = ComputeChecksum(plan);
        int targetVersion = alreadyApplied ? oldRules.Version : RulesV3.RulesVersion;

        return new UpgradeToTieredResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            oldRules.Version == RulesV2.RulesVersion || oldRules.Version == RulesV3.RulesVersion ? RulesV1.RulesVersion : oldRules.Version,
            targetVersion,
            leagues,
            f2,
            f3,
            pools,
            poolTotal,
            movements,
            checksum,
            rngBefore.State,
            rngBefore.Stream,
            rngAfter.State,
            rngAfter.Stream,
            alreadyApplied);
    }

    internal static List<UpgradeTierLeague> MapCreatedLeagues(
        List<LeagueEntity> created, Dictionary<int, int> counts)
    {
        List<UpgradeTierLeague> leagues = new(created.Count);
        foreach (LeagueEntity league in created)
        {
            counts.TryGetValue(league.Id, out int count);
            leagues.Add(new UpgradeTierLeague(
                league.Id,
                league.Name,
                ((SportingColor)league.SportingColor).ToString(),
                league.FeederDivision,
                LeagueHierarchy.DisplayName(LeagueHierarchy.LevelForDivision((FeederDivision)league.FeederDivision, isSuperleague: false)),
                count));
        }

        return leagues;
    }

    internal static async Task<List<UpgradeTierSeed>> MapSeedsAsync(
        SaveDbContext context,
        SeasonEntity next,
        IReadOnlyList<UpgradeToTieredSelection.PoolCandidate> seeds,
        FeederDivision division,
        Dictionary<int, string> names,
        Dictionary<int, string?> images,
        CancellationToken cancellationToken)
    {
        Dictionary<int, SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);

        List<UpgradeTierSeed> list = new(seeds.Count);
        foreach (UpgradeToTieredSelection.PoolCandidate seed in seeds.OrderBy(s => s.SaveAthleteId))
        {
            memberships.TryGetValue(seed.SaveAthleteId, out SeasonMembershipEntity? membership);
            int leagueId = membership?.LeagueId ?? 0;
            leagues.TryGetValue(leagueId, out LeagueEntity? league);
            names.TryGetValue(seed.SaveAthleteId, out string? name);
            images.TryGetValue(seed.SaveAthleteId, out string? image);
            list.Add(new UpgradeTierSeed(
                seed.SaveAthleteId,
                name ?? seed.Name,
                ((SportingColor)(membership?.SportingColor ?? (int)seed.SportingColor)).ToString(),
                leagueId,
                league?.Name ?? $"League {leagueId}",
                (int)division,
                image));
        }

        return list;
    }

    internal static async Task<List<UpgradePoolCount>> MapPoolsAsync(
        SaveDbContext context, SeasonEntity next, CancellationToken cancellationToken)
    {
        List<UpgradePoolCount> pools = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            int count = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == null && e.SportingColor == (int)color, cancellationToken)
                .ConfigureAwait(false);
            pools.Add(new UpgradePoolCount(color.ToString(), count));
        }

        return pools;
    }
}
