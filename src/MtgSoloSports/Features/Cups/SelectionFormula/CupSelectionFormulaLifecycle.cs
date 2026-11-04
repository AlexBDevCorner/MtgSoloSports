using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.SelectionFormula;

/// <summary>
/// Save-specific Cup selection formula activation plus per-edition effective
/// weights (MSS-132). Activation persists the first source season that uses
/// the approved 35/30/20/5/10 weights; earlier source seasons keep their
/// snapshot weights (legacy 35/30/25/10). The original rules snapshot and all
/// unrelated league/bonus/prestige/tournament/RNG settings stay intact; only
/// the five outer weights change at activation. Effective formula is selected
/// by the Cup's source season, never by today's current season or UI state.
/// Reuses <see cref="ChaosCup.ChaosCupLifecycle"/>'s persisted-activation
/// pattern but stays independently identifiable. Idempotent, consumes no
/// sporting RNG, never moves the effective season on retry.
/// </summary>
public static class CupSelectionFormulaLifecycle
{
    public const int ActivationId = 1;

    public const int FormulaVersion = 1;

    public const int LegacyBonusWeightPermille = 350;
    public const int LegacyPerformanceWeightPermille = 300;
    public const int LegacyFormWeightPermille = 250;
    public const int LegacyChaosWeightPermille = 0;
    public const int LegacyPrestigeWeightPermille = 100;

    public sealed record EffectiveWeights(
        int BonusWeightPermille,
        int PerformanceWeightPermille,
        int FormWeightPermille,
        int ChaosWeightPermille,
        int PrestigeWeightPermille);

    public static EffectiveWeights LegacyWeights => new(
        LegacyBonusWeightPermille,
        LegacyPerformanceWeightPermille,
        LegacyFormWeightPermille,
        LegacyChaosWeightPermille,
        LegacyPrestigeWeightPermille);

    public static EffectiveWeights CurrentWeights => new(
        RulesV1.DefaultCupBonusWeightPermille,
        RulesV1.DefaultCupPerformanceWeightPermille,
        RulesV1.DefaultCupFormWeightPermille,
        RulesV1.DefaultCupChaosWeightPermille,
        RulesV1.DefaultCupPrestigeWeightPermille);

    public static bool IsLegacyWeights(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.CupBonusWeightPermille == LegacyBonusWeightPermille
            && rules.CupPerformanceWeightPermille == LegacyPerformanceWeightPermille
            && rules.CupFormWeightPermille == LegacyFormWeightPermille
            && rules.CupChaosWeightPermille == LegacyChaosWeightPermille
            && rules.CupPrestigeWeightPermille == LegacyPrestigeWeightPermille;
    }

    public static bool IsCurrentWeights(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.CupBonusWeightPermille == RulesV1.DefaultCupBonusWeightPermille
            && rules.CupPerformanceWeightPermille == RulesV1.DefaultCupPerformanceWeightPermille
            && rules.CupFormWeightPermille == RulesV1.DefaultCupFormWeightPermille
            && rules.CupChaosWeightPermille == RulesV1.DefaultCupChaosWeightPermille
            && rules.CupPrestigeWeightPermille == RulesV1.DefaultCupPrestigeWeightPermille;
    }

