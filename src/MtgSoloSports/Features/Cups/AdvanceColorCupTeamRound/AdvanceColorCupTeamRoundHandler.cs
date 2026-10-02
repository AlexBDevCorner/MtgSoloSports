using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupTeamLive;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Cups.AdvanceColorCupTeamRound;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Simulates exactly one
/// Color Cup team round for a completed odd source season and persists it with
/// the RNG-after state in one transaction: groups run 1..4 in order, rounds
/// 1..8 within each group. Completing a group's eighth round also ranks that
/// group's legs (consuming RNG exactly as the former atomic run did) and
/// persists them; completing group 4 round 8 additionally persists the
/// official team standings, champion honours and stories. Skipping, repeating,
/// reordering, or mixing rule versions aborts the mutation; corrupted sporting
/// state is never silently repaired. Holds one per-save lock; the live GET
/// projection never locks. Returns the authoritative live projection (re-read
/// after commit) so incremental refresh and reload agree. The separate Color
/// Cup individual event is unchanged.
/// </summary>
public sealed partial class AdvanceColorCupTeamRoundHandler
{
    private readonly SaveStore _store;

    public AdvanceColorCupTeamRoundHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ColorCupTeamLiveResponse> HandleAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AdvanceUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Advances one round assuming the per-save lock is already held. Shared by
    /// the advance endpoint and the (resume-capable) full team run so both
    /// produce byte-identical results.
    /// </summary>
    internal async Task<ColorCupTeamLiveResponse> AdvanceUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        _ = await AdvanceCoreAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        GetColorCupTeamLiveHandler live = new(_store);
        return await live.HandleAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record AdvanceOutcome(
        int SourceSeasonId,
        int CompletedRounds,
        int TotalRounds,
        RunColorCupTeamHandler.TeamSimulation? CompletedSimulation);

    internal sealed record AdvancePreparation(
        RulesV1 Rules,
        Pcg32State RngBefore,
        SeasonEntity Source,
        List<ColorCupSelectionEntity> Selection,
        Dictionary<int, List<ColorCupSelectionEntity>> Groups,
        int StageCountBefore,
        int SeasonCountBefore,
        int RoundCountBefore,
        long LifetimeBefore,
        long EffectiveBefore,
        long ChampionshipBefore);

    internal sealed record AdvanceStep(
        List<ColorCupTeamRoundEntity> Rounds,
        int GroupNumber,
        int RoundNumber,
        Pcg32State Current,
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> Rosters,
        Dictionary<int, Bonus> Bonuses);

    /// <summary>
    /// Simulates and commits exactly one round. <see cref="AdvanceOutcome.CompletedSimulation"/>
    /// is non-null only when this round completed the event.
    /// </summary>
    internal async Task<AdvanceOutcome> AdvanceCoreAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        AdvancePreparation preparation = await PrepareAdvanceAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        AdvanceStep step = await LoadStepAsync(context, preparation, cancellationToken).ConfigureAwait(false);
        (ColorCupTeamRoundPayloadDocument payload, List<TeamEvent.TeamLegRanked>? freshLegs, Pcg32State current) =
            SimulateRound(context, preparation, step);

        RunColorCupTeamHandler.TeamSimulation? completed = null;
        if (step.RoundNumber == preparation.Rules.ColorCupTeamGroupRounds && step.GroupNumber == preparation.Rules.ColorCupTeamSize)
        {
            completed = await CompleteEventAsync(context, saveId, preparation, step, payload, freshLegs!, current, cancellationToken).ConfigureAwait(false);
        }

        if (completed is null)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await ValidatePartialAsync(context, preparation, step, cancellationToken).ConfigureAwait(false);
            await RunColorCupTeamHandler.VerifyPreservationAsync(
                context, preparation.StageCountBefore, preparation.SeasonCountBefore, preparation.RoundCountBefore,
                preparation.LifetimeBefore, preparation.EffectiveBefore, preparation.ChampionshipBefore, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        int totalRounds = checked(preparation.Rules.ColorCupTeamSize * preparation.Rules.ColorCupTeamGroupRounds);
        return new AdvanceOutcome(preparation.Source.Id, step.Rounds.Count + 1, totalRounds, completed);
    }

