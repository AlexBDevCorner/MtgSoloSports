using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Qualifiers.Engine;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Generic 16-athlete feeder qualifier execution (MSS-058).
/// One boundary (F1↔F2 or F2↔F3) plus one sporting color per event, 16 rounds,
/// top 8 occupy/remain in the higher tier. Active bonus applies; no new bonus
/// or championship points; immutable replay payload/checksum/RNG before/after
/// persisted per qualifier with boundary identity. Deterministic canonical
/// order is enforced by the caller (RunAll): Superleague first, then F1↔F2 by
/// color enum, then F2↔F3 by color enum. Each event commits RNG + results in
/// one transaction; retry skips already-resolved events and never reruns them.
/// Simulation, ranking, checksums, payload persistence and the completion
/// spine live in the shared <see cref="QualifierEngine"/> (MSS-150); this
/// runner owns the feeder path's guards, field selection, roster destination
/// rules and persisted validation.
/// </summary>
public sealed class BoundaryQualifierRunner
{
    private readonly SaveStore _store;

    public BoundaryQualifierRunner(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<RunFeederQualifierResponse> HandleAsync(
        Guid saveId,
        QualifierBoundary boundary,
        int sportingColor,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (boundary != QualifierBoundary.Feeder1Feeder2 && boundary != QualifierBoundary.Feeder2Feeder3)
        {
            throw new ArgumentException($"Boundary must be F1↔F2 or F2↔F3, was {boundary}.", nameof(boundary));
        }

        if (sportingColor < 0 || sportingColor >= 8)
        {
            throw new ArgumentException($"Sporting color must be 0..7, was {sportingColor}.", nameof(sportingColor));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunSingleUnderLockAsync(saveId, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<RunFeederQualifierResponse> RunSingleUnderLockAsync(
        Guid saveId,
        QualifierBoundary boundary,
        int sportingColor,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        QualifierEngineState state = await LoadStateAsync(context, saveId, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
        QualifierEngineHooks hooks = CreateHooks();
        List<QualifierRoundPayload> payloads = new(state.Played);
        Pcg32State current = state.Rng;
        while (payloads.Count < state.TotalRounds)
        {
            QualifierRoundPayload payload = QualifierEngine.PlayRound(state, payloads, current, hooks.ValidateRound);
            payloads.Add(payload);
            current = new Pcg32State(payload.RngAfterState, payload.RngAfterStream);
        }

        QualifierEngineSimulation simulation = await QualifierEngine.CompleteAsync(
            context, state, payloads, hooks, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(_store, saveId, state, simulation, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<QualifierEngineState> LoadStateAsync(
        SaveDbContext context,
        Guid saveId,
        QualifierBoundary boundary,
        int sportingColor,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);

        (SeasonEntity source, SeasonEntity next) = await LoadPendingTransitionAsync(context, cancellationToken).ConfigureAwait(false);
        await EnsureAutomaticMovementResolvedAsync(context, source, next, rules, cancellationToken).ConfigureAwait(false);
        await QualifierEngine.EnsureChaosCompleteAsync(
            context, source, rules, static message => new RunFeederQualifierConflictException(message), cancellationToken).ConfigureAwait(false);
        await EnsureQualifierUnresolvedAsync(context, source, next, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
        await EnsureCanonicalOrderAsync(context, source, next, boundary, sportingColor, rules, cancellationToken).ConfigureAwait(false);
        await EnsureNoSuperleagueInProgressAsync(context, source, next, cancellationToken).ConfigureAwait(false);

        (LeagueEntity upper, LeagueEntity lower) = await LoadBoundaryLeaguesAsync(
            context, source, boundary, sportingColor, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> upperStandings = await QualifierEngine.LoadLeagueStandingsAsync(
            context, source, upper, rules, requireCompleteRanks: false, cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> lowerStandings = await QualifierEngine.LoadLeagueStandingsAsync(
            context, source, lower, rules, requireCompleteRanks: false, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> sourceMemberships = await QualifierEngine.LoadSourceMembershipsAsync(
            context, source, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete = sourceMemberships.ToDictionary(m => m.SaveAthleteId);
        Dictionary<int, string> namesByAthlete = await QualifierEngine.LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);

        FeederQualifierFieldSelection.FeederField field = FeederQualifierFieldSelection.Select(
            boundary, sportingColor, upperStandings, upper, lowerStandings, lower,
            membershipByAthlete, namesByAthlete, rules);
        FeederQualifierInvariants.ValidateField(field, upperStandings, lowerStandings, upper, lower, rules);

        IReadOnlyList<QualifierProvenance> provenance = ToProvenance(field);
        List<AdvanceRoundHandler.MemberRow> roster = QualifierEngine.BuildRoster(provenance);
        Dictionary<int, MtgSoloSports.SimulationKernel.FixedPoint.Bonus> activeBonuses = await QualifierEngine.LoadStageStartBonusesAsync(
            context, roster, next, rules, cancellationToken).ConfigureAwait(false);

        List<QualifierRoundPayload> played = await LoadPlayedRoundsAsync(context, source, next, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
        ValidateInProgress(played, rules, rngRow.ToState(), boundary, sportingColor);

        return new QualifierEngineState(
            rules, metadata, source, next, boundary, sportingColor,
            rules.FeederQualifierSize, rules.FeederQualifierRounds, rules.FeederQualifierWinners,
            QualifierIdentity.HigherLevel(boundary),
            provenance, roster, activeBonuses, played, rngRow.ToState());
    }

    /// <summary>
    /// The feeder path's completion behavior (MSS-150): feeder-strict per-round
    /// and completed-qualifier invariants, winners to the upper tier and
    /// losers to the lower tier, feeder-identity persisted validation, and no
    /// extra story events.
    /// </summary>
    internal static QualifierEngineHooks CreateHooks() => new(
        static (payload, state, rngBefore) => ValidateRoundPayload(payload, state),
        static (ranked, totals, rules) => FeederQualifierInvariants.ValidateCompleted(ranked, totals, rules),
        static (context, state, simulation, cancellationToken) => ApplyToNextRosterAsync(context, state, simulation, cancellationToken),
        static (context, state, simulation, cancellationToken) => ValidatePersistedAsync(context, state, simulation, cancellationToken),
        null);

    internal static IReadOnlyList<QualifierProvenance> ToProvenance(FeederQualifierFieldSelection.FeederField field)
    {
        List<QualifierProvenance> provenance = new(field.All.Count);
        foreach (FeederQualifierFieldSelection.FeederPick pick in field.All)
        {
            provenance.Add(new QualifierProvenance(
                pick.SaveAthleteId, pick.Name, pick.SportingColor, pick.FromLeagueId, pick.FromSeasonRank, pick.Role));
        }

        return provenance;
    }

    internal static async Task<List<QualifierRoundPayload>> LoadPlayedRoundsAsync(
        SaveDbContext context,
        SeasonEntity source,
        SeasonEntity next,
        QualifierBoundary boundary,
        int sportingColor,
        CancellationToken cancellationToken)
    {
        List<QualifierRoundEntity> rows = await context.QualifierRounds
            .AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == sportingColor)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<QualifierRoundPayload> played = new(rows.Count);
        foreach (QualifierRoundEntity row in rows)
        {
            played.Add(QualifierRoundPayload.FromJson(row.PayloadJson));
        }

        return played;
    }

    internal static void ValidateInProgress(
        List<QualifierRoundPayload> played,
        RulesV1 rules,
        Pcg32State rngRow,
        QualifierBoundary boundary,
        int sportingColor)
    {
        ArgumentNullException.ThrowIfNull(played);
        ArgumentNullException.ThrowIfNull(rules);
        if (played.Count >= rules.FeederQualifierRounds)
        {
            throw new SaveCorruptionException(
                $"Feeder qualifier {boundary} color {sportingColor} has all {rules.FeederQualifierRounds} rounds persisted but no standings; sporting state is corrupt.");
        }

        for (int index = 0; index < played.Count; index++)
        {
            QualifierRoundPayload payload = played[index];
            if (payload.RoundNumber != index + 1)
            {
                throw new SaveCorruptionException(
                    $"Feeder qualifier {boundary} color {sportingColor} round sequence is corrupt: expected round {index + 1}, found {payload.RoundNumber}.");
            }

            if (payload.Boundary != boundary || payload.SportingColor != sportingColor)
            {
                throw new SaveCorruptionException(
                    $"Feeder qualifier {boundary} color {sportingColor} round {payload.RoundNumber} has corrupt event identity.");
            }

            if (index > 0)
            {
                QualifierRoundPayload previous = played[index - 1];
                if (payload.RngBeforeState != previous.RngAfterState || payload.RngBeforeStream != previous.RngAfterStream)
                {
                    throw new SaveCorruptionException(
                        $"Feeder qualifier {boundary} color {sportingColor} RNG chain is broken before round {payload.RoundNumber}.");
                }
            }
        }

        if (played.Count == 0)
        {
            return;
        }

        QualifierRoundPayload last = played[^1];
        Pcg32State expectedRow = new(last.RngAfterState, last.RngAfterStream);
        if (rngRow != expectedRow)
        {
            throw new SaveCorruptionException(
                $"Feeder qualifier {boundary} color {sportingColor} is in progress but the save RNG moved since its last round; sporting state is corrupt.");
        }
    }

    internal static void ValidateRoundPayload(QualifierRoundPayload payload, QualifierEngineState state)
    {
        if (payload.Placements.Count != state.Rules.FeederQualifierSize)
        {
            throw new SaveCorruptionException(
                $"Feeder qualifier round must contain exactly {state.Rules.FeederQualifierSize} placements, was {payload.Placements.Count}.");
        }

        HashSet<int> positions = new();
        HashSet<int> ids = new();
        foreach (RoundPayloadEntry entry in payload.Placements)
        {
            if (!positions.Add(entry.Position) || !ids.Add(entry.AthleteId))
            {
                throw new SaveCorruptionException("Feeder qualifier payload contains duplicates.");
            }

            FeederQualifierInvariants.ValidateRound(entry, state.Boundary, state.Rules);
        }

        if (!positions.SetEquals(Enumerable.Range(1, state.Rules.FeederQualifierSize)))
        {
            throw new SaveCorruptionException("Feeder qualifier payload must cover positions 1..16 exactly once.");
        }
    }

    internal static async Task ApplyToNextRosterAsync(
        SaveDbContext context,
        QualifierEngineState state,
        QualifierEngineSimulation simulation,
        CancellationToken cancellationToken)
    {
        (int upperLeagueId, int lowerLeagueId) = await LoadNextLeagueTargetsAsync(
            context, state.Next, state.Boundary, state.SportingColor, state.Rules, cancellationToken).ConfigureAwait(false);
        HashSet<int> qualifiedIds = simulation.Ranked
            .Where(r => r.StageRank <= state.Rules.FeederQualifierWinners)
            .Select(r => r.AthleteId)
            .ToHashSet();
        if (qualifiedIds.Count != state.Rules.FeederQualifierWinners)
        {
            throw new SaveCorruptionException(
                $"Feeder qualifier must hold exactly {state.Rules.FeederQualifierWinners} winners, was {qualifiedIds.Count}.");
        }

        HashSet<int> fieldIds = simulation.Field.Select(p => p.SaveAthleteId).ToHashSet();
        if (fieldIds.Count != state.Rules.FeederQualifierSize)
        {
            throw new SaveCorruptionException("Feeder qualifier field size is corrupt.");
        }

        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == state.Next.Id && fieldIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != state.Rules.FeederQualifierSize)
        {
            throw new SaveCorruptionException(
                $"Next season must hold exactly {state.Rules.FeederQualifierSize} feeder qualifier memberships, was {memberships.Count}.");
        }

        foreach (SeasonMembershipEntity membership in memberships)
        {
            if (membership.LeagueId is null)
            {
                throw new SaveCorruptionException($"Feeder qualifier athlete {membership.SaveAthleteId} has no provisional league.");
            }

            membership.LeagueId = qualifiedIds.Contains(membership.SaveAthleteId) ? upperLeagueId : lowerLeagueId;
        }
    }

    internal static async Task<(int UpperLeagueId, int LowerLeagueId)> LoadNextLeagueTargetsAsync(
        SaveDbContext context,
        SeasonEntity next,
        QualifierBoundary boundary,
        int sportingColor,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        FeederDivision upperDivision = boundary switch
        {
            QualifierBoundary.Feeder1Feeder2 => FeederDivision.First,
            QualifierBoundary.Feeder2Feeder3 => FeederDivision.Second,
            _ => throw new SaveCorruptionException($"Unexpected feeder boundary {boundary}."),
        };
        FeederDivision lowerDivision = boundary switch
        {
            QualifierBoundary.Feeder1Feeder2 => FeederDivision.Second,
            QualifierBoundary.Feeder2Feeder3 => FeederDivision.Third,
            _ => throw new SaveCorruptionException($"Unexpected feeder boundary {boundary}."),
        };

        LeagueEntity? upper = await context.Leagues.AsNoTracking().SingleOrDefaultAsync(
            e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder
                && e.FeederDivision == (int)upperDivision && e.SportingColor == sportingColor,
            cancellationToken).ConfigureAwait(false);
        LeagueEntity? lower = await context.Leagues.AsNoTracking().SingleOrDefaultAsync(
            e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder
                && e.FeederDivision == (int)lowerDivision && e.SportingColor == sportingColor,
            cancellationToken).ConfigureAwait(false);
        if (upper is null || lower is null)
        {
            throw new SaveCorruptionException(
                $"Season {next.SeasonNumber} is missing the {boundary} leagues for color {sportingColor}.");
        }

        _ = rules;
        return (upper.Id, lower.Id);
    }

    internal static async Task ValidatePersistedAsync(
        SaveDbContext context,
        QualifierEngineState state,
        QualifierEngineSimulation simulation,
        CancellationToken cancellationToken)
    {
        List<QualifierRoundEntity> rounds = await context.QualifierRounds
            .Where(e => e.FromSeasonId == state.Source.Id && e.ToSeasonId == state.Next.Id
                && e.QualifierBoundary == (int)state.Boundary && e.QualifierSportingColor == state.SportingColor)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<QualifierStandingEntity> standings = await context.QualifierStandings
            .Where(e => e.FromSeasonId == state.Source.Id && e.ToSeasonId == state.Next.Id
                && e.QualifierBoundary == (int)state.Boundary && e.QualifierSportingColor == state.SportingColor)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        FeederQualifierInvariants.ValidatePersisted(
            state.Source, state.Next, state.Boundary, state.SportingColor, rounds, standings, state.Rules);

        HashSet<int> fieldIds = simulation.Field.Select(p => p.SaveAthleteId).ToHashSet();
        HashSet<int> standingIds = standings.Select(s => s.SaveAthleteId).ToHashSet();
        if (!fieldIds.SetEquals(standingIds))
        {
            throw new SaveCorruptionException("Feeder qualifier standings do not match the qualifier field.");
        }
    }

    internal static async Task<(SeasonEntity Source, SeasonEntity Next)> LoadPendingTransitionAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        List<SeasonEntity> candidates = await context.Seasons
            .Where(e => e.IsComplete && e.HasSuperleague)
            .OrderByDescending(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new RunFeederQualifierConflictException(
                "No completed Superleague season is ready for feeder qualifiers.");
        }

        foreach (SeasonEntity candidate in candidates)
        {
            SeasonEntity? successor = await context.Seasons.SingleOrDefaultAsync(
                e => e.SeasonNumber == candidate.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
            if (successor is null || !successor.HasSuperleague || successor.IsComplete)
            {
                continue;
            }

            return (candidate, successor);
        }

        throw new RunFeederQualifierConflictException(
            "No pending postseason transition is ready for feeder qualifiers.");
    }

    internal static async Task EnsureAutomaticMovementResolvedAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, RulesV1 rules, CancellationToken cancellationToken)
    {
        int promotions = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.AutomaticPromotion,
            cancellationToken).ConfigureAwait(false);
        int relegations = await context.Movements.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.AutomaticRelegation,
            cancellationToken).ConfigureAwait(false);
        if (promotions != rules.FeederAutoPromotedCount || relegations != rules.SuperleagueRelegatedCount)
        {
            throw new RunFeederQualifierConflictException(
                "Automatic movement must be resolved before feeder qualifiers can run.");
        }

        if (rules.FeederDivisionsPerColor == 3)
        {
            int feederPromos = await context.Movements.CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.FeederAutomaticPromotion,
                cancellationToken).ConfigureAwait(false);
            int feederRelegs = await context.Movements.CountAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.FeederAutomaticRelegation,
                cancellationToken).ConfigureAwait(false);
            int expectedFeederAutos = rules.SportingColorCount * 8 * 2;
            if (feederPromos != expectedFeederAutos || feederRelegs != expectedFeederAutos)
            {
                throw new RunFeederQualifierConflictException(
                    "Feeder automatic movement must be resolved before feeder qualifiers can run.");
            }
        }
    }

    internal static async Task EnsureQualifierUnresolvedAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next,
        QualifierBoundary boundary, int sportingColor, CancellationToken cancellationToken)
    {
        bool hasStandings = await context.QualifierStandings.AnyAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == sportingColor,
            cancellationToken).ConfigureAwait(false);
        if (hasStandings)
        {
            throw new RunFeederQualifierConflictException(
                $"Feeder qualifier {boundary} color {sportingColor} for Season {source.SeasonNumber} has already been resolved.");
        }
    }

    internal static async Task EnsureCanonicalOrderAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next,
        QualifierBoundary boundary, int sportingColor, RulesV1 rules, CancellationToken cancellationToken)
    {
        if (rules.FeederDivisionsPerColor != 3)
        {
            throw new RunFeederQualifierConflictException(
                "Feeder qualifiers require a tiered save with three feeder divisions.");
        }

        bool superResolved = await context.QualifierStandings.AnyAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)QualifierBoundary.Superleague,
            cancellationToken).ConfigureAwait(false);
        if (!superResolved)
        {
            throw new RunFeederQualifierConflictException(
                "The Superleague qualifier must be resolved before feeder qualifiers can run (canonical order).");
        }

        // Enforce exact canonical position: Superleague, then F1↔F2 by color
        // enum order, then F2↔F3 by color order. The requested event must be the
        // first unresolved event in that order; out-of-order mutations are
        // rejected without changing save state and never silently advance a
        // different qualifier.
        foreach ((QualifierBoundary orderedBoundary, int? orderedColor) in QualifierIdentity.CanonicalOrder)
        {
            if (orderedBoundary == QualifierBoundary.Superleague)
            {
                continue;
            }

            if (orderedBoundary == boundary && orderedColor == sportingColor)
            {
                return;
            }

            int storageColor = QualifierIdentity.ToStorageColor(orderedColor);
            bool resolved = await context.QualifierStandings.AnyAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                    && e.QualifierBoundary == (int)orderedBoundary && e.QualifierSportingColor == storageColor,
                cancellationToken).ConfigureAwait(false);
            if (!resolved)
            {
                throw new RunFeederQualifierConflictException(
                    $"Qualifier {orderedBoundary} color {orderedColor} must be resolved before {boundary} color {sportingColor} (canonical order).");
            }
        }