    public static void EnsureSupportedWeights(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!IsLegacyWeights(rules) && !IsCurrentWeights(rules))
        {
            throw new InvalidOperationException(
                "Save rules snapshot has unsupported Cup selection weights; game-rule migration is explicit and never implicit.");
        }
    }

    public static bool IsNewFormula(CupSelectionFormulaActivationEntity activation, int sourceSeasonNumber)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (sourceSeasonNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSeasonNumber));
        }

        return sourceSeasonNumber >= activation.EffectiveFromSeasonNumber;
    }

    public static EffectiveWeights ResolveWeights(
        RulesV1 rules,
        CupSelectionFormulaActivationEntity activation,
        int sourceSeasonNumber)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(activation);
        EnsureSupportedWeights(rules);
        ValidateActivation(activation);
        if (sourceSeasonNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSeasonNumber));
        }

        if (!IsNewFormula(activation, sourceSeasonNumber))
        {
            return new EffectiveWeights(
                rules.CupBonusWeightPermille,
                rules.CupPerformanceWeightPermille,
                rules.CupFormWeightPermille,
                rules.CupChaosWeightPermille,
                rules.CupPrestigeWeightPermille);
        }

        return CurrentWeights;
    }

    /// <summary>
    /// Returns an effective rules view for one source season: the same snapshot
    /// version and sporting core with only the five outer weights replaced when
    /// the source season is at or past activation. Historical sources keep the
    /// snapshot object unchanged. Only weights change; input calculations,
    /// tie-breaks, prestige model and eligibility are untouched.
    /// </summary>
    public static RulesV1 ToEffectiveRules(
        RulesV1 rules,
        CupSelectionFormulaActivationEntity activation,
        int sourceSeasonNumber)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(activation);
        EffectiveWeights weights = ResolveWeights(rules, activation, sourceSeasonNumber);
        if (weights.BonusWeightPermille == rules.CupBonusWeightPermille
            && weights.PerformanceWeightPermille == rules.CupPerformanceWeightPermille
            && weights.FormWeightPermille == rules.CupFormWeightPermille
            && weights.ChaosWeightPermille == rules.CupChaosWeightPermille
            && weights.PrestigeWeightPermille == rules.CupPrestigeWeightPermille)
        {
            return rules;
        }

        return CloneWithWeights(rules, weights);
    }

    internal static RulesV1 CloneWithWeights(RulesV1 rules, EffectiveWeights weights)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(weights);
        if (rules is RulesV3 v3)
        {
            RulesV3SnapshotDocument document = RulesV3SnapshotDocument.FromRules(v3) with
            {
                CupBonusWeightPermille = weights.BonusWeightPermille,
                CupPerformanceWeightPermille = weights.PerformanceWeightPermille,
                CupFormWeightPermille = weights.FormWeightPermille,
                CupChaosWeightPermille = weights.ChaosWeightPermille,
                CupPrestigeWeightPermille = weights.PrestigeWeightPermille,
            };
            RulesV3 cloned = document.ToRules();
            cloned.Validate();
            return cloned;
        }

        if (rules is RulesV2 v2)
        {
            RulesV2SnapshotDocument document = RulesV2SnapshotDocument.FromRules(v2) with
            {
                CupBonusWeightPermille = weights.BonusWeightPermille,
                CupPerformanceWeightPermille = weights.PerformanceWeightPermille,
                CupFormWeightPermille = weights.FormWeightPermille,
                CupChaosWeightPermille = weights.ChaosWeightPermille,
                CupPrestigeWeightPermille = weights.PrestigeWeightPermille,
            };
            RulesV2 cloned = document.ToRules();
            cloned.Validate();
            return cloned;
        }

        RulesSnapshotDocument v1Document = RulesSnapshotDocument.FromRules(rules) with
        {
            CupBonusWeightPermille = weights.BonusWeightPermille,
            CupPerformanceWeightPermille = weights.PerformanceWeightPermille,
            CupFormWeightPermille = weights.FormWeightPermille,
            CupChaosWeightPermille = weights.ChaosWeightPermille,
            CupPrestigeWeightPermille = weights.PrestigeWeightPermille,
        };
        RulesV1 clonedV1 = v1Document.ToRules();
        clonedV1.Validate();
        return clonedV1;
    }

    public static string FormulaLabel(EffectiveWeights weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        return $"{weights.BonusWeightPermille}/{weights.PerformanceWeightPermille}/{weights.FormWeightPermille}/{weights.ChaosWeightPermille}/{weights.PrestigeWeightPermille}";
    }

    public static async Task<CupSelectionFormulaActivationEntity> EnsureActivationAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        DetachUncommittedActivation(context);
        CupSelectionFormulaActivationEntity? existing = await context.CupSelectionFormulaActivations
            .SingleOrDefaultAsync(e => e.Id == ActivationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            ValidateActivation(existing);
            return existing;
        }

        RulesV1 rules = await LoadSnapshotRulesAsync(context, cancellationToken).ConfigureAwait(false);
        EnsureSupportedWeights(rules);
        int effectiveFrom = await ComputeLegacyEffectiveFromAsync(context, rules, cancellationToken).ConfigureAwait(false);
        return await InsertActivationAsync(context, effectiveFrom, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RulesV1> LoadSnapshotRulesAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        RulesSnapshotEntity snapshot = await context.RulesSnapshots
            .AsNoTracking()
            .SingleAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        return SaveRulesCompatibility.ReadSnapshotRules(snapshot.RulesJson);
    }

    private static async Task<int> ComputeLegacyEffectiveFromAsync(
        SaveDbContext context, RulesV1 rules, CancellationToken cancellationToken)
    {
        // Uninitialized saves (no seasons) and saves already on current weights
        // stay current from the first eligible edition.
        bool hasSeasons = await context.Seasons.AnyAsync(cancellationToken).ConfigureAwait(false);
        if (!hasSeasons)
        {
            return 1;
        }

        if (IsCurrentWeights(rules))
        {
            return 1;
        }

        // Initialized legacy save first upgraded during source season N uses the
        // first newly started season, no earlier than N+1 and no earlier than
        // the save's Chaos activation season. A pending successor already
        // persisted by movement but not started is still N+1 (CurrentSeason + 1,
        // never max-persisted + 1, which would permanently skip it).
        SaveMetadataEntity? metadata = await context.SaveMetadata
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        int current = metadata?.CurrentSeason
            ?? await context.Seasons.MaxAsync(e => e.SeasonNumber, cancellationToken).ConfigureAwait(false);
        if (current < 1)
        {
            return 1;
        }

        int candidate = checked(current + 1);
        int chaosEffective = await LoadChaosEffectiveFromAsync(context, candidate, cancellationToken).ConfigureAwait(false);
        return Math.Max(candidate, chaosEffective);
    }

    private static async Task<int> LoadChaosEffectiveFromAsync(
        SaveDbContext context, int fallback, CancellationToken cancellationToken)
    {
        try
        {
            ChaosCupActivationEntity? chaos = await context.ChaosCupActivations
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.Id == ChaosCup.ChaosCupLifecycle.ActivationId, cancellationToken)
                .ConfigureAwait(false);
            if (chaos is null)
            {
                return fallback;
            }

            return chaos.EffectiveFromSeasonNumber < 1 ? fallback : chaos.EffectiveFromSeasonNumber;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1
            && ex.Message.Contains("ChaosCupActivations", StringComparison.OrdinalIgnoreCase))
        {
            // Table predates the Chaos activation migration; treat Chaos as not
            // yet activated and keep the N+1 boundary.
            return fallback;
        }
    }

    private static async Task<CupSelectionFormulaActivationEntity> InsertActivationAsync(
        SaveDbContext context, int effectiveFrom, CancellationToken cancellationToken)
    {
        var created = new CupSelectionFormulaActivationEntity
        {
            Id = ActivationId,
            EffectiveFromSeasonNumber = effectiveFrom,
            FormulaVersion = FormulaVersion,
        };
        ValidateActivation(created);
        context.CupSelectionFormulaActivations.Add(created);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch (DbUpdateException ex) when (IsActivationUniqueViolation(ex))
        {
            context.Entry(created).State = EntityState.Detached;
            CupSelectionFormulaActivationEntity? winner = await context.CupSelectionFormulaActivations
                .SingleOrDefaultAsync(e => e.Id == ActivationId, cancellationToken)
                .ConfigureAwait(false);
            if (winner is not null)
            {
                ValidateActivation(winner);
                return winner;
            }

            throw;
        }
    }

    private static void DetachUncommittedActivation(SaveDbContext context)
    {
        CupSelectionFormulaActivationEntity? staged = context.CupSelectionFormulaActivations.Local
            .FirstOrDefault(e => e.Id == ActivationId);
        if (staged is not null && context.Entry(staged).State == EntityState.Added)
        {
            context.Entry(staged).State = EntityState.Detached;
        }
    }

    private static bool IsActivationUniqueViolation(DbUpdateException ex)
    {
        for (Exception? current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.Sqlite.SqliteException sqlite
                && (sqlite.SqliteErrorCode == 19
                    || sqlite.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    internal static void ValidateActivation(CupSelectionFormulaActivationEntity activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (activation.EffectiveFromSeasonNumber < 1)
        {
            throw new InvalidOperationException("Cup selection formula effective season must be positive.");
        }

        if (activation.FormulaVersion != FormulaVersion)
        {
            throw new InvalidOperationException(
                $"Cup selection formula version must be {FormulaVersion}, was {activation.FormulaVersion}.");
        }
    }

    public static async Task<CupSelectionFormulaActivationEntity> LoadActivationAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await EnsureActivationAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            throw new InvalidOperationException("Save schema predates Cup selection formula activation; migrate the save first.", ex);
        }
    }
}
