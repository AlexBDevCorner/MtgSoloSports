using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetTypeCupTeamLive;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Cups.AdvanceTypeCupTeamRound;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Simulates exactly one
/// Type Cup team round for a completed even source season and persists it with
/// the RNG-after state in one transaction: groups run 1..4 in order, rounds
/// 1..8 within each group. Completing a group's eighth round also ranks that
/// group's legs (consuming RNG exactly as the former atomic run did) and
/// persists them; completing group 4 round 8 additionally persists the
/// official team standings, champion honours, permanent nationality and stories.
/// Skipping, repeating, reordering, or mixing rule versions aborts the mutation;
/// corrupted sporting state is never silently repaired. Holds one per-save
/// lock; the live GET projection never locks. Returns the authoritative live
/// projection (re-read after commit) so incremental refresh and reload agree.
/// </summary>
public sealed partial class AdvanceTypeCupTeamRoundHandler
{
    private readonly SaveStore _store;

    public AdvanceTypeCupTeamRoundHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<TypeCupTeamLiveResponse> HandleAsync(
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
    internal async Task<TypeCupTeamLiveResponse> AdvanceUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        _ = await AdvanceCoreAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        GetTypeCupTeamLiveHandler live = new(_store);
        return await live.HandleAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record AdvanceOutcome(
        int SourceSeasonId,
        int CompletedRounds,
        int TotalRounds,
        RunTypeCupTeamHandler.TeamSimulation? CompletedSimulation);

    internal sealed record AdvancePreparation(
        RulesV1 Rules,
        Pcg32State RngBefore,
        SeasonEntity Source,
        List<TypeCupSelectionEntity> Selection,
        int TeamCount,
        Dictionary<int, List<TypeCupSelectionEntity>> Groups,
        int StageCountBefore,
        int SeasonCountBefore,
        int RoundCountBefore,
        long LifetimeBefore,
        long EffectiveBefore,
        long ChampionshipBefore);

    internal sealed record AdvanceStep(
        List<TypeCupTeamRoundEntity> Rounds,
        int GroupNumber,
        int RoundNumber,
        Pcg32State Current,
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> Rosters,
        Dictionary<int, Bonus> Bonuses,
        Dictionary<string, int> TeamIds);

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
        (TypeCupTeamRoundPayloadDocument payload, List<TeamEvent.TeamLegRanked>? freshLegs, Pcg32State current) =
            SimulateRound(context, preparation, step);

        RunTypeCupTeamHandler.TeamSimulation? completed = null;
        if (step.RoundNumber == preparation.Rules.TypeCupGroupRounds && step.GroupNumber == preparation.Rules.TypeCupMinTeamSize)
        {
            completed = await CompleteEventAsync(context, saveId, preparation, step, payload, freshLegs!, current, cancellationToken).ConfigureAwait(false);
        }

        if (completed is null)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await ValidatePartialAsync(context, preparation, step, cancellationToken).ConfigureAwait(false);
            await RunTypeCupTeamHandler.VerifyPreservationAsync(
                context, preparation.StageCountBefore, preparation.SeasonCountBefore, preparation.RoundCountBefore,
                preparation.LifetimeBefore, preparation.EffectiveBefore, preparation.ChampionshipBefore, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        int totalRounds = checked(preparation.Rules.TypeCupMinTeamSize * preparation.Rules.TypeCupGroupRounds);
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
        SeasonEntity source = await RunTypeCupTeamHandler.LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        RunTypeCupTeamHandler.EnsureEvenSeason(source);
        List<TypeCupSelectionEntity> selection = await RunTypeCupTeamHandler.LoadSelectionAsync(context, source, cancellationToken).ConfigureAwait(false);
        TypeCupSelectionInvariants.ValidatePersisted(source, selection, rules);
        int teamCount = TypeCupTeamInvariants.ValidateField(selection, rules);
        Dictionary<int, List<TypeCupSelectionEntity>> groups = RunTypeCupTeamHandler.PartitionGroups(selection, rules);
        TypeCupTeamInvariants.ValidateGroups(groups, rules, teamCount);
        await ValidateNationalityPreconditionsAsync(context, selection, cancellationToken).ConfigureAwait(false);
        bool alreadyResolved = await context.TypeCupTeamStandings
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyResolved)
        {
            throw new RunTypeCupTeamConflictException(
                $"Type Cup team event for Season {source.SeasonNumber} has already been resolved.");
        }

        (int stages, int seasons, int rounds, long lifetime, long effective, long championship) =
            await RunTypeCupTeamHandler.CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);
        return new AdvancePreparation(
            rules, rngRow.ToState(), source, selection, teamCount, groups,
            stages, seasons, rounds, lifetime, effective, championship);
    }

    internal static async Task<AdvanceStep> LoadStepAsync(
        SaveDbContext context,
        AdvancePreparation preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds
            .Where(e => e.SourceSeasonId == preparation.Source.Id)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        GetTypeCupTeamLiveHandler.ValidateRoundSequence(rounds, preparation.Rules, preparation.Source);
        int totalRounds = checked(preparation.Rules.TypeCupMinTeamSize * preparation.Rules.TypeCupGroupRounds);
        if (rounds.Count >= totalRounds)
        {
            throw new InvalidOperationException(
                $"Type Cup team event for Season {preparation.Source.SeasonNumber} holds all {totalRounds} rounds but no official standings; the event state is corrupt.");
        }

        int groupNumber = (rounds.Count / preparation.Rules.TypeCupGroupRounds) + 1;
        await ValidateProgressAsync(context, preparation, groupNumber, cancellationToken).ConfigureAwait(false);
        Dictionary<string, int> teamIds = RunTypeCupTeamHandler.BuildTeamIds(preparation.Selection);
        Pcg32State current = VerifyRngChain(rounds, preparation, teamIds);
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = RunTypeCupTeamHandler.BuildRosters(context, preparation.Groups);
        Dictionary<int, Bonus> bonuses = await RunTypeCupTeamHandler.LoadCupActiveBonusesAsync(
            context, rosters, preparation.Source, preparation.Rules, cancellationToken).ConfigureAwait(false);
        int roundNumber = (rounds.Count % preparation.Rules.TypeCupGroupRounds) + 1;
        return new AdvanceStep(rounds, groupNumber, roundNumber, current, rosters, bonuses, teamIds);
    }

    internal static async Task ValidateProgressAsync(
        SaveDbContext context,
        AdvancePreparation preparation,
        int groupNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .Where(e => e.SourceSeasonId == preparation.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidatePartialLegs(legs, preparation.Selection, groupNumber, preparation.TeamCount);
        bool hasHonour = await context.Honours
            .AnyAsync(e => e.SeasonId == preparation.Source.Id && e.Kind == (int)HonourKind.TypeCupTeamChampion, cancellationToken)
            .ConfigureAwait(false);
        if (hasHonour)
        {
            throw new InvalidOperationException(
                $"Type Cup team event for Season {preparation.Source.SeasonNumber} has corrupt partial honours.");
        }
    }

    internal static (TypeCupTeamRoundPayloadDocument Payload, List<TeamEvent.TeamLegRanked>? FreshLegs, Pcg32State Current) SimulateRound(
        SaveDbContext context,
        AdvancePreparation preparation,
        AdvanceStep step)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(step);
        Dictionary<int, Points> cumulative = BuildGroupCumulative(step.Rounds, step.GroupNumber, step.Rosters[step.GroupNumber], preparation.Source);
        RoundSimulationResult simulation = RunTypeCupTeamHandler.SimulateGroupRound(
            step.Rosters[step.GroupNumber], cumulative, step.Bonuses, step.Current, preparation.Rules);
        TypeCupTeamRoundPayloadDocument payload = RunTypeCupTeamHandler.BuildGroupPayload(
            preparation.Groups[step.GroupNumber], preparation.Source, preparation.Rules,
            step.GroupNumber, step.RoundNumber, step.Current, simulation);
        TypeCupTeamInvariants.ValidateRound(payload, preparation.Rules, preparation.TeamCount, step.Current.State, step.Current.Stream);

        context.TypeCupTeamRounds.Add(new TypeCupTeamRoundEntity
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
        if (step.RoundNumber == preparation.Rules.TypeCupGroupRounds)
        {
            List<TypeCupTeamRoundPayloadDocument> groupPayloads = step.Rounds
                .Where(row => row.GroupNumber == step.GroupNumber)
                .Select(row => TypeCupTeamRoundPayloadDocument.FromStored(row.PayloadJson))
                .ToList();
            groupPayloads.Add(payload);
            freshLegs = CompleteGroup(context, preparation, step, groupPayloads, ref current);
        }

        return (payload, freshLegs, current);
    }

    internal static async Task ValidateNationalityPreconditionsAsync(
        SaveDbContext context,
        List<TypeCupSelectionEntity> selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selection);
        Dictionary<int, string> typeByAthlete = selection.ToDictionary(e => e.SaveAthleteId, e => e.CreatureType);
        List<int> ids = typeByAthlete.Keys.ToList();
        List<SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (athletes.Count != ids.Count)
        {
            throw new InvalidOperationException("Type Cup team selection references unknown athletes.");
        }

        foreach (SaveAthleteEntity athlete in athletes)
        {
            string allocated = typeByAthlete[athlete.Id];
            if (string.IsNullOrWhiteSpace(athlete.TypeCupNationality))
            {
                continue;
            }

            if (!string.Equals(athlete.TypeCupNationality.Trim(), allocated, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Athlete '{athlete.Name}' is capped for '{athlete.TypeCupNationality}' but is allocated for '{allocated}'; nationality can never change.");
            }
        }
    }
}
