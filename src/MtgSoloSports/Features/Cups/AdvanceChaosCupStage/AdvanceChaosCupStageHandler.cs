using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.AdvanceChaosCupStage;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Draws the next Chaos Cup stage
/// after every group of the previous stage completes (MSS-102).
/// Exactly the top 16 per group qualify; fresh 32-athlete groups start from zero
/// with a pure random draw (no color/tier seeding) for stages 1..5. The Final is
/// the single 32-athlete stage whose P1..P32 standings are the official places;
/// eliminated athletes carry only their exit-stage category. The draw consumes
/// the versioned save RNG once and commits it atomically with the draw rows;
/// per-group isolated RNG derives from persisted edition plus stage-draw checksums
/// so group execution order never changes the draw. Retries return the persisted
/// draw without rerolling. Holds one per-save lock; reads never lock.
/// </summary>
public sealed class AdvanceChaosCupStageHandler
{
    private readonly SaveStore _store;

    public AdvanceChaosCupStageHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<AdvanceChaosCupStageResponse> HandleAsync(
        Guid saveId,
        int? targetStageIndex = null,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (targetStageIndex is < 1 or > 5)
        {
            throw new ArgumentException("Chaos Cup advance covers target stages 1..5.", nameof(targetStageIndex));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AdvanceUnderLockAsync(saveId, targetStageIndex, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AdvanceChaosCupStageResponse> GetAsync(
        Guid saveId,
        int stageIndex,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (stageIndex < 1 || stageIndex >= ChaosCupFormat.StageCount)
        {
            throw new ArgumentException($"Chaos Cup stage {stageIndex} is out of range (1..{ChaosCupFormat.StageCount - 1}).", nameof(stageIndex));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        return await LoadPersistedResponseAsync(context, saveId, stageIndex, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<AdvanceChaosCupStageResponse> AdvanceUnderLockAsync(
        Guid saveId,
        int? targetStageIndex,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        AdvanceState state = await LoadAdvanceStateAsync(context, saveId, targetStageIndex, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        List<ChaosCupDrawEntity> existing = await LoadStageDrawsAsync(context, state.Source, state.Target, cancellationToken).ConfigureAwait(false);
        if (existing.Count != 0)
        {
            AdvanceChaosCupStageResponse idempotent = MapPersisted(saveId, state.Source, state.Rules, state.Edition, state.PrevStandings, existing, state.Prev, state.Target);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return idempotent;
        }

        AdvanceChaosCupStageResponse response = await CreateDrawAsync(context, saveId, state, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }

    private sealed record AdvanceState(
        RulesV1 Rules,
        SeasonEntity Source,
        ChaosCupEditionEntity Edition,
        List<ChaosCupEntrantEntity> Entrants,
        Pcg32State RngBefore,
        int Prev,
        int Target,
        List<ChaosCupGroupStandingEntity> PrevStandings);

    private static async Task<AdvanceState> LoadAdvanceStateAsync(
        SaveDbContext context,
        Guid saveId,
        int? targetStageIndex,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        _ = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        ChaosCupEditionEntity edition = await LoadEditionAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<ChaosCupEntrantEntity> entrants = await LoadEntrantsAsync(context, source, cancellationToken).ConfigureAwait(false);
        ChaosCupInvariants.ValidateEdition(edition, rules);
        ChaosCupInvariants.ValidateEntrants(source, edition, entrants);
        ChaosCupFormat.ValidateFormatAgainstRules(rules);

        int target = targetStageIndex ?? await FindNextUndrawnStageAsync(context, source, cancellationToken).ConfigureAwait(false);
        if (target < 1 || target >= ChaosCupFormat.StageCount)
        {
            throw new AdvanceChaosCupStageConflictException($"Chaos Cup stage {target} cannot be drawn; random draws cover stages 1..{ChaosCupFormat.StageCount - 1}.");
        }

        int prev = target - 1;
        List<ChaosCupDrawEntity> prevDraws = await LoadStageDrawsAsync(context, source, prev, cancellationToken).ConfigureAwait(false);
        List<ChaosCupRoundEntity> prevRounds = await LoadStageRoundsAsync(context, source, prev, cancellationToken).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> prevStandings = await LoadStageStandingsAsync(context, source, prev, cancellationToken).ConfigureAwait(false);
        EnsurePrevStageComplete(source, edition, entrants, prevDraws, prevRounds, prevStandings, prev, rules);
        return new AdvanceState(rules, source, edition, entrants, rngRow.ToState(), prev, target, prevStandings);
    }

    private static async Task<AdvanceChaosCupStageResponse> CreateDrawAsync(
        SaveDbContext context,
        Guid saveId,
        AdvanceState state,
        CancellationToken cancellationToken)
    {
        await EnsureNoTargetResultsAsync(context, state.Source, state.Target, cancellationToken).ConfigureAwait(false);
        PreservationSnapshot before = await CapturePreservationAsync(context, cancellationToken).ConfigureAwait(false);
        List<AdvanceChaosCupStageSelection.StageAdvancer> advancers = await BuildAdvancersAsync(context, state.PrevStandings, cancellationToken).ConfigureAwait(false);
        Pcg32V1 rng = Pcg32V1.Restore(state.RngBefore);
        IReadOnlyList<IReadOnlyList<AdvanceChaosCupStageSelection.StageAdvancer>> groups =
            AdvanceChaosCupStageSelection.DrawRandomStage(advancers, rng, state.Target);
        Pcg32State rngAfter = rng.Snapshot();
        if (rngAfter.State == state.RngBefore.State && rngAfter.Stream == state.RngBefore.Stream)
        {
            throw new InvalidOperationException($"Chaos Cup stage {state.Target} draw must advance the save RNG.");
        }

        List<ChaosCupDrawEntity> draws = BuildDrawRows(state.Source, state.Rules, groups, state.Target, state.RngBefore, rngAfter);
        foreach (ChaosCupDrawEntity row in draws)
        {
            context.ChaosCupDraws.Add(row);
        }

        context.ApplyRngState(rngAfter);
        _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        List<ChaosCupDrawEntity> persisted = await LoadStageDrawsAsync(context, state.Source, state.Target, cancellationToken).ConfigureAwait(false);
        ChaosCupInvariants.ValidateStageDraw(state.Source, state.Edition, state.Entrants, persisted, state.Target);
        ChaosCupInvariants.ValidateAdvancementLinkage(state.PrevStandings, persisted, state.Prev);
        await VerifyPreservationAsync(context, before, rngAfter, cancellationToken).ConfigureAwait(false);
        return MapPersisted(saveId, state.Source, state.Rules, state.Edition, state.PrevStandings, persisted, state.Prev, state.Target);
    }

    private static async Task<int> FindNextUndrawnStageAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        for (int stage = 1; stage < ChaosCupFormat.StageCount; stage++)
        {
            bool hasDraws = await context.ChaosCupDraws.AnyAsync(
                e => e.SourceSeasonId == source.Id && e.StageIndex == stage, cancellationToken).ConfigureAwait(false);
            if (!hasDraws)
            {
                return stage;
            }
        }

        throw new AdvanceChaosCupStageConflictException(
            $"Chaos Cup for Season {source.SeasonNumber} is already complete with all {ChaosCupFormat.StageCount} stages drawn.");
    }

    private static void EnsurePrevStageComplete(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        List<ChaosCupEntrantEntity> entrants,
        List<ChaosCupDrawEntity> prevDraws,
        List<ChaosCupRoundEntity> prevRounds,
        List<ChaosCupGroupStandingEntity> prevStandings,
        int prev,
        RulesV1 rules)
    {
        if (prevDraws.Count == 0)
        {
            throw new AdvanceChaosCupStageConflictException(
                $"Chaos Cup stage {prev} has no persisted draw yet for Season {source.SeasonNumber}.");
        }

        int groupCount = ChaosCupFormat.GroupCountForStage(prev);
        int expectedRounds = checked(groupCount * ChaosCupFormat.RoundsPerGroup);
        if (prevRounds.Count != expectedRounds)
        {
            throw new AdvanceChaosCupStageConflictException(
                $"Chaos Cup stage {prev} is not complete: {prevRounds.Count} of {expectedRounds} rounds played; finish every group before drawing stage {prev + 1}.");
        }

        int expectedStandings = ChaosCupFormat.EntrantCountForStage(prev);
        if (prevStandings.Count != expectedStandings)
        {
            throw new AdvanceChaosCupStageConflictException(
                $"Chaos Cup stage {prev} is not complete: {prevStandings.Count} of {expectedStandings} standings persisted; finish every group before drawing stage {prev + 1}.");
        }

        ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, prevDraws, prev);
        ChaosCupInvariants.ValidateStageStandings(source, edition, prevDraws, prevRounds, prevStandings, prev, rules);

        int expectedAdvancers = ChaosCupFormat.AdvancingCountForStage(prev);
        int advanced = prevStandings.Count(s => s.IsAdvanced);
        if (advanced != expectedAdvancers)
        {
            throw new InvalidOperationException($"Chaos Cup stage {prev} must advance exactly {expectedAdvancers} athletes, was {advanced}.");
        }

        if (expectedAdvancers != ChaosCupFormat.EntrantCountForStage(prev + 1))
        {
            throw new InvalidOperationException($"Chaos Cup stage {prev} advancers {expectedAdvancers} do not fill stage {prev + 1}.");
        }
    }

    private static async Task EnsureNoTargetResultsAsync(
        SaveDbContext context,
        SeasonEntity source,
        int target,
        CancellationToken cancellationToken)
    {
        bool hasRounds = await context.ChaosCupRounds.AnyAsync(
            e => e.SourceSeasonId == source.Id && e.StageIndex == target, cancellationToken).ConfigureAwait(false);
        if (hasRounds)
        {
            throw new InvalidOperationException($"Chaos Cup stage {target} holds rounds without a persisted draw.");
        }

        bool hasStandings = await context.ChaosCupGroupStandings.AnyAsync(
            e => e.SourceSeasonId == source.Id && e.StageIndex == target, cancellationToken).ConfigureAwait(false);
        if (hasStandings)
        {
            throw new InvalidOperationException($"Chaos Cup stage {target} holds standings without a persisted draw.");
        }
    }

    private static async Task<List<AdvanceChaosCupStageSelection.StageAdvancer>> BuildAdvancersAsync(
        SaveDbContext context,
        List<ChaosCupGroupStandingEntity> prevStandings,
        CancellationToken cancellationToken)
    {
        List<int> ids = prevStandings.Where(s => s.IsAdvanced).Select(s => s.SaveAthleteId).ToList();
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        if (names.Count != ids.Count)
        {
            throw new InvalidOperationException("Chaos Cup advancers reference unknown athletes.");
        }

        List<AdvanceChaosCupStageSelection.StageAdvancer> advancers = new(ids.Count);
        foreach (int id in ids)
        {
            string name = names[id];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"Save athlete {id} has an empty name.");
            }

            advancers.Add(new AdvanceChaosCupStageSelection.StageAdvancer(id, name));
        }

        return advancers;
    }

    internal static List<ChaosCupDrawEntity> BuildDrawRows(
        SeasonEntity source,
        RulesV1 rules,
        IReadOnlyList<IReadOnlyList<AdvanceChaosCupStageSelection.StageAdvancer>> groups,
        int stageIndex,
        Pcg32State rngBefore,
        Pcg32State rngAfter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(groups);
        int groupCount = ChaosCupFormat.GroupCountForStage(stageIndex);
        if (groups.Count != groupCount)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} must hold exactly {groupCount} groups, was {groups.Count}.");
        }

        List<ChaosCupDrawEntity> rows = new(ChaosCupFormat.EntrantCountForStage(stageIndex));
        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            IReadOnlyList<AdvanceChaosCupStageSelection.StageAdvancer> group = groups[groupIndex];
            if (group.Count != ChaosCupFormat.GroupSize)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {groupIndex + 1} must hold exactly {ChaosCupFormat.GroupSize} athletes, was {group.Count}.");
            }

            for (int drawIndex = 0; drawIndex < group.Count; drawIndex++)
            {
                rows.Add(new ChaosCupDrawEntity
                {
                    SourceSeasonId = source.Id,
                    SourceSeasonNumber = source.SeasonNumber,
                    StageIndex = stageIndex,
                    GroupNumber = groupIndex + 1,
                    SaveAthleteId = group[drawIndex].SaveAthleteId,
                    DrawIndex = drawIndex,
                    GroupSize = ChaosCupFormat.GroupSize,
                    StageGroupCount = groupCount,
                    RulesVersion = rules.Version,
                    ChaosFormatVersion = ChaosCupFormat.ChaosFormatVersion,
                    RngBeforeState = unchecked((long)rngBefore.State),
                    RngBeforeStream = unchecked((long)rngBefore.Stream),
                    RngAfterState = unchecked((long)rngAfter.State),
                    RngAfterStream = unchecked((long)rngAfter.Stream),
                    DrawChecksum = string.Empty,
                });
            }
        }

        string checksum = ChaosCupInvariants.ComputeStageDrawChecksum(stageIndex, rows);
        foreach (ChaosCupDrawEntity row in rows)
        {
            row.DrawChecksum = checksum;
        }

        return rows;
    }

