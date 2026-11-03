using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.ChaosCup;

/// <summary>
/// Save-specific Chaos activation plus per-season completeness (MSS-106).
/// Activation persists the effective-start season/format version so only seasons
/// started after deployment run Chaos; current/historic seasons keep the old chain.
/// Completeness is edition plus all six stage draws, rounds and standings; the Final
/// holds official P1..P32. Pure reads except <see cref="EnsureActivationAsync"/>.
/// </summary>
public static class ChaosCupLifecycle
{
    public const int ActivationId = 1;

    public sealed record ChaosState(bool SelectionResolved, bool Complete);

    public static bool IsEligible(ChaosCupActivationEntity activation, int sourceSeasonNumber)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (sourceSeasonNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSeasonNumber));
        }

        return sourceSeasonNumber >= activation.EffectiveFromSeasonNumber;
    }

    public static async Task<ChaosCupActivationEntity> EnsureActivationAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ChaosCupActivationEntity? existing = await context.ChaosCupActivations
            .SingleOrDefaultAsync(e => e.Id == ActivationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateActivation(existing);
            return existing;
        }

        // Legacy backfill: the first unstarted season is CurrentSeason + 1. A pending
        // successor (movement already resolved, StartNextSeason not yet run) has not
        // started league play, so it stays eligible; max-persisted-season + 1 would
        // permanently skip it depending on upgrade timing. New saves already seed
        // EffectiveFrom = 1 at creation, so this path only runs for pre-MSS-106 saves.
        int effectiveFrom;
        SaveMetadataEntity? metadata = await context.SaveMetadata
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        if (metadata is not null && await context.Seasons.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            effectiveFrom = checked(metadata.CurrentSeason + 1);
        }
        else
        {
            int maxSeason = await context.Seasons.AnyAsync(cancellationToken).ConfigureAwait(false)
                ? await context.Seasons.MaxAsync(e => e.SeasonNumber, cancellationToken).ConfigureAwait(false)
                : 0;
            effectiveFrom = checked(maxSeason + 1);
        }

        var created = new ChaosCupActivationEntity
        {
            Id = ActivationId,
            EffectiveFromSeasonNumber = effectiveFrom,
            ChaosFormatVersion = ChaosCupFormat.ChaosFormatVersion,
        };
        ValidateActivation(created);
        context.ChaosCupActivations.Add(created);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    internal static void ValidateActivation(ChaosCupActivationEntity activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (activation.EffectiveFromSeasonNumber < 1)
        {
            throw new InvalidOperationException("Chaos activation effective season must be positive.");
        }

        if (activation.ChaosFormatVersion != ChaosCupFormat.ChaosFormatVersion)
        {
            throw new InvalidOperationException(
                $"Chaos activation format version must be {ChaosCupFormat.ChaosFormatVersion}, was {activation.ChaosFormatVersion}.");
        }
    }

    public static async Task<ChaosCupActivationEntity> LoadActivationAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Pre-migration saves lack the table until EnsureMigrated runs; callers always run
        // EnsureMigrated first, so a missing row here means a legacy save needing backfill.
        try
        {
            return await EnsureActivationAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            throw new InvalidOperationException("Save schema predates Chaos activation; migrate the save first.", ex);
        }
    }

    public static async Task<ChaosState> LoadStateAsync(
        SaveDbContext context,
        SeasonEntity source,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ChaosCupEditionEntity? edition = await context.ChaosCupEditions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (edition is null)
        {
            return new ChaosState(false, false);
        }

        ChaosCupInvariants.ValidateEdition(edition, rules);
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            int groupCount = ChaosCupFormat.GroupCountForStage(stage);
            int expectedRounds = checked(groupCount * ChaosCupFormat.RoundsPerGroup);
            int expectedStandings = ChaosCupFormat.EntrantCountForStage(stage);
            int draws = await context.ChaosCupDraws.CountAsync(
                e => e.SourceSeasonId == source.Id && e.StageIndex == stage, cancellationToken).ConfigureAwait(false);
            int rounds = await context.ChaosCupRounds.CountAsync(
                e => e.SourceSeasonId == source.Id && e.StageIndex == stage, cancellationToken).ConfigureAwait(false);
            int standings = await context.ChaosCupGroupStandings.CountAsync(
                e => e.SourceSeasonId == source.Id && e.StageIndex == stage, cancellationToken).ConfigureAwait(false);
            int expectedDraws = expectedStandings;
            if (draws != expectedDraws || rounds != expectedRounds || standings != expectedStandings)
            {
                // Partial progress (including no draws for later stages) is pending, not corrupt;
                // detailed handlers validate contiguity and checksums on next execution.
                return new ChaosState(true, false);
            }
        }

        return new ChaosState(true, true);
    }

    public static async Task EnsureCompleteAsync(
        SaveDbContext context,
        SeasonEntity source,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ChaosCupActivationEntity activation = await LoadActivationAsync(context, cancellationToken).ConfigureAwait(false);
        if (!IsEligible(activation, source.SeasonNumber))
        {
            return;
        }

        ChaosState state = await LoadStateAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        if (!state.Complete)
        {
            throw new InvalidOperationException(
                $"Chaos Cup for Season {source.SeasonNumber} must be complete before promotion movement (selection {state.SelectionResolved}, complete {state.Complete}).");
        }
    }
}