    internal static async Task<AdvancePreparation> PrepareAdvanceAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await RunColorCupTeamHandler.LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        RunColorCupTeamHandler.EnsureOddSeason(source);
        List<ColorCupSelectionEntity> selection = await RunColorCupTeamHandler.LoadSelectionAsync(context, source, cancellationToken).ConfigureAwait(false);
        ColorCupSelectionInvariants.ValidatePersisted(source, selection, rules);
        ColorCupTeamInvariants.ValidateField(selection, rules);
        Dictionary<int, List<ColorCupSelectionEntity>> groups = RunColorCupTeamHandler.PartitionGroups(selection, rules);
        ColorCupTeamInvariants.ValidateGroups(groups, rules);
        bool alreadyResolved = await context.ColorCupTeamStandings
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyResolved)
        {
            throw new RunColorCupTeamConflictException(
                $"Color Cup team event for Season {source.SeasonNumber} has already been resolved.");
        }

        (int stages, int seasons, int rounds, long lifetime, long effective, long championship) =
            await RunColorCupTeamHandler.CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);
        return new AdvancePreparation(
            rules, rngRow.ToState(), source, selection, groups,
            stages, seasons, rounds, lifetime, effective, championship);
    }

    internal static async Task<AdvanceStep> LoadStepAsync(
        SaveDbContext context,
        AdvancePreparation preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        List<ColorCupTeamRoundEntity> rounds = await context.ColorCupTeamRounds
            .Where(e => e.SourceSeasonId == preparation.Source.Id)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        GetColorCupTeamLiveHandler.ValidateRoundSequence(rounds, preparation.Rules, preparation.Source);
        int totalRounds = checked(preparation.Rules.ColorCupTeamSize * preparation.Rules.ColorCupTeamGroupRounds);
        if (rounds.Count >= totalRounds)
        {
            throw new InvalidOperationException(
                $"Color Cup team event for Season {preparation.Source.SeasonNumber} holds all {totalRounds} rounds but no official standings; the event state is corrupt.");
        }

        int groupNumber = (rounds.Count / preparation.Rules.ColorCupTeamGroupRounds) + 1;
        await ValidateProgressAsync(context, preparation, groupNumber, cancellationToken).ConfigureAwait(false);
        Pcg32State current = VerifyRngChain(rounds, preparation);
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = RunColorCupTeamHandler.BuildRosters(context, preparation.Groups);
        Dictionary<int, Bonus> bonuses = await RunColorCupTeamHandler.LoadCupActiveBonusesAsync(
            context, rosters, preparation.Source, preparation.Rules, cancellationToken).ConfigureAwait(false);
        int roundNumber = (rounds.Count % preparation.Rules.ColorCupTeamGroupRounds) + 1;
        return new AdvanceStep(rounds, groupNumber, roundNumber, current, rosters, bonuses);
    }

    internal static async Task ValidateProgressAsync(
        SaveDbContext context,
        AdvancePreparation preparation,
        int groupNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings
            .Where(e => e.SourceSeasonId == preparation.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidatePartialLegs(legs, preparation.Selection, groupNumber, preparation.Rules.ColorCupColorCount);
        bool hasHonour = await context.Honours
            .AnyAsync(e => e.SeasonId == preparation.Source.Id && e.Kind == (int)HonourKind.ColorCupTeamChampion, cancellationToken)
            .ConfigureAwait(false);
        if (hasHonour)
        {
            throw new InvalidOperationException(
                $"Color Cup team event for Season {preparation.Source.SeasonNumber} has corrupt partial honours.");
        }
    }

    internal static (ColorCupTeamRoundPayloadDocument Payload, List<TeamEvent.TeamLegRanked>? FreshLegs, Pcg32State Current) SimulateRound(
        SaveDbContext context,
        AdvancePreparation preparation,
        AdvanceStep step)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(step);
        Dictionary<int, Points> cumulative = BuildGroupCumulative(step.Rounds, step.GroupNumber, step.Rosters[step.GroupNumber], preparation.Source);
        RoundSimulationResult simulation = RunColorCupTeamHandler.SimulateGroupRound(
            step.Rosters[step.GroupNumber], cumulative, step.Bonuses, step.Current, preparation.Rules);
        ColorCupTeamRoundPayloadDocument payload = RunColorCupTeamHandler.BuildGroupPayload(
            preparation.Groups[step.GroupNumber], preparation.Source, preparation.Rules,
            step.GroupNumber, step.RoundNumber, step.Current, simulation);
        ColorCupTeamInvariants.ValidateRound(payload, preparation.Rules, step.Current.State, step.Current.Stream);

        context.ColorCupTeamRounds.Add(new ColorCupTeamRoundEntity
        {
            SourceSeasonId = preparation.Source.Id,
            SourceSeasonNumber = preparation.Source.SeasonNumber,
            GroupNumber = payload.GroupNumber,
            RoundNumber = payload.RoundNumber,
            RulesVersion = payload.RulesVersion,
            RngBeforeState = unchecked((long)payload.RngBeforeState),
            RngBeforeStream = unchecked((long)payload.RngBeforeStream),
            RngAfterState = unchecked((long)payload.RngAfterState),
            RngAfterStream = unchecked((long)payload.RngAfterStream),
            PayloadJson = payload.ToStored(),
            PayloadChecksum = payload.Checksum,
        });
        Pcg32State current = simulation.RngAfter;
        context.ApplyRngState(current);

        List<TeamEvent.TeamLegRanked>? freshLegs = null;
        if (step.RoundNumber == preparation.Rules.ColorCupTeamGroupRounds)
        {
            List<ColorCupTeamRoundPayloadDocument> groupPayloads = step.Rounds
                .Where(row => row.GroupNumber == step.GroupNumber)
                .Select(row => ColorCupTeamRoundPayloadDocument.FromStored(row.PayloadJson))
                .ToList();
            groupPayloads.Add(payload);
            freshLegs = CompleteGroup(context, preparation, step, groupPayloads, ref current);
        }

        return (payload, freshLegs, current);
    }
}
