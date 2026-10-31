using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.SelectChaosCupField;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Selects the frozen 1024-entrant Chaos
/// Cup field for one completed source season and persists the balanced 32x32 first-stage
/// draw (MSS-100). All source-season league athletes are included; distinct uniform-random
/// Pool members per color fill to 128 per color with no strength, bonus or league weighting.
/// Source tier/Pool, color, identity and the effective-bonus context are frozen before
/// promotion/rebalancing. The first draw holds exactly 4 of each color per group with its
/// RNG identity and checksums committed atomically; retries return the persisted field
/// without rerolling. Holds one per-save lock; reads never lock.
/// </summary>
public sealed partial class SelectChaosCupFieldHandler
{
    private readonly SaveStore _store;

    public SelectChaosCupFieldHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<SelectChaosCupFieldResponse> HandleAsync(
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
            return await SelectUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SelectChaosCupFieldResponse> GetAsync(
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

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await LoadPersistedResponseAsync(context, saveId, source, rules, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SelectChaosCupFieldResponse> SelectUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        InitialState state = await LoadInitialStateAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        SelectChaosCupFieldResponse? existing =
            await TryReturnExistingAsync(context, saveId, state, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        SelectChaosCupFieldResponse created =
            await CreateFieldAsync(context, saveId, state, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    internal sealed record InitialState(RulesV1 Rules, SeasonEntity Source, Pcg32State RngBefore);

    internal sealed record FieldInputs(
        Dictionary<SportingColor, int> ActivePerColor,
        Dictionary<SportingColor, int> PoolAvailablePerColor,
        IReadOnlyDictionary<SportingColor, int> RequiredByColor,
        Dictionary<SportingColor, IReadOnlyList<SelectChaosCupFieldSelection.PoolCandidate>> PoolByColor,
        List<ActiveMember> Actives,
        Dictionary<int, (int Kind, int Division)> LeagueMeta,
        Dictionary<int, string> NamesByAthlete,
        Dictionary<int, SportingColor> ColorByAthlete);

    internal sealed record ActiveMember(int SaveAthleteId, int LeagueId);

    private static async Task<InitialState> LoadInitialStateAsync(
        SaveDbContext context,
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        _ = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity source = await LoadSourceSeasonAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        EnsureComplete(source);
        ChaosCupFormat.ValidateFormatAgainstRules(rules);
        return new InitialState(rules, source, rngRow.ToState());
    }

    private static async Task<SelectChaosCupFieldResponse?> TryReturnExistingAsync(
        SaveDbContext context,
        Guid saveId,
        InitialState state,
        CancellationToken cancellationToken)
    {
        ChaosCupEditionEntity? edition = await context.ChaosCupEditions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == state.Source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (edition is null)
        {
            return null;
        }

        List<ChaosCupEntrantEntity> entrants = await LoadEntrantsAsync(context, state.Source, cancellationToken).ConfigureAwait(false);
        List<ChaosCupDrawEntity> draws = await LoadStageDrawsAsync(context, state.Source, 0, cancellationToken).ConfigureAwait(false);
        ChaosCupInvariants.ValidateEdition(edition, state.Rules);
        ChaosCupInvariants.ValidateEntrants(state.Source, edition, entrants);
        ChaosCupInvariants.ValidateStageDraw(state.Source, edition, entrants, draws, 0);
        return MapResponse(saveId, state.Source, state.Rules, edition, entrants, draws);
    }
}