        throw new SaveCorruptionException($"Unknown qualifier identity {boundary} color {sportingColor}.");
    }

    internal static async Task EnsureAllF1F2ResolvedAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        int resolved = await context.QualifierStandings
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)QualifierBoundary.Feeder1Feeder2)
            .Select(e => e.QualifierSportingColor)
            .Distinct()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        if (resolved != 8)
        {
            throw new RunFeederQualifierConflictException(
                "All eight F1↔F2 qualifiers must be resolved before F2↔F3 qualifiers can run (canonical order).");
        }
    }

    internal static async Task EnsureNoSuperleagueInProgressAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        int rounds = await context.QualifierRounds.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)QualifierBoundary.Superleague,
            cancellationToken).ConfigureAwait(false);
        int standings = await context.QualifierStandings.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)QualifierBoundary.Superleague,
            cancellationToken).ConfigureAwait(false);
        if (rounds > 0 && standings == 0)
        {
            throw new RunFeederQualifierConflictException(
                "The Superleague qualifier is partly played; finish it before playing feeder qualifiers.");
        }
    }

    internal static async Task<(LeagueEntity Upper, LeagueEntity Lower)> LoadBoundaryLeaguesAsync(
        SaveDbContext context, SeasonEntity source, QualifierBoundary boundary, int sportingColor,
        RulesV1 rules, CancellationToken cancellationToken)
    {
        (FeederDivision upperDiv, FeederDivision lowerDiv) = boundary switch
        {
            QualifierBoundary.Feeder1Feeder2 => (FeederDivision.First, FeederDivision.Second),
            QualifierBoundary.Feeder2Feeder3 => (FeederDivision.Second, FeederDivision.Third),
            _ => throw new SaveCorruptionException($"Unexpected feeder boundary {boundary}."),
        };

        LeagueEntity? upper = await context.Leagues.SingleOrDefaultAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Feeder
                && e.FeederDivision == (int)upperDiv && e.SportingColor == sportingColor,
            cancellationToken).ConfigureAwait(false);
        LeagueEntity? lower = await context.Leagues.SingleOrDefaultAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Feeder
                && e.FeederDivision == (int)lowerDiv && e.SportingColor == sportingColor,
            cancellationToken).ConfigureAwait(false);
        if (upper is null || lower is null)
        {
            throw new SaveCorruptionException(
                $"Season {source.SeasonNumber} is missing the {boundary} leagues for color {sportingColor}.");
        }

        _ = rules;
        return (upper, lower);
    }

    internal static async Task<RunFeederQualifierResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        QualifierEngineState state,
        QualifierEngineSimulation simulation,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<QualifierStandingEntity> standings = await context.QualifierStandings
            .AsNoTracking()
            .Where(e => e.FromSeasonId == state.Source.Id && e.ToSeasonId == state.Next.Id
                && e.QualifierBoundary == (int)state.Boundary && e.QualifierSportingColor == state.SportingColor)
            .OrderBy(e => e.QualifierRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        List<FeederQualifierStandingMember> members = new(standings.Count);
        foreach (QualifierStandingEntity standing in standings)
        {
            names.TryGetValue(standing.SaveAthleteId, out string? name);
            leaguesById.TryGetValue(standing.FromLeagueId, out LeagueEntity? from);
            members.Add(new FeederQualifierStandingMember(
                standing.SaveAthleteId,
                name ?? $"Athlete {standing.SaveAthleteId}",
                ((SportingColor)standing.SportingColor).ToString(),
                ((QualifierRole)standing.Role).ToString(),
                standing.FromLeagueId,
                from?.Name ?? $"League {standing.FromLeagueId}",
                standing.FromSeasonRank,
                standing.QualifierRank,
                standing.QualifierScoreThousandths,
                standing.BaseScoreThousandths,
                standing.RoundWins,
                standing.IsQualified));
        }

        QualifierRoundPayload first = simulation.Payloads[0];
        QualifierRoundPayload last = simulation.Payloads[^1];
        return new RunFeederQualifierResponse(
            saveId,
            state.Source.SeasonNumber,
            state.Next.SeasonNumber,
            ((int)state.Boundary).ToString(System.Globalization.CultureInfo.InvariantCulture),
            state.Boundary.ToString(),
            state.SportingColor,
            ((SportingColor)state.SportingColor).ToString(),
            standings.Count,
            simulation.Payloads.Count,
            standings.Count(s => s.IsQualified),
            simulation.Checksum,
            first.RngBeforeState,
            first.RngBeforeStream,
            last.RngAfterState,
            last.RngAfterStream,
            members);
    }
}