    private static async Task<AdvanceChaosCupStageResponse> LoadPersistedResponseAsync(
        SaveDbContext context,
        Guid saveId,
        int stageIndex,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        ChaosCupEditionEntity edition = await LoadEditionAsync(context, source, cancellationToken).ConfigureAwait(false);
        List<ChaosCupEntrantEntity> entrants = await LoadEntrantsAsync(context, source, cancellationToken).ConfigureAwait(false);
        ChaosCupInvariants.ValidateEdition(edition, rules);
        ChaosCupInvariants.ValidateEntrants(source, edition, entrants);

        List<ChaosCupDrawEntity> draws = await LoadStageDrawsAsync(context, source, stageIndex, cancellationToken).ConfigureAwait(false);
        if (draws.Count == 0)
        {
            throw new AdvanceChaosCupStageConflictException(
                $"Chaos Cup stage {stageIndex} has no persisted draw yet for Season {source.SeasonNumber}.");
        }

        int prev = stageIndex - 1;
        List<ChaosCupGroupStandingEntity> prevStandings = await LoadStageStandingsAsync(context, source, prev, cancellationToken).ConfigureAwait(false);
        ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, draws, stageIndex);
        ChaosCupInvariants.ValidateAdvancementLinkage(prevStandings, draws, prev);
        return MapPersisted(saveId, source, rules, edition, prevStandings, draws, prev, stageIndex);
    }

    private static AdvanceChaosCupStageResponse MapPersisted(
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        ChaosCupEditionEntity edition,
        List<ChaosCupGroupStandingEntity> prevStandings,
        List<ChaosCupDrawEntity> draws,
        int prev,
        int target)
    {
        ChaosCupDrawEntity first = draws.OrderBy(d => d.GroupNumber).ThenBy(d => d.DrawIndex).First();
        List<AdvanceChaosCupStageResponse.DrawGroup> groups = draws
            .GroupBy(d => d.GroupNumber)
            .OrderBy(g => g.Key)
            .Select(g => new AdvanceChaosCupStageResponse.DrawGroup(
                g.Key,
                g.OrderBy(d => d.DrawIndex).Select(d => d.SaveAthleteId).ToList()))
            .ToList();
        return new AdvanceChaosCupStageResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            rules.Version,
            ChaosCupFormat.ChaosFormatVersion,
            prev,
            target,
            ChaosCupFormat.GroupCountForStage(target),
            ChaosCupFormat.EntrantCountForStage(target),
            prevStandings.Count(s => s.IsAdvanced),
            ChaosCupFormat.IsFinalStage(target),
            edition.EditionChecksum,
            first.DrawChecksum,
            unchecked((ulong)first.RngBeforeState),
            unchecked((ulong)first.RngBeforeStream),
            unchecked((ulong)first.RngAfterState),
            unchecked((ulong)first.RngAfterStream),
            groups);
    }

    private static async Task<SeasonEntity> LoadSourceSeasonAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null)
            {
                throw new AdvanceChaosCupStageConflictException($"Season {sourceSeasonNumber.Value} does not exist for Chaos Cup stage advance.");
            }

            return explicitSeason;
        }

        ChaosCupEditionEntity? latest = await context.ChaosCupEditions
            .AsNoTracking()
            .OrderByDescending(e => e.SourceSeasonNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new AdvanceChaosCupStageConflictException("Chaos Cup field has not been selected yet for any season.");
        }

        SeasonEntity? source = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latest.SourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
        return source ?? throw new InvalidOperationException("Chaos Cup edition references an unknown season.");
    }

    private static async Task<ChaosCupEditionEntity> LoadEditionAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        ChaosCupEditionEntity? edition = await context.ChaosCupEditions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (edition is null)
        {
            throw new AdvanceChaosCupStageConflictException(
                $"Chaos Cup field for Season {source.SeasonNumber} has not been selected yet.");
        }

        return edition;
    }

    private static async Task<List<ChaosCupEntrantEntity>> LoadEntrantsAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<List<ChaosCupDrawEntity>> LoadStageDrawsAsync(
        SaveDbContext context,
        SeasonEntity source,
        int stageIndex,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<List<ChaosCupRoundEntity>> LoadStageRoundsAsync(
        SaveDbContext context,
        SeasonEntity source,
        int stageIndex,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<List<ChaosCupGroupStandingEntity>> LoadStageStandingsAsync(
        SaveDbContext context,
        SeasonEntity source,
        int stageIndex,
        CancellationToken cancellationToken)
    {
        return await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record PreservationSnapshot(
        int StageStandings,
        int SeasonStandings,
        int LeagueRounds,
        long LifetimeBonus,
        long EffectiveBonus,
        long Championship,
        int ChaosRounds,
        int ChaosStandings,
        int ChaosDraws);

    private static async Task<PreservationSnapshot> CapturePreservationAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths, cancellationToken).ConfigureAwait(false);
        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths, cancellationToken).ConfigureAwait(false);
        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths, cancellationToken).ConfigureAwait(false);
        int chaosRounds = await context.ChaosCupRounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int chaosStandings = await context.ChaosCupGroupStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int chaosDraws = await context.ChaosCupDraws.CountAsync(cancellationToken).ConfigureAwait(false);
        return new PreservationSnapshot(stages, seasons, rounds, lifetime, effective, championship, chaosRounds, chaosStandings, chaosDraws);
    }

    private static async Task VerifyPreservationAsync(
        SaveDbContext context,
        PreservationSnapshot before,
        Pcg32State rngAfter,
        CancellationToken cancellationToken)
    {
        int stages = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        if (stages != before.StageStandings)
        {
            throw new InvalidOperationException("Chaos Cup stage draw must not create league stage standings.");
        }

        int seasons = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        if (seasons != before.SeasonStandings)
        {
            throw new InvalidOperationException("Chaos Cup stage draw must not create league season standings.");
        }

        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        if (rounds != before.LeagueRounds)
        {
            throw new InvalidOperationException("Chaos Cup stage draw must not create league rounds.");
        }

        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths, cancellationToken).ConfigureAwait(false);
        if (lifetime != before.LifetimeBonus)
        {
            throw new InvalidOperationException("Chaos Cup stage draw must not change career bonus.");
        }

        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths, cancellationToken).ConfigureAwait(false);
        if (effective != before.EffectiveBonus)
        {
            throw new InvalidOperationException("Chaos Cup stage draw must not change effective bonus projections.");
        }

        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths, cancellationToken).ConfigureAwait(false);
        if (championship != before.Championship)
        {
            throw new InvalidOperationException("Chaos Cup stage draw must not award league championship points.");
        }

        int chaosRounds = await context.ChaosCupRounds.CountAsync(cancellationToken).ConfigureAwait(false);
        if (chaosRounds != before.ChaosRounds)
        {
            throw new InvalidOperationException("Chaos Cup stage draw must not create Chaos rounds.");
        }

        int chaosStandings = await context.ChaosCupGroupStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        if (chaosStandings != before.ChaosStandings)
        {
            throw new InvalidOperationException("Chaos Cup stage draw must not create Chaos standings.");
        }

        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);
        if (rng.State != unchecked((long)rngAfter.State) || rng.Stream != unchecked((long)rngAfter.Stream))
        {
            throw new InvalidOperationException("Chaos Cup stage draw must commit the save RNG exactly once.");
        }
    }

    internal static async Task<SeasonEntity> LoadSourceSeasonForTestsAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        return await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
    }
}
