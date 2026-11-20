using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Qualifiers.Engine;

/// <summary>
/// The one qualifier engine (MSS-150) serving both the Superleague promotion
/// qualifier and the feeder boundary qualifiers, so future tie-break or rule
/// changes can never again apply to one pipeline but not the other. One
/// implementation, two parameterizations through
/// <see cref="QualifierEngineState"/> (field size 32/16, winners, ranking
/// level, boundary identity) plus <see cref="QualifierEngineHooks"/> for the
/// behavior that genuinely differs per path. Round simulation, accumulation,
/// ranking, checksums, payload persistence, movement-story emission and the
/// completion spine (RNG committing with results in the caller's transaction)
/// are shared; each path's persisted bytes are unchanged. Not a Shared/
/// abstraction: this is postseason-qualifier machinery owned by the
/// Qualifiers area, which both pipelines already share. No MediatR, no
/// repositories; the kernel stays pure and untouched.
/// </summary>
internal static class QualifierEngine
{
    /// <summary>
    /// Fingerprints qualifier standings: lowercase hex SHA-256 over lines of
    /// <c>Rank:AthleteId:Name:Score:Base</c> in rank order.
    /// SHA-256 is a content fingerprint here, not sporting randomness.
    /// Championship/earned values are intentionally excluded: qualifiers
    /// persist no championship points and no new bonus.
    /// </summary>
    internal static string ComputeChecksum(IReadOnlyList<StageRankedAthlete> ranked)
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

    /// <summary>
    /// Builds the name-sorted simulation roster from the field provenance.
    /// Deterministic input order is mandatory: the kernel shuffle consumes the
    /// roster in this order.
    /// </summary>
    internal static List<AdvanceRoundHandler.MemberRow> BuildRoster(IReadOnlyList<QualifierProvenance> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        List<AdvanceRoundHandler.MemberRow> roster = new(field.Count);
        foreach (QualifierProvenance pick in field)
        {
            roster.Add(new AdvanceRoundHandler.MemberRow(pick.SaveAthleteId, pick.Name, pick.SportingColor));
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (AdvanceRoundHandler.MemberRow row in roster)
        {
            if (!names.Add(row.Name))
            {
                throw new SaveCorruptionException($"Qualifier field contains duplicate athlete '{row.Name}'.");
            }
        }

        roster.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return roster;
    }

    /// <summary>
    /// Loads the stage-start active bonus for the qualifier roster at the
    /// next-season Stage 1 boundary. Role never branches bonus math: every
    /// qualifier athlete uses that boundary, so source-season Stage 32 bonus
    /// enters at 80% decay like any next-season opener.
    /// </summary>
    internal static async Task<Dictionary<int, Bonus>> LoadStageStartBonusesAsync(
        SaveDbContext context,
        List<AdvanceRoundHandler.MemberRow> roster,
        SeasonEntity next,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        Dictionary<int, int> seasonNumbers = await AdvanceRoundHandler.LoadSeasonNumbersAsync(context, next, cancellationToken).ConfigureAwait(false);
        if (!seasonNumbers.TryGetValue(next.Id, out int nextSeasonNumber))
        {
            throw new SaveCorruptionException($"Save has no season row for season {next.SeasonNumber}.");
        }

        List<StageStandingEntity> standings = await AdvanceRoundHandler.LoadBonusStandingsAsync(
            context, roster, AdvanceRoundHandler.SelectBonusSeasonIds(seasonNumbers, nextSeasonNumber), cancellationToken).ConfigureAwait(false);
        Dictionary<int, List<BonusContribution>> contributions = AdvanceRoundHandler.GroupBonusContributions(
            roster, standings, seasonNumbers, nextSeasonNumber);
        return AdvanceRoundHandler.ComputeStageStartBonuses(roster, contributions, nextSeasonNumber, 1, rules);
    }

    /// <summary>
    /// Ensures the Chaos Cup is complete for the source season, mapping its
    /// retryable incompleteness onto the calling pipeline's conflict
    /// exception type; corruption passes through uncaught.
    /// </summary>
    internal static async Task EnsureChaosCompleteAsync(
        SaveDbContext context,
        SeasonEntity source,
        RulesV1 rules,
        Func<string, Exception> conflict,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(conflict);
        try
        {
            await Features.Cups.ChaosCup.ChaosCupLifecycle.EnsureCompleteAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        }
        catch (Features.Cups.ChaosCup.SavePredatesChaosActivationException ex)
        {
            throw conflict(ex.Message);
        }
        catch (Features.Cups.ChaosCup.ChaosCupIncompleteException ex)
        {
            // Genuine retryable postseason signal; corruption passes through uncaught.
            throw conflict(ex.Message);
        }
    }

    /// <summary>
    /// Loads one source league's final standings. The Superleague path
    /// additionally requires rank coverage (<paramref name="requireCompleteRanks"/>);
    /// feeder paths validate count only, exactly as before the unification.
    /// </summary>
    internal static async Task<List<SeasonStandingEntity>> LoadLeagueStandingsAsync(
        SaveDbContext context,
        SeasonEntity source,
        LeagueEntity league,
        RulesV1 rules,
        bool requireCompleteRanks,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == league.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count != rules.LeagueSize)
        {
            throw new SaveCorruptionException(
                $"League '{league.Name}' must have exactly {rules.LeagueSize} final standings, was {rows.Count}.");
        }

        if (requireCompleteRanks)
        {
            HashSet<int> ranks = rows.Select(r => r.SeasonRank).ToHashSet();
            if (!ranks.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
            {
                throw new SaveCorruptionException($"League '{league.Name}' must cover ranks 1..{rules.LeagueSize} exactly once.");
            }
        }

        return rows;
    }

    /// <summary>
    /// Loads all source-season memberships, validating the save-wide athlete
    /// count.
    /// </summary>
    internal static async Task<List<SeasonMembershipEntity>> LoadSourceMembershipsAsync(
        SaveDbContext context, SeasonEntity source, RulesV1 rules, CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != rules.TotalAthletesInSave)
        {
            throw new SaveCorruptionException(
                $"Season {source.SeasonNumber} must have exactly {rules.TotalAthletesInSave} memberships, was {memberships.Count}.");
        }

        return memberships;
    }

