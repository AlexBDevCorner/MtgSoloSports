using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.SeasonDossier;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Builds a versioned,
/// journalism-ready dossier for exactly one season: full Superleague table,
/// stage-by-stage winners from persisted stage standings, concise feeder
/// summaries, postseason movement/qualifier status, completed Cup summaries
/// with qualification-vs-Final and individual-vs-team separated, plus
/// previous-champion context, leader/cup-performer career titles and official
/// career records. Never resimulates, never advances events, never uses RNG.
/// Read-only: single AsNoTracking context in one committed read transaction.
/// </summary>
public sealed class GetSeasonDossierHandler
{
    private readonly SaveStore _store;

    public GetSeasonDossierHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

    public async Task<ReportFile> HandleAsync(
        Guid saveId,
        int seasonNumber,
        string? format,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(seasonNumber));
        }

        string normalized = ReportContract.NormalizeFormat(format);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity season;
        try
        {
            season = await ListHistoryCompetitionsHandler.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        }
        catch (HistoryNotFoundException ex)
        {
            throw new SeasonDossierNotFoundException(ex.Message);
        }

        int seasonCount = await context.Seasons.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
        SeasonDossierData loaded = await SeasonDossierQueries.LoadAsync(context, saveId, season, rules, cancellationToken).ConfigureAwait(false);
        SeasonDossierData data = loaded with { SeasonCount = seasonCount };
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(normalized, "md", StringComparison.Ordinal))
        {
            string markdown = SeasonDossierMarkdown.Build(saveId, metadata, rules, seasonCount, data);
            return new ReportFile(
                Encoding.UTF8.GetBytes(markdown),
                ReportContract.MarkdownContentType,
                ReportFilenames.SeasonDossierFileName(seasonNumber, "md"));
        }

        string json = SeasonDossierJson.Build(saveId, metadata, rules, seasonCount, data);
        return new ReportFile(
            Encoding.UTF8.GetBytes(json),
            ReportContract.JsonContentType,
            ReportFilenames.SeasonDossierFileName(seasonNumber, "json"));
    }
}
