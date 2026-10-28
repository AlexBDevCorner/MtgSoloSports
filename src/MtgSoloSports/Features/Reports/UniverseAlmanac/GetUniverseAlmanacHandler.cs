using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Builds a curated,
/// all-time universe almanac: season champions/podiums, Color/Type Cup medal
/// history, official career records, weighted Career Leaders plus official
/// Superleague points leaders as a separate statistic, and consecutive-title
/// streaks. Curated summaries only: no round payloads, no card artwork, no
/// per-athlete every-placement matrices. Read-only: single AsNoTracking
/// context in one committed read transaction, no RNG, no mutation.
/// </summary>
public sealed class GetUniverseAlmanacHandler
{
    private readonly SaveStore _store;

    public GetUniverseAlmanacHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

    public async Task<ReportFile> HandleAsync(
        Guid saveId,
        string? format,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        string normalized = ReportContract.NormalizeFormat(format);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        UniverseAlmanacData data = await UniverseAlmanacQueries.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(normalized, "md", StringComparison.Ordinal))
        {
            string markdown = UniverseAlmanacMarkdown.Build(saveId, metadata, rules, data);
            return new ReportFile(
                Encoding.UTF8.GetBytes(markdown),
                ReportContract.MarkdownContentType,
                ReportFilenames.UniverseAlmanacFileName(metadata.Name, "md"));
        }

        string json = UniverseAlmanacJson.Build(saveId, metadata, rules, data);
        return new ReportFile(
            Encoding.UTF8.GetBytes(json),
            ReportContract.JsonContentType,
            ReportFilenames.UniverseAlmanacFileName(metadata.Name, "json"));
    }
}
