using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Diagnostics.LongRunInvariants;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Runs the six structural
/// long-run invariant checks over normalized tables (league sizes, duplicates,
/// bonus timing, qualifier counts, nationality immutability, Cup rotation).
/// Read-only: no lock, no RNG, no mutation, no round-payload decompression.
/// </summary>
public sealed class ValidateLongRunInvariantsHandler
{
    private readonly SaveStore _store;

    public ValidateLongRunInvariantsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ValidateLongRunInvariantsResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        LongRunInvariantChecks.Snapshot snapshot = await LongRunInvariantChecks.LoadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LongRunInvariantChecks.InvariantResult> results = LongRunInvariantChecks.ValidateAll(snapshot, rules);
        return new ValidateLongRunInvariantsResponse(
            saveId,
            results.All(r => r.Passed),
            results.Select(r => new LongRunInvariantEntry(r.Name, r.Passed, r.Detail)).ToList());
    }
}
