using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Seasons.SeasonLifecycle;

/// <summary>
/// Post-rebalance Cup extension point. Game rules run one Cup after every
/// season (odd seasons Color Cup, even seasons Type Cup) between feeder
/// rebalancing and season finalization/next-season start. Cups use the completed
/// source season plus current-season effective bonus values before season aging,
/// consume the versioned simulation RNG in canonical order (Color: selection
/// with no RNG, then individual, then team; Type: selection with no RNG, then
/// team) and commit RNG state with results. Each Cup step is an explicit
/// inspectable <c>Next Event</c> boundary; only a fully complete Cup may start
/// the next season. Never consumes sporting RNG itself.
/// </summary>
public static class CupExtensionPoint
{
    public const string NoCup = "None";
    public const string ColorCup = "ColorCup";
    public const string TypeCup = "TypeCup";

    /// <summary>
    /// Returns the Cup that runs for a completed source season. Odd seasons use
    /// the Color Cup (selection, individual, team); even seasons use the Type Cup
    /// (selection, team). Before rebalancing the Cup is not yet runnable.
    /// </summary>
    public static string ExpectedCupForSource(int sourceSeasonNumber)
    {
        if (sourceSeasonNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSeasonNumber));
        }

        return sourceSeasonNumber % 2 == 1 ? ColorCup : TypeCup;
    }

    public sealed record CupState(
        bool SelectionResolved,
        bool IndividualResolved,
        bool TeamResolved,
        bool Complete);

    /// <summary>
    /// Loads the persisted Cup state for one completed source season. For the
    /// Color Cup, selection, individual and team must all be present; for the
    /// Type Cup (team-only), selection and team must be present. The individual
    /// flag stays false for Type Cup seasons, mirroring how the inaugural
    /// transition leaves <c>QualifierResolved</c> false. Never consumes RNG.
    /// </summary>
    public static async Task<CupState> LoadCupStateAsync(
        SaveDbContext context,
        SeasonEntity source,
        string expectedCup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCup);

        if (string.Equals(expectedCup, ColorCup, StringComparison.Ordinal))
        {
            bool selection = await context.ColorCupSelections
                .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
                .ConfigureAwait(false);
            bool individual = await context.ColorCupIndividualStandings
                .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
                .ConfigureAwait(false);
            bool team = await context.ColorCupTeamStandings
                .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
                .ConfigureAwait(false);
            if ((individual || team) && !selection)
            {
                throw new InvalidOperationException(
                    $"Color Cup for Season {source.SeasonNumber} has event results without team selection; sporting state is corrupt.");
            }

            bool complete = selection && individual && team;
            return new CupState(selection, individual, team, complete);
        }

        if (string.Equals(expectedCup, TypeCup, StringComparison.Ordinal))
        {
            bool selection = await context.TypeCupSelections
                .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
                .ConfigureAwait(false);
            // The Cup is complete only when Final (or legacy single-field)
            // standings are persisted. Qualification standings alone never
            // complete the tournament.
            bool team = await context.TypeCupTeamStandings
                .AnyAsync(e => e.SourceSeasonId == source.Id
                    && (e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                        || e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final),
                    cancellationToken)
                .ConfigureAwait(false);
            if (team && !selection)
            {
                throw new InvalidOperationException(
                    $"Type Cup for Season {source.SeasonNumber} has team results without allocation; sporting state is corrupt.");
            }

            bool complete = selection && team;
            return new CupState(selection, IndividualResolved: false, team, complete);
        }

        throw new InvalidOperationException($"Unsupported expected Cup '{expectedCup}'.");
    }

    /// <summary>
    /// Validates the Cup extension point. Kept for callers that only need a
    /// synchronous hook; the authoritative gate is
    /// <see cref="EnsureCupCompleteAsync"/>, which requires the persisted Cup to
    /// be complete before season finalization. Never consumes sporting RNG.
    /// </summary>
    public static void ValidateCupExtensionPoint(SaveDbContext context, SeasonEntity source, SeasonEntity next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(next);
    }

    /// <summary>
    /// Requires the persisted Cup for one source season to be complete. Throws
    /// <c>InvalidOperationException</c> when the Cup is pending or corrupt; the
    /// StartNextSeason gate converts pending Cups to a legal conflict. Never
    /// consumes sporting RNG.
    /// </summary>
    public static async Task EnsureCupCompleteAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        string expectedCup = ExpectedCupForSource(source.SeasonNumber);
        CupState state = await LoadCupStateAsync(context, source, expectedCup, cancellationToken).ConfigureAwait(false);
        if (state.Complete)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Post-season {expectedCup} for Season {source.SeasonNumber} must be complete before the next season can start " +
            $"(selection {state.SelectionResolved}, individual {state.IndividualResolved}, team {state.TeamResolved}).");
    }
}
