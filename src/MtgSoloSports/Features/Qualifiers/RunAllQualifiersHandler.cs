using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Runs all remaining
/// qualifiers for a pending ordinary transition in canonical deterministic
/// order: Superleague first, then F1↔F2 by sporting-color enum, then F2↔F3 by
/// color enum. Each event commits RNG + results in its own transaction;
/// a retry resumes from persisted completed qualifiers and never reruns them.
/// Individual qualifier execution remains possible via the single-event
/// endpoints for Live UI. Holds one per-save lock; read-only queries never lock.
/// </summary>
public sealed class RunAllQualifiersHandler
{
    private readonly SaveStore _store;

    public RunAllQualifiersHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<RunAllQualifiersResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunRemainingUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<RunAllQualifiersResponse> RunRemainingUnderLockAsync(
        Guid saveId, CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await LoadRulesAsync(saveId, cancellationToken).ConfigureAwait(false);
        (int sourceNumber, int nextNumber) = await LoadTransitionNumbersAsync(saveId, cancellationToken).ConfigureAwait(false);

        List<string> completed = new();
        List<string> executed = new();

        // Canonical order: SL, then F1F2 by color, then F2F3 by color.
        foreach ((QualifierBoundary boundary, int? color) in QualifierIdentity.CanonicalOrder)
        {
            if (rules.FeederDivisionsPerColor != 3 && boundary != QualifierBoundary.Superleague)
            {
                continue;
            }

            bool already = await IsResolvedAsync(saveId, sourceNumber, boundary, color, cancellationToken).ConfigureAwait(false);
            if (already)
            {
                completed.Add(Describe(boundary, color));
                continue;
            }

            await RunSingleAsync(saveId, boundary, color, cancellationToken).ConfigureAwait(false);
            executed.Add(Describe(boundary, color));
        }

        return await BuildResponseAsync(saveId, sourceNumber, nextNumber, completed, executed, cancellationToken).ConfigureAwait(false);
    }

    internal static string Describe(QualifierBoundary boundary, int? color)
    {
        return boundary == QualifierBoundary.Superleague
            ? "Superleague"
            : $"{boundary}:{((MtgSoloSports.SimulationKernel.Catalog.SportingColor)color!.Value)}";
    }

    internal async Task<RulesV1> LoadRulesAsync(Guid saveId, CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        return await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<(int SourceNumber, int NextNumber)> LoadTransitionNumbersAsync(
        Guid saveId, CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        List<SeasonEntity> candidates = await context.Seasons
            .Where(e => e.IsComplete && e.HasSuperleague)
            .OrderByDescending(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (SeasonEntity candidate in candidates)
        {
            SeasonEntity? successor = await context.Seasons.SingleOrDefaultAsync(
                e => e.SeasonNumber == candidate.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
            if (successor is null || !successor.HasSuperleague || successor.IsComplete)
            {
                continue;
            }

            return (candidate.SeasonNumber, successor.SeasonNumber);
        }

        throw new RunFeederQualifierConflictException("No pending postseason transition is ready for qualifiers.");
    }

    internal async Task<bool> IsResolvedAsync(
        Guid saveId, int sourceNumber, QualifierBoundary boundary, int? color, CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity? source = await context.Seasons.AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == sourceNumber, cancellationToken).ConfigureAwait(false);
        SeasonEntity? next = await context.Seasons.AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == sourceNumber + 1, cancellationToken).ConfigureAwait(false);
        if (source is null || next is null)
        {
            return false;
        }

        int storageColor = QualifierIdentity.ToStorageColor(color);
        return await context.QualifierStandings.AnyAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == storageColor,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task RunSingleAsync(
        Guid saveId, QualifierBoundary boundary, int? color, CancellationToken cancellationToken)
    {
        if (boundary == QualifierBoundary.Superleague)
        {
            RunQualifierHandler handler = new(_store);
            await handler.RunUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
            return;
        }

        BoundaryQualifierRunner runner = new(_store);
        await runner.RunSingleUnderLockAsync(saveId, boundary, color!.Value, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RunAllQualifiersResponse> BuildResponseAsync(
        Guid saveId, int sourceNumber, int nextNumber,
        List<string> alreadyCompleted, List<string> executedNow, CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking()
            .SingleAsync(e => e.SeasonNumber == sourceNumber, cancellationToken).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking()
            .SingleAsync(e => e.SeasonNumber == nextNumber, cancellationToken).ConfigureAwait(false);
        int standings = await context.QualifierStandings.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        int rounds = await context.QualifierRounds.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
        return new RunAllQualifiersResponse(
            saveId, sourceNumber, nextNumber, standings, rounds,
            alreadyCompleted, executedNow);
    }
}
