using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.GlobalStage;
using MtgSoloSports.Features.Simulation.SeasonCompletion;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Simulation.CompleteStage;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Completes the currently legal
/// league stage: simulates any remaining legal rounds with the identical RNG and
/// mathematics as sequential <c>AdvanceRound</c> operations, accumulates 16-round
/// stage scores, ranks with deterministic tie-breaks, persists 32
/// <c>StageStanding</c> rows with championship points and pending bonus, and
/// creates the next stage cursor. Round and stage bonus earned during the stage
/// stays pending and activates only at the next stage boundary; Stage 32 bonus
/// therefore first becomes usable in the next season at 80% decay.
/// Fast-stage atomicity: all remaining rounds plus standings plus RNG-after
/// commit in one transaction. Presentation DTOs are built from persisted facts
/// so replay never resimulates.
/// </summary>
public sealed class CompleteStageHandler
{
    private readonly SaveStore _store;

    public CompleteStageHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<CompleteStageResponse> HandleAsync(Guid saveId, int leagueId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (leagueId <= 0)
        {
            throw new ArgumentException("League id must be positive.", nameof(leagueId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CompleteUnderLockAsync(saveId, leagueId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<CompleteStageResponse> CompleteUnderLockAsync(Guid saveId, int leagueId, CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);

        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        StageCompletionContext completion = await LoadCompletionContextAsync(context, saveId, leagueId, cancellationToken).ConfigureAwait(false);
        await EnsureStageIncompleteAsync(context, completion, cancellationToken).ConfigureAwait(false);
        await EnsureGlobalStageLegalAsync(context, completion, cancellationToken).ConfigureAwait(false);

        List<RoundEntity> existingRounds = await LoadExistingRoundsAsync(context, completion, cancellationToken).ConfigureAwait(false);
        ValidateStageCursor(completion.Stage, existingRounds, completion.Rules, completion.League);
        Dictionary<int, Bonus> activeBonuses = await AdvanceRoundHandler.LoadStageStartActiveBonusesAsync(
            context, completion.Roster, completion.Season, completion.Stage, completion.Rules, cancellationToken).ConfigureAwait(false);

        Pcg32State rngAfterRounds = completion.RngBefore;
        StageRounds stageRounds = await EnsureSixteenRoundsAsync(
            context, completion, existingRounds, activeBonuses, rngAfterRounds, cancellationToken).ConfigureAwait(false);

        Pcg32V1 tieBreakRng = Pcg32V1.Restore(stageRounds.RngAfterRounds);
        IReadOnlyList<StageRankedAthlete> ranked = RankCompletedStage(completion, stageRounds, tieBreakRng);
        StageInvariants.ValidateCompletedStage(ranked, stageRounds.Totals, completion.Rules, completion.IsSuperleague);

        Pcg32State rngAfterStage = tieBreakRng.Snapshot();
        int? nextStageNumber = await PersistCompletionAsync(
            context, completion, stageRounds, ranked, rngAfterStage, cancellationToken).ConfigureAwait(false);

        Pcg32State rngAfter = await MaybeFinalizeSeasonAsync(
            context, completion, rngAfterStage, cancellationToken).ConfigureAwait(false);

        SyncLifecyclePhase(completion);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return BuildResponse(completion, ranked, nextStageNumber, rngAfter);
    }

    /// <summary>
    /// Keeps the persisted lifecycle phase in sync with the season-complete flag
    /// in the same transaction as the stage result. Regular stages stay
    /// SeasonInProgress; the transaction that finalizes Stage 32 for every
    /// active league moves the save to SeasonComplete for the postseason chain.
    /// </summary>
    internal static void SyncLifecyclePhase(StageCompletionContext completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        completion.Metadata.Phase = completion.Season.IsComplete
            ? Saves.SavePhaseParser.ToText(Saves.SavePhase.SeasonComplete)
            : Saves.SavePhaseParser.ToText(Saves.SavePhase.SeasonInProgress);
    }

    /// <summary>
    /// Enforces synchronous global progression: a league may only complete the
    /// current global stage. Leagues within the stage complete one by one;
    /// Stage N+1 cannot begin until Stage N is complete for every active league.
    /// </summary>
    internal static async Task EnsureGlobalStageLegalAsync(
        SaveDbContext context,
        StageCompletionContext completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(completion);

        GlobalStageGate.GlobalStageView global = await GlobalStageGate
            .LoadGlobalStageAsync(context, completion.Season, completion.Rules, cancellationToken)
            .ConfigureAwait(false);
        if (!GlobalStageGate.IsStageLegal(completion.Stage.StageNumber, global))
        {
            throw new CompleteStageConflictException(
                GlobalStageGate.BuildBlockedMessage(completion.League.Name, completion.Stage.StageNumber, global));
        }
    }

    /// <summary>
    /// Finalizes the season when the completed stage is Stage 32 and every
    /// active league has now completed it. Season standings (including each
    /// feeder champion) plus the season-complete flag and RNG-after state
    /// commit in the same transaction as the stage result.
    /// </summary>
    internal static async Task<Pcg32State> MaybeFinalizeSeasonAsync(
        SaveDbContext context,
        StageCompletionContext completion,
        Pcg32State rngAfterStage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.Stage.StageNumber != completion.Rules.StagesPerSeason)
        {
            return rngAfterStage;
        }

        Pcg32V1 rng = Pcg32V1.Restore(rngAfterStage);
        (bool finalized, Pcg32State rngAfter) = await SeasonFinalizer
            .TryFinalizeSeasonAsync(context, completion.Season, completion.Rules, rng, cancellationToken)
            .ConfigureAwait(false);
        return finalized ? rngAfter : rngAfterStage;
    }

    internal sealed record StageCompletionContext(
        Guid SaveId,
        SaveMetadataEntity Metadata,
        RulesV1 Rules,
        Pcg32State RngBefore,
        SeasonEntity Season,
        LeagueEntity League,
        List<AdvanceRoundHandler.MemberRow> Roster,
        StageEntity Stage,
        bool IsSuperleague);

    internal sealed record StageRounds(
        List<RoundPayloadDocument> Payloads,
        IReadOnlyList<StageAthleteTotals> Totals,
        Pcg32State RngAfterRounds);

    internal static async Task<StageCompletionContext> LoadCompletionContextAsync(
        SaveDbContext context,
        Guid saveId,
        int leagueId,
        CancellationToken cancellationToken)
    {
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = rngRow.ToState();
        SeasonEntity season = await AdvanceRoundHandler.LoadSeasonAsync(context, metadata, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await AdvanceRoundHandler.LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);
        List<AdvanceRoundHandler.MemberRow> roster = await AdvanceRoundHandler.LoadRosterAsync(context, season, league, rules, cancellationToken).ConfigureAwait(false);
        StageEntity stage = await LoadCurrentStageAsync(context, season, league, rules, cancellationToken).ConfigureAwait(false);
        bool isSuperleague = league.Kind == (int)LeagueKind.Superleague;
        return new StageCompletionContext(saveId, metadata, rules, rngBefore, season, league, roster, stage, isSuperleague);
    }

    /// <summary>
    /// Loads the league's current stage cursor for completion: the highest
    /// stage number, even when it already holds 16 rounds awaiting finalization.
    /// Unlike round advancement (which rejects a full stage), completion must be
    /// able to finalize those 16 rounds, so a full-but-unfinalized stage is
    /// returned here and rejected later only when standings already exist.
    /// </summary>
    internal static async Task<StageEntity> LoadCurrentStageAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(rules);

        List<StageEntity> stages = await context.Stages
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.StageNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (stages.Count == 0)
        {
            StageEntity created = new()
            {
                SeasonId = season.Id,
                LeagueId = league.Id,
                StageNumber = 1,
                CompletedRounds = 0,
                IsComplete = false,
            };
            context.Stages.Add(created);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }

        foreach (StageEntity stage in stages)
        {
            if (stage.StageNumber < 1 || stage.StageNumber > rules.StagesPerSeason)
            {
                throw new InvalidOperationException($"League '{league.Name}' has corrupt stage {stage.StageNumber}.");
            }

            if (stage.CompletedRounds < 0 || stage.CompletedRounds > rules.RoundsPerStage)
            {
                throw new InvalidOperationException($"League '{league.Name}' stage {stage.StageNumber} has corrupt completed-round count {stage.CompletedRounds}.");
            }
        }

        int distinct = stages.Select(s => s.StageNumber).Distinct().Count();
        if (distinct != stages.Count)
        {
            throw new InvalidOperationException($"League '{league.Name}' has duplicate stage rows.");
        }

        return stages.OrderByDescending(s => s.StageNumber).First();
    }

    internal static async Task EnsureStageIncompleteAsync(
        SaveDbContext context,
        StageCompletionContext completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(completion);

        if (completion.Stage.IsComplete)
        {
            throw new CompleteStageConflictException(
                $"Stage {completion.Stage.StageNumber} for league '{completion.League.Name}' is already complete.");
        }

        bool hasStandings = await context.StageStandings.AnyAsync(
            e => e.SeasonId == completion.Season.Id &&
                e.LeagueId == completion.League.Id &&
                e.StageNumber == completion.Stage.StageNumber,
            cancellationToken).ConfigureAwait(false);
        if (hasStandings)
        {
            throw new CompleteStageConflictException(
                $"Stage {completion.Stage.StageNumber} for league '{completion.League.Name}' is already complete.");
        }
    }

    internal static async Task<List<RoundEntity>> LoadExistingRoundsAsync(
        SaveDbContext context,
        StageCompletionContext completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(completion);

        return await context.Rounds
            .Where(e => e.SeasonId == completion.Season.Id &&
                e.LeagueId == completion.League.Id &&
                e.StageNumber == completion.Stage.StageNumber)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static void ValidateStageCursor(
        StageEntity stage,
        List<RoundEntity> existingRounds,
        RulesV1 rules,
        LeagueEntity league)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(existingRounds);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(league);

        if (stage.StageNumber < 1 || stage.StageNumber > rules.StagesPerSeason)
        {
            throw new InvalidOperationException($"League '{league.Name}' has corrupt stage {stage.StageNumber}.");
        }

        if (stage.CompletedRounds < 0 || stage.CompletedRounds > rules.RoundsPerStage)
        {
            throw new InvalidOperationException($"League '{league.Name}' stage {stage.StageNumber} has corrupt completed-round count {stage.CompletedRounds}.");
        }

        if (stage.CompletedRounds != existingRounds.Count)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' stage {stage.StageNumber} cursor says {stage.CompletedRounds} rounds but {existingRounds.Count} round rows exist.");
        }

        for (int i = 0; i < existingRounds.Count; i++)
        {
            if (existingRounds[i].RoundNumber != i + 1)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {stage.StageNumber} has non-sequential round rows; rounds must advance in order.");
            }
        }
    }

