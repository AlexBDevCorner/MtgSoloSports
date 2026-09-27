using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Diagnostics.LongRunStats;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reports long-run storage
/// shape with COUNT queries only: no round payload is selected or
/// decompressed. Used by the opt-in benchmark harness and by operators sizing
/// hundred/thousand-season saves. Read-only: no lock, no RNG mutation.
/// </summary>
public sealed class GetLongRunStatsHandler
{
    private readonly SaveStore _store;

    public GetLongRunStatsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetLongRunStatsResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);

        int seasons = await context.Seasons.CountAsync(cancellationToken).ConfigureAwait(false);
        int leagues = await context.Leagues.CountAsync(cancellationToken).ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int stageStandings = await context.StageStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int seasonStandings = await context.SeasonStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync(cancellationToken).ConfigureAwait(false);
        int qualifierStandings = await context.QualifierStandings.CountAsync(cancellationToken).ConfigureAwait(false);
        int colorSelections = await context.ColorCupSelections.CountAsync(cancellationToken).ConfigureAwait(false);
        int typeSelections = await context.TypeCupSelections.CountAsync(cancellationToken).ConfigureAwait(false);
        int honours = await context.Honours.CountAsync(cancellationToken).ConfigureAwait(false);
        int stories = await context.StoryEvents.CountAsync(cancellationToken).ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1, cancellationToken).ConfigureAwait(false);

        string path = _store.GetSaveFilePath(saveId);
        long bytes = File.Exists(path) ? new FileInfo(path).Length : 0L;

        unchecked
        {
            return new GetLongRunStatsResponse(
                saveId, seasons, leagues, rounds, stageStandings, seasonStandings,
                qualifierRounds, qualifierStandings, colorSelections, typeSelections,
                honours, stories, bytes, (ulong)rng.State, (ulong)rng.Stream);
        }
    }
}