    /// <summary>Loads every save athlete's id-to-name map.</summary>
    internal static async Task<Dictionary<int, string>> LoadAthleteNamesAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Simulates exactly one qualifier round from <paramref name="rngBefore"/>.</summary>
    internal static QualifierRoundPayload PlayRound(
        QualifierEngineState state,
        IReadOnlyList<QualifierRoundPayload> played,
        Pcg32State rngBefore,
        Action<QualifierRoundPayload, QualifierEngineState, Pcg32State> validateRound)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(played);
        ArgumentNullException.ThrowIfNull(validateRound);
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
            state.Roster, cumulative, state.ActiveBonuses, rngBefore, state.Rules, state.FieldSize);
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
        validateRound(payload, state, rngBefore);
        return payload;
    }

    /// <summary>
    /// Simulates one round for an explicit field size. With
    /// <c>fieldSize == LeagueSize</c> this is byte-identical to the league-scale
    /// <c>RoundSimulator.Simulate</c> the Superleague qualifier used before
    /// the unification, so persisted outcomes are unchanged.
    /// </summary>
    internal static RoundSimulationResult SimulateRoundWithFieldSize(
        List<AdvanceRoundHandler.MemberRow> roster,
        Dictionary<int, Points> cumulativeBefore,
        Dictionary<int, Bonus> activeBonuses,
        Pcg32State rngBefore,
        RulesV1 rules,
        int fieldSize)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(cumulativeBefore);
        ArgumentNullException.ThrowIfNull(activeBonuses);
        ArgumentNullException.ThrowIfNull(rules);
        List<RoundAthleteInput> inputs = new(roster.Count);
        foreach (AdvanceRoundHandler.MemberRow row in roster)
        {
            Points before = cumulativeBefore.TryGetValue(row.AthleteId, out Points value) ? value : Points.Zero;
            if (!activeBonuses.TryGetValue(row.AthleteId, out Bonus bonus))
            {
                throw new SaveCorruptionException($"Missing stage-start bonus for athlete '{row.Name}'.");
            }

            inputs.Add(new RoundAthleteInput(row.AthleteId, row.Name, bonus, before));
        }

        Pcg32V1 rng = Pcg32V1.Restore(rngBefore);
        return RoundSimulator.SimulateWithFieldSize(inputs, rng, rules, fieldSize);
    }

    /// <summary>
    /// Completes the qualifier from all payloads (the source of truth) in the
    /// caller's transaction, exactly as both pipelines always have: ranking
    /// with the tie-break RNG restored from the last round's RNG-after, newly
    /// played round rows, standings, the path's own next-roster application,
    /// the RNG row (committed with the results in this transaction), the
    /// path's own persisted-state validation, story emission and the
    /// persisted phase. Order and transaction boundaries are unchanged.
    /// </summary>
    internal static async Task<QualifierEngineSimulation> CompleteAsync(
        SaveDbContext context,
        QualifierEngineState state,
        List<QualifierRoundPayload> payloads,
        QualifierEngineHooks hooks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(hooks);
        QualifierEngineSimulation simulation = Finish(state, payloads, hooks.ValidateCompleted);
        await PersistAsync(context, state, simulation, cancellationToken).ConfigureAwait(false);
        await hooks.ApplyToNextRosterAsync(context, state, simulation, cancellationToken).ConfigureAwait(false);
        context.ApplyRngState(simulation.RngAfter);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await hooks.ValidatePersistedAsync(context, state, simulation, cancellationToken).ConfigureAwait(false);
        await EmitStoriesAsync(context, state, simulation, hooks, cancellationToken).ConfigureAwait(false);
        await ReconcilePhaseAsync(context, state, cancellationToken).ConfigureAwait(false);

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return simulation;
    }

    /// <summary>
    /// Ranks the completed qualifier from the persisted payloads: accumulate
    /// stage totals, rank with the seeded tie-break RNG, validate through the
    /// path hook and fingerprint the standings. One ranking call, two
    /// parameterizations; the Superleague path passes
    /// <c>(LeagueLevel.Feeder1, 32)</c> — the qualifier awards no new bonus,
    /// so the kernel's earned value at the baseline Feeder 1 scale is ignored
    /// by that path's invariants — and feeder boundaries pass
    /// <c>(their higher tier, 16)</c>.
    /// </summary>
    internal static QualifierEngineSimulation Finish(
        QualifierEngineState state,
        List<QualifierRoundPayload> payloads,
        Action<IReadOnlyList<StageRankedAthlete>, IReadOnlyList<StageAthleteTotals>, RulesV1> validateCompleted)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(validateCompleted);
        List<List<StageRoundEntry>> roundEntries = BuildRoundEntries(payloads);
        IReadOnlyList<StageAthleteTotals> totals = StageCalculator.AccumulateWithFieldSize(
            roundEntries, state.Rules, state.FieldSize, state.TotalRounds);
        QualifierRoundPayload last = payloads[^1];
        Pcg32V1 tieBreakRng = Pcg32V1.Restore(new Pcg32State(last.RngAfterState, last.RngAfterStream));
        IReadOnlyList<StageRankedAthlete> ranked = StageCalculator.RankWithFieldSize(
            totals, tieBreakRng, state.Rules, state.RankingLevel, state.FieldSize);
        validateCompleted(ranked, totals, state.Rules);
        Pcg32State rngAfter = tieBreakRng.Snapshot();
        string checksum = ComputeChecksum(ranked);
        return new QualifierEngineSimulation(state.Field, payloads, totals, ranked, rngAfter, checksum);
    }

    /// <summary>
    /// Persists the newly played round rows and the full standings inside the
    /// caller's transaction. Event identity (boundary/storage color) comes from
    /// the state; qualification uses the path's own winners count. Round
    /// payloads keep each path's exact persisted bytes.
    /// </summary>
    internal static async Task PersistAsync(
        SaveDbContext context,
        QualifierEngineState state,
        QualifierEngineSimulation simulation,
        CancellationToken cancellationToken)
    {
        foreach (QualifierRoundPayload payload in simulation.Payloads.Skip(state.Played.Count))
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

        Dictionary<int, QualifierProvenance> picksByAthlete =
            simulation.Field.ToDictionary(p => p.SaveAthleteId);
        foreach (StageRankedAthlete entry in simulation.Ranked)
        {
            if (!picksByAthlete.TryGetValue(entry.AthleteId, out QualifierProvenance? pick))
            {
                throw new SaveCorruptionException($"Qualifier standing for '{entry.Name}' has no provenance.");
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
                IsQualified = entry.StageRank <= state.Winners,
                Role = (int)pick.Role,
                FromLeagueId = pick.FromLeagueId,
                FromSeasonRank = pick.FromSeasonRank,
                SportingColor = pick.SportingColor,
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits qualifier-decided movement stories transactionally with the
    /// result: promotion for qualified challengers, relegation for failed
    /// incumbents. Idempotent via dedup keys; retained athletes emit nothing.
    /// Shared by both paths; the Superleague path additionally emits
    /// first-appearance stories through its hook.
    /// </summary>
    internal static async Task<bool> EmitMovementStoriesAsync(
        SaveDbContext context,
        QualifierEngineState state,
        QualifierEngineSimulation simulation,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, SeasonMembershipEntity> nextByAthlete,
        CancellationToken cancellationToken)
    {
        Dictionary<int, QualifierProvenance> picksByAthlete =
            simulation.Field.ToDictionary(p => p.SaveAthleteId);
        string movementDedup = Features.Stories.StoryEventEmitter.MovementDedup(
            state.Source.SeasonNumber, state.Next.SeasonNumber);
        bool emitted = false;
        foreach (StageRankedAthlete entry in simulation.Ranked.OrderBy(r => r.StageRank))
        {
            if (!picksByAthlete.TryGetValue(entry.AthleteId, out QualifierProvenance? pick))
            {
                throw new SaveCorruptionException($"Qualifier standing for '{entry.Name}' has no provenance.");
            }

            bool qualified = entry.StageRank <= state.Winners;
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
                continue;
            }

            emitted |= await Features.Stories.StoryEventEmitter.TryEmitAsync(
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

        return emitted;
    }

    private static async Task EmitStoriesAsync(
        SaveDbContext context,
        QualifierEngineState state,
        QualifierEngineSimulation simulation,
        QualifierEngineHooks hooks,
        CancellationToken cancellationToken)
    {
        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> nextByAthlete = await context.SeasonMemberships
            .Where(e => e.SeasonId == state.Next.Id)
            .ToDictionaryAsync(e => e.SaveAthleteId, cancellationToken)
            .ConfigureAwait(false);
        bool emitted = await EmitMovementStoriesAsync(
            context, state, simulation, leaguesById, nextByAthlete, cancellationToken).ConfigureAwait(false);
        if (hooks.EmitExtraStoriesAsync is not null)
        {
            emitted |= await hooks.EmitExtraStoriesAsync(
                context, state, simulation, leaguesById, nextByAthlete, cancellationToken).ConfigureAwait(false);
        }

        if (emitted)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReconcilePhaseAsync(
        SaveDbContext context,
        QualifierEngineState state,
        CancellationToken cancellationToken)
    {
        QualifierProgress progress = await QualifierProgressEvaluator
            .LoadAsync(context, state.Source, state.Next, state.Rules, cancellationToken).ConfigureAwait(false);
        if (progress.IsComplete
            && Features.Saves.SavePhaseParser.Parse(state.Metadata.Phase) < Features.Saves.SavePhase.QualifierResolved)
        {
            state.Metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.QualifierResolved);
        }
    }

    private static IReadOnlyList<RoundPayloadEntry> ToPayloadEntries(RoundSimulationResult simulation)
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

    private static List<List<StageRoundEntry>> BuildRoundEntries(IReadOnlyList<QualifierRoundPayload> payloads)
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

        return roundEntries;
    }
}