    internal static async Task<StageRounds> EnsureSixteenRoundsAsync(
        SaveDbContext context,
        StageCompletionContext completion,
        List<RoundEntity> existingRounds,
        Dictionary<int, Bonus> activeBonuses,
        Pcg32State rngBefore,
        CancellationToken cancellationToken)
    {
        List<RoundPayloadDocument> payloads = LoadPayloads(existingRounds, completion);
        Dictionary<int, Points> cumulative = BuildCumulativeFromPayloads(payloads, completion.Roster);
        Pcg32State current = rngBefore;

        for (int roundNumber = existingRounds.Count + 1; roundNumber <= completion.Rules.RoundsPerStage; roundNumber++)
        {
            (RoundPayloadDocument payload, RoundSimulationResult simulation) = SimulateSingleRound(completion, activeBonuses, cumulative, current, roundNumber);
            _ = AdvanceRoundHandler.PersistRound(
                context, completion.Season, completion.League, completion.Stage, roundNumber, completion.Rules, current, simulation, payload);
            completion.Stage.CompletedRounds = roundNumber;
            foreach (RoundPayloadEntry entry in payload.Placements)
            {
                cumulative[entry.AthleteId] = Points.FromThousandths(entry.CumulativeAfterThousandths);
            }

            payloads.Add(payload);
            current = simulation.RngAfter;
            cancellationToken.ThrowIfCancellationRequested();
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        List<List<StageRoundEntry>> roundEntries = BuildRoundEntries(payloads);
        IReadOnlyList<StageAthleteTotals> totals = StageCalculator.Accumulate(roundEntries, completion.Rules);
        return new StageRounds(payloads, totals, current);
    }

    /// <summary>
    /// Simulates one round with the identical call sequence as
    /// <c>AdvanceRound</c>: stage-start active bonus held constant, cumulative
    /// chaining, one deterministic shuffle from the save RNG state.
    /// Returns the payload plus the simulation (including RNG-after).
    /// </summary>
    internal static (RoundPayloadDocument Payload, RoundSimulationResult Simulation) SimulateSingleRound(
        StageCompletionContext completion,
        Dictionary<int, Bonus> activeBonuses,
        Dictionary<int, Points> cumulative,
        Pcg32State rngBefore,
        int roundNumber)
    {
        RoundSimulationResult simulation = AdvanceRoundHandler.SimulateRound(
            completion.Roster, cumulative, activeBonuses, rngBefore, completion.Rules);
        RoundPayloadDocument payload = AdvanceRoundHandler.BuildPayload(
            completion.Season, completion.League, completion.Stage, roundNumber, completion.Rules, rngBefore, simulation);
        RoundInvariants.ValidateSimulation(payload, completion.Rules, rngBefore.State, rngBefore.Stream);
        return (payload, simulation);
    }

    internal static List<RoundPayloadDocument> LoadPayloads(List<RoundEntity> existingRounds, StageCompletionContext completion)
    {
        List<RoundPayloadDocument> payloads = new(existingRounds.Count);
        foreach (RoundEntity round in existingRounds)
        {
            RoundPayloadDocument document = RoundPayloadCodec.DecodeRound(round.PayloadJson);
            if (document.StageNumber != completion.Stage.StageNumber)
            {
                throw new InvalidOperationException($"Persisted round {round.Id} has corrupt stage identity.");
            }

            payloads.Add(document);
        }

        return payloads;
    }

    internal static Dictionary<int, Points> BuildCumulativeFromPayloads(
        List<RoundPayloadDocument> payloads,
        List<AdvanceRoundHandler.MemberRow> roster)
    {
        Dictionary<int, Points> cumulative = new(roster.Count);
        if (payloads.Count == 0)
        {
            return cumulative;
        }

        RoundPayloadDocument last = payloads[^1];
        foreach (RoundPayloadEntry entry in last.Placements)
        {
            cumulative[entry.AthleteId] = Points.FromThousandths(entry.CumulativeAfterThousandths);
        }

        return cumulative;
    }

    internal static List<List<StageRoundEntry>> BuildRoundEntries(List<RoundPayloadDocument> payloads)
    {
        List<List<StageRoundEntry>> roundEntries = new(payloads.Count);
        foreach (RoundPayloadDocument payload in payloads)
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

    internal static IReadOnlyList<StageRankedAthlete> RankCompletedStage(
        StageCompletionContext completion,
        StageRounds stageRounds,
        Pcg32V1 rng)
    {
        return StageCalculator.Rank(stageRounds.Totals, rng, completion.Rules, completion.IsSuperleague);
    }

    internal static async Task<int?> PersistCompletionAsync(
        SaveDbContext context,
        StageCompletionContext completion,
        StageRounds stageRounds,
        IReadOnlyList<StageRankedAthlete> ranked,
        Pcg32State rngAfter,
        CancellationToken cancellationToken)
    {
        PersistStandings(context, completion, ranked);
        completion.Stage.CompletedRounds = completion.Rules.RoundsPerStage;
        completion.Stage.IsComplete = true;
        context.ApplyRngState(rngAfter);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await VerifyStandingsAsync(context, completion, ranked, cancellationToken).ConfigureAwait(false);

        await Features.Athletes.Projections.AthleteProjectionUpdater.RefreshAfterStageAsync(
            context, completion.Season, completion.League, completion.Rules, cancellationToken).ConfigureAwait(false);

        int? nextStageNumber = CreateNextStage(context, completion);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return nextStageNumber;
    }

    internal static void PersistStandings(
        SaveDbContext context,
        StageCompletionContext completion,
        IReadOnlyList<StageRankedAthlete> ranked)
    {
        foreach (StageRankedAthlete entry in ranked)
        {
            context.StageStandings.Add(new StageStandingEntity
            {
                SeasonId = completion.Season.Id,
                LeagueId = completion.League.Id,
                StageId = completion.Stage.Id,
                StageNumber = completion.Stage.StageNumber,
                SaveAthleteId = entry.AthleteId,
                StageRank = entry.StageRank,
                StageScoreThousandths = entry.StageScoreThousandths,
                BaseScoreThousandths = entry.BaseScoreThousandths,
                ChampionshipPointsThousandths = entry.ChampionshipPointsThousandths,
                RoundWins = entry.RoundWins,
                RoundPlaceCountsJson = JsonSerializer.Serialize(entry.RoundPlaceCounts),
                EarnedBonusThousandths = entry.EarnedBonusThousandths,
            });
        }
    }

    internal static async Task VerifyStandingsAsync(
        SaveDbContext context,
        StageCompletionContext completion,
        IReadOnlyList<StageRankedAthlete> ranked,
        CancellationToken cancellationToken)
    {
        int count = await context.StageStandings.CountAsync(
            e => e.SeasonId == completion.Season.Id &&
                e.LeagueId == completion.League.Id &&
                e.StageNumber == completion.Stage.StageNumber,
            cancellationToken).ConfigureAwait(false);
        if (count != completion.Rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Stage {completion.Stage.StageNumber} must persist exactly {completion.Rules.LeagueSize} standings, was {count}.");
        }

        RngStateEntity rngRow = await context.RngStates.SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        _ = rngRow.ToState();
    }

    internal static int? CreateNextStage(SaveDbContext context, StageCompletionContext completion)
    {
        if (completion.Stage.StageNumber >= completion.Rules.StagesPerSeason)
        {
            return null;
        }

        StageEntity next = new()
        {
            SeasonId = completion.Season.Id,
            LeagueId = completion.League.Id,
            StageNumber = completion.Stage.StageNumber + 1,
            CompletedRounds = 0,
            IsComplete = false,
        };
        context.Stages.Add(next);
        return next.StageNumber;
    }

    internal static CompleteStageResponse BuildResponse(
        StageCompletionContext completion,
        IReadOnlyList<StageRankedAthlete> ranked,
        int? nextStageNumber,
        Pcg32State rngAfter)
    {
        List<CompleteStageStanding> standings = new(ranked.Count);
        foreach (StageRankedAthlete entry in ranked)
        {
            standings.Add(new CompleteStageStanding(
                entry.AthleteId,
                entry.Name,
                entry.StageRank,
                entry.StageScoreThousandths,
                entry.BaseScoreThousandths,
                entry.ChampionshipPointsThousandths,
                entry.RoundWins,
                entry.EarnedBonusThousandths));
        }

        return new CompleteStageResponse(
            completion.SaveId,
            completion.Season.SeasonNumber,
            completion.League.Id,
            completion.League.Name,
            completion.Stage.StageNumber,
            completion.Rules.Version,
            completion.Rules.RoundsPerStage,
            StageInvariants.ComputeChecksum(ranked),
            completion.RngBefore.State,
            completion.RngBefore.Stream,
            rngAfter.State,
            rngAfter.Stream,
            nextStageNumber,
            standings);
    }
}
