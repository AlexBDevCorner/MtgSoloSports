using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.Stages;

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

        QualifierState state = await LoadStateAsync(context, saveId, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
        List<QualifierRoundPayload> payloads = new(state.Rules.FeederQualifierRounds);
        Pcg32State current = state.Rng;
        while (payloads.Count < state.Rules.FeederQualifierRounds)
        {
            QualifierRoundPayload payload = PlayRound(state, payloads, current);
            payloads.Add(payload);
            current = new Pcg32State(payload.RngAfterState, payload.RngAfterStream);
        }

        QualifierSimulation simulation = await CompleteAsync(context, state, payloads, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(_store, saveId, state, simulation, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record QualifierState(
        RulesV1 Rules,
        SaveMetadataEntity Metadata,
        SeasonEntity Source,
        SeasonEntity Next,
        QualifierBoundary Boundary,
        int SportingColor,
        LeagueEntity UpperLeague,
        LeagueEntity LowerLeague,
        FeederQualifierFieldSelection.FeederField Field,
        List<AdvanceRoundHandler.MemberRow> Roster,
        Dictionary<int, Bonus> ActiveBonuses,
        Pcg32State Rng);

    internal sealed record QualifierSimulation(
        FeederQualifierFieldSelection.FeederField Field,
        List<QualifierRoundPayload> Payloads,
        IReadOnlyList<StageAthleteTotals> Totals,
        IReadOnlyList<StageRankedAthlete> Ranked,
        Pcg32State RngAfter,
        string Checksum);

    internal sealed record QualifierRoundPayload(
        int Version,
        int RulesVersion,
        int FromSeasonNumber,
        int ToSeasonNumber,
        QualifierBoundary Boundary,
        int SportingColor,
        int RoundNumber,
        ulong RngBeforeState,
        ulong RngBeforeStream,
        ulong RngAfterState,
        ulong RngAfterStream,
        string Checksum,
        IReadOnlyList<RoundPayloadEntry> Placements)
    {
        public const int PayloadVersion = 1;

        public string ToJson()
        {
            var doc = new
            {
                version = Version,
                rulesVersion = RulesVersion,
                fromSeasonNumber = FromSeasonNumber,
                toSeasonNumber = ToSeasonNumber,
                boundary = (int)Boundary,
                sportingColor = SportingColor,
                roundNumber = RoundNumber,
                rngBeforeState = RngBeforeState,
                rngBeforeStream = RngBeforeStream,
                rngAfterState = RngAfterState,
                rngAfterStream = RngAfterStream,
                checksum = Checksum,
                placements = Placements,
            };
            return JsonSerializer.Serialize(doc);
        }
    }

    internal static async Task<QualifierState> LoadStateAsync(
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
        await EnsureQualifierUnresolvedAsync(context, source, next, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
        await EnsureNoPartialAsync(context, source, next, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
        await EnsureCanonicalOrderAsync(context, source, next, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
        await EnsureNoSuperleagueInProgressAsync(context, source, next, cancellationToken).ConfigureAwait(false);

        (LeagueEntity upper, LeagueEntity lower) = await LoadBoundaryLeaguesAsync(
            context, source, boundary, sportingColor, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> upperStandings = await LoadLeagueStandingsAsync(
            context, source, upper, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> lowerStandings = await LoadLeagueStandingsAsync(
            context, source, lower, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> sourceMemberships = await LoadSourceMembershipsAsync(
            context, source, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete = sourceMemberships.ToDictionary(m => m.SaveAthleteId);
        Dictionary<int, string> namesByAthlete = await LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);

        FeederQualifierFieldSelection.FeederField field = FeederQualifierFieldSelection.Select(
            boundary, sportingColor, upperStandings, upper, lowerStandings, lower,
            membershipByAthlete, namesByAthlete, rules);
        FeederQualifierInvariants.ValidateField(field, upperStandings, lowerStandings, upper, lower, rules);

        List<AdvanceRoundHandler.MemberRow> roster = BuildRoster(field);
        Dictionary<int, Bonus> activeBonuses = await LoadActiveBonusesAsync(
            context, roster, next, rules, cancellationToken).ConfigureAwait(false);

        return new QualifierState(
            rules, metadata, source, next, boundary, sportingColor,
            upper, lower, field, roster, activeBonuses, rngRow.ToState());
    }

    internal static List<AdvanceRoundHandler.MemberRow> BuildRoster(FeederQualifierFieldSelection.FeederField field)
    {
        List<AdvanceRoundHandler.MemberRow> roster = new(field.All.Count);
        foreach (FeederQualifierFieldSelection.FeederPick pick in field.All)
        {
            roster.Add(new AdvanceRoundHandler.MemberRow(pick.SaveAthleteId, pick.Name, pick.SportingColor));
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (AdvanceRoundHandler.MemberRow row in roster)
        {
            if (!names.Add(row.Name))
            {
                throw new InvalidOperationException($"Feeder qualifier field contains duplicate athlete '{row.Name}'.");
            }
        }

        roster.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return roster;
    }

    internal static QualifierRoundPayload PlayRound(
        QualifierState state,
        IReadOnlyList<QualifierRoundPayload> played,
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
        RoundSimulationResult simulation = SimulateRoundWithFieldSize(
            state.Roster, cumulative, state.ActiveBonuses, rngBefore, state.Rules, state.Rules.FeederQualifierSize);
        QualifierRoundPayload payload = new(
            QualifierRoundPayload.PayloadVersion,
            state.Rules.Version,
            state.Source.SeasonNumber,
            state.Next.SeasonNumber,
            state.Boundary,
            state.SportingColor,
            roundNumber,
            rngBefore.State,
            rngBefore.Stream,
            simulation.RngAfter.State,
            simulation.RngAfter.Stream,
            simulation.Checksum,
            ToPayloadEntries(simulation));
        ValidateRoundPayload(payload, state);
        return payload;
    }

    internal static RoundSimulationResult SimulateRoundWithFieldSize(
        List<AdvanceRoundHandler.MemberRow> roster,
        Dictionary<int, Points> cumulativeBefore,
        Dictionary<int, Bonus> activeBonuses,
        Pcg32State rngBefore,
        RulesV1 rules,
        int fieldSize)
    {
        List<RoundAthleteInput> inputs = new(roster.Count);
        foreach (AdvanceRoundHandler.MemberRow row in roster)
        {
            Points before = cumulativeBefore.TryGetValue(row.AthleteId, out Points value) ? value : Points.Zero;
            if (!activeBonuses.TryGetValue(row.AthleteId, out Bonus bonus))
            {
                throw new InvalidOperationException($"Missing stage-start bonus for athlete '{row.Name}'.");
            }

            inputs.Add(new RoundAthleteInput(row.AthleteId, row.Name, bonus, before));
        }

        Pcg32V1 rng = Pcg32V1.Restore(rngBefore);
        return RoundSimulator.SimulateWithFieldSize(inputs, rng, rules, fieldSize);
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

    internal static void ValidateRoundPayload(QualifierRoundPayload payload, QualifierState state)
    {
        if (payload.Placements.Count != state.Rules.FeederQualifierSize)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier round must contain exactly {state.Rules.FeederQualifierSize} placements, was {payload.Placements.Count}.");
        }

        HashSet<int> positions = new();
        HashSet<int> ids = new();
        foreach (RoundPayloadEntry entry in payload.Placements)
        {
            if (!positions.Add(entry.Position) || !ids.Add(entry.AthleteId))
            {
                throw new InvalidOperationException("Feeder qualifier payload contains duplicates.");
            }

            FeederQualifierInvariants.ValidateRound(entry, state.Boundary, state.Rules);
        }

        if (!positions.SetEquals(Enumerable.Range(1, state.Rules.FeederQualifierSize)))
        {
            throw new InvalidOperationException("Feeder qualifier payload must cover positions 1..16 exactly once.");
        }
    }

    internal static async Task<QualifierSimulation> CompleteAsync(
        SaveDbContext context,
        QualifierState state,
        List<QualifierRoundPayload> payloads,
        CancellationToken cancellationToken)
    {
        QualifierSimulation simulation = Finish(state, payloads);
        await PersistQualifierAsync(context, state, simulation, cancellationToken).ConfigureAwait(false);
        await ApplyToNextRosterAsync(context, state, simulation, cancellationToken).ConfigureAwait(false);
        context.ApplyRngState(simulation.RngAfter);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await ValidatePersistedAsync(context, state, simulation, cancellationToken).ConfigureAwait(false);
        await EmitStoriesAsync(context, state, simulation, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return simulation;
    }

    internal static QualifierSimulation Finish(QualifierState state, List<QualifierRoundPayload> payloads)
    {
        List<List<StageRoundEntry>> roundEntries = new(payloads.Count);
        foreach (QualifierRoundPayload payload in payloads)
        {
            List<StageRoundEntry> entries = new(payload.Placements.Count);
            foreach (RoundPayloadEntry placement in payload.Placements)
            {
                entries.Add(new StageRoundEntry(
                    placement.AthleteId,
                    placement.Name,
                    placement.Position,
                    placement.BaseThousandths,
                    placement.FinalThousandths));
            }

            roundEntries.Add(entries);
        }

        IReadOnlyList<StageAthleteTotals> totals = StageCalculator.AccumulateWithFieldSize(
            roundEntries, state.Rules, state.Rules.FeederQualifierSize, state.Rules.FeederQualifierRounds);
        QualifierRoundPayload last = payloads[^1];
        Pcg32V1 tieBreakRng = Pcg32V1.Restore(new Pcg32State(last.RngAfterState, last.RngAfterStream));
        LeagueLevel upperLevel = QualifierIdentity.HigherLevel(state.Boundary);
        IReadOnlyList<StageRankedAthlete> ranked = StageCalculator.RankWithFieldSize(
            totals, tieBreakRng, state.Rules, upperLevel, state.Rules.FeederQualifierSize);
        FeederQualifierInvariants.ValidateCompleted(ranked, totals, state.Rules);
        Pcg32State rngAfter = tieBreakRng.Snapshot();
        string checksum = ComputeChecksum(ranked);
        return new QualifierSimulation(state.Field, payloads, totals, ranked, rngAfter, checksum);
    }

    public static string ComputeChecksum(IReadOnlyList<StageRankedAthlete> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (StageRankedAthlete entry in ranked)
        {
            builder.Append(entry.StageRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.AthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.Name);
            builder.Append(':');
            builder.Append(entry.StageScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.BaseScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    internal static async Task PersistQualifierAsync(
        SaveDbContext context,
        QualifierState state,
        QualifierSimulation simulation,
        CancellationToken cancellationToken)
    {
        foreach (QualifierRoundPayload payload in simulation.Payloads)
        {
            context.QualifierRounds.Add(new QualifierRoundEntity
            {
                FromSeasonId = state.Source.Id,
                ToSeasonId = state.Next.Id,
                QualifierBoundary = (int)state.Boundary,
                QualifierSportingColor = state.SportingColor,
                RoundNumber = payload.RoundNumber,
                RulesVersion = payload.RulesVersion,
                RngBeforeState = unchecked((long)payload.RngBeforeState),
                RngBeforeStream = unchecked((long)payload.RngBeforeStream),
                RngAfterState = unchecked((long)payload.RngAfterState),
                RngAfterStream = unchecked((long)payload.RngAfterStream),
                PayloadJson = payload.ToJson(),
                PayloadChecksum = payload.Checksum,
            });
        }

        Dictionary<int, FeederQualifierFieldSelection.FeederPick> picksByAthlete =
            simulation.Field.All.ToDictionary(p => p.SaveAthleteId);
        foreach (StageRankedAthlete entry in simulation.Ranked)
        {
            if (!picksByAthlete.TryGetValue(entry.AthleteId, out FeederQualifierFieldSelection.FeederPick? pick))
            {
                throw new InvalidOperationException($"Feeder qualifier standing for '{entry.Name}' has no field provenance.");
            }

            context.QualifierStandings.Add(new QualifierStandingEntity
            {
                FromSeasonId = state.Source.Id,
                ToSeasonId = state.Next.Id,
                QualifierBoundary = (int)state.Boundary,
                QualifierSportingColor = state.SportingColor,
                SaveAthleteId = entry.AthleteId,
                QualifierRank = entry.StageRank,
                QualifierScoreThousandths = entry.StageScoreThousandths,
                BaseScoreThousandths = entry.BaseScoreThousandths,
                RoundWins = entry.RoundWins,
                RoundPlaceCountsJson = JsonSerializer.Serialize(entry.RoundPlaceCounts),
                IsQualified = entry.StageRank <= state.Rules.FeederQualifierWinners,
                Role = (int)pick.Role,
                FromLeagueId = pick.FromLeagueId,
                FromSeasonRank = pick.FromSeasonRank,
                SportingColor = pick.SportingColor,
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ApplyToNextRosterAsync(
        SaveDbContext context,
        QualifierState state,
        QualifierSimulation simulation,
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
            throw new InvalidOperationException(
                $"Feeder qualifier must hold exactly {state.Rules.FeederQualifierWinners} winners, was {qualifiedIds.Count}.");
        }

        HashSet<int> fieldIds = simulation.Field.All.Select(p => p.SaveAthleteId).ToHashSet();
        if (fieldIds.Count != state.Rules.FeederQualifierSize)
        {
            throw new InvalidOperationException("Feeder qualifier field size is corrupt.");
        }

        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == state.Next.Id && fieldIds.Contains(e.SaveAthleteId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != state.Rules.FeederQualifierSize)
        {
            throw new InvalidOperationException(
                $"Next season must hold exactly {state.Rules.FeederQualifierSize} feeder qualifier memberships, was {memberships.Count}.");
        }

        foreach (SeasonMembershipEntity membership in memberships)
        {
            if (membership.LeagueId is null)
            {
                throw new InvalidOperationException($"Feeder qualifier athlete {membership.SaveAthleteId} has no provisional league.");
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
            _ => throw new InvalidOperationException($"Unexpected feeder boundary {boundary}."),
        };
        FeederDivision lowerDivision = boundary switch
        {
            QualifierBoundary.Feeder1Feeder2 => FeederDivision.Second,
            QualifierBoundary.Feeder2Feeder3 => FeederDivision.Third,
            _ => throw new InvalidOperationException($"Unexpected feeder boundary {boundary}."),
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
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} is missing the {boundary} leagues for color {sportingColor}.");
        }

        _ = rules;
        return (upper.Id, lower.Id);
    }

    internal static async Task ValidatePersistedAsync(
        SaveDbContext context,
        QualifierState state,
        QualifierSimulation simulation,
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

        HashSet<int> fieldIds = simulation.Field.All.Select(p => p.SaveAthleteId).ToHashSet();
        HashSet<int> standingIds = standings.Select(s => s.SaveAthleteId).ToHashSet();
        if (!fieldIds.SetEquals(standingIds))
        {
            throw new InvalidOperationException("Feeder qualifier standings do not match the qualifier field.");
        }
    }

    internal static async Task EmitStoriesAsync(
        SaveDbContext context,
        QualifierState state,
        QualifierSimulation simulation,
        CancellationToken cancellationToken)
    {
        Dictionary<int, FeederQualifierFieldSelection.FeederPick> picksByAthlete =
            simulation.Field.All.ToDictionary(p => p.SaveAthleteId);
        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> nextByAthlete = await context.SeasonMemberships
            .Where(e => e.SeasonId == state.Next.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, cancellationToken)
            .ConfigureAwait(false);
        string movementDedup = Features.Stories.StoryEventEmitter.MovementDedup(
            state.Source.SeasonNumber, state.Next.SeasonNumber);
        bool emitted = await EmitRankedStoriesAsync(
            context, state, simulation, picksByAthlete, leaguesById, nextByAthlete, movementDedup, cancellationToken).ConfigureAwait(false);

        if (emitted)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> EmitRankedStoriesAsync(
        SaveDbContext context,
        QualifierState state,
        QualifierSimulation simulation,
        Dictionary<int, FeederQualifierFieldSelection.FeederPick> picksByAthlete,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        string movementDedup,
        CancellationToken cancellationToken)
    {
        bool emitted = false;
        foreach (StageRankedAthlete entry in simulation.Ranked.OrderBy(r => r.StageRank))
        {
            emitted |= await TryEmitQualifierStoryAsync(
                context, state, entry, picksByAthlete, leaguesById, nextByAthlete, movementDedup, cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    private static async Task<bool> TryEmitQualifierStoryAsync(
        SaveDbContext context,
        QualifierState state,
        StageRankedAthlete entry,
        Dictionary<int, FeederQualifierFieldSelection.FeederPick> picksByAthlete,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        string movementDedup,
        CancellationToken cancellationToken)
    {
        if (!picksByAthlete.TryGetValue(entry.AthleteId, out FeederQualifierFieldSelection.FeederPick? pick))
        {
            throw new InvalidOperationException($"Feeder qualifier standing for '{entry.Name}' has no provenance.");
        }

        bool qualified = entry.StageRank <= state.Rules.FeederQualifierWinners;
        leaguesById.TryGetValue(pick.FromLeagueId, out LeagueEntity? from);
        string fromName = from?.Name ?? $"League {pick.FromLeagueId}";
        nextByAthlete.TryGetValue(entry.AthleteId, out SeasonMembershipEntity? nextMembership);
        string toName = nextMembership?.LeagueId is not null && leaguesById.TryGetValue(nextMembership.LeagueId.Value, out LeagueEntity? to)
            ? to.Name
            : fromName;
        string eventType = qualified && pick.Role == QualifierRole.Challenger
            ? Features.Stories.StoryEventType.Promotion
            : !qualified && pick.Role == QualifierRole.Incumbent
                ? Features.Stories.StoryEventType.Relegation
                : string.Empty;
        if (string.IsNullOrEmpty(eventType))
        {
            return false;
        }

        return await Features.Stories.StoryEventEmitter.TryEmitAsync(
            context,
            entry.AthleteId,
            eventType,
            movementDedup,
            state.Next.SeasonNumber,
            null,
            new Features.Stories.StoryEventPayload(
                entry.Name,
                state.Next.SeasonNumber,
                FromLeagueName: fromName,
                ToLeagueName: toName,
                FromSeasonNumber: state.Source.SeasonNumber,
                ToSeasonNumber: state.Next.SeasonNumber,
                FromSeasonRank: pick.FromSeasonRank,
                ViaQualifier: true),
            cancellationToken).ConfigureAwait(false);
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

    internal static async Task EnsureNoPartialAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next,
        QualifierBoundary boundary, int sportingColor, CancellationToken cancellationToken)
    {
        bool hasRounds = await context.QualifierRounds.AnyAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == sportingColor,
            cancellationToken).ConfigureAwait(false);
        if (hasRounds)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier {boundary} color {sportingColor} has partial rounds without standings; sporting state is corrupt.");
        }
    }

    internal static async Task EnsureCanonicalOrderAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next,
        QualifierBoundary boundary, int sportingColor, CancellationToken cancellationToken)
    {
        bool superResolved = await context.QualifierStandings.AnyAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)QualifierBoundary.Superleague,
            cancellationToken).ConfigureAwait(false);
        if (!superResolved)
        {
            throw new RunFeederQualifierConflictException(
                "The Superleague qualifier must be resolved before feeder qualifiers can run (canonical order).");
        }

        if (boundary != QualifierBoundary.Feeder2Feeder3)
        {
            return;
        }

        await EnsureAllF1F2ResolvedAsync(context, source, next, cancellationToken).ConfigureAwait(false);
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
            _ => throw new InvalidOperationException($"Unexpected feeder boundary {boundary}."),
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
            throw new InvalidOperationException(
                $"Season {source.SeasonNumber} is missing the {boundary} leagues for color {sportingColor}.");
        }

        _ = rules;
        return (upper, lower);
    }

    internal static async Task<List<SeasonStandingEntity>> LoadLeagueStandingsAsync(
        SaveDbContext context, SeasonEntity source, LeagueEntity league, RulesV1 rules, CancellationToken cancellationToken)
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

        return rows;
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

    internal static async Task<Dictionary<int, string>> LoadAthleteNamesAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, Bonus>> LoadActiveBonusesAsync(
        SaveDbContext context,
        List<AdvanceRoundHandler.MemberRow> roster,
        SeasonEntity next,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, int> seasonNumbers = await AdvanceRoundHandler.LoadSeasonNumbersAsync(context, next, cancellationToken).ConfigureAwait(false);
        if (!seasonNumbers.TryGetValue(next.Id, out int nextSeasonNumber))
        {
            throw new InvalidOperationException($"Save has no season row for season {next.SeasonNumber}.");
        }

        List<StageStandingEntity> standings = await AdvanceRoundHandler.LoadBonusStandingsAsync(
            context, roster, AdvanceRoundHandler.SelectBonusSeasonIds(seasonNumbers, nextSeasonNumber), cancellationToken).ConfigureAwait(false);
        Dictionary<int, List<BonusContribution>> contributions = AdvanceRoundHandler.GroupBonusContributions(
            roster, standings, seasonNumbers, nextSeasonNumber);
        return AdvanceRoundHandler.ComputeStageStartBonuses(roster, contributions, nextSeasonNumber, 1, rules);
    }

    internal static async Task<RunFeederQualifierResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        QualifierState state,
        QualifierSimulation simulation,
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
