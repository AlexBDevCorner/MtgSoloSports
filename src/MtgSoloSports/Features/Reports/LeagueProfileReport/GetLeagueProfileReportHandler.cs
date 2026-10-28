using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Leagues.Profiles;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.LeagueProfileReport;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Builds a versioned,
/// standalone report for exactly one stable league identity: completed-season
/// podium history, the official all-time league-points leaderboard and
/// league-specific records, plus the labelled provisional current season.
/// Reuses the existing profile aggregation over completed final standings;
/// weighted career ratings never leak in. Read-only: single AsNoTracking
/// context in one committed read transaction.
/// </summary>
public sealed class GetLeagueProfileReportHandler
{
    private readonly SaveStore _store;

    public GetLeagueProfileReportHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

    internal sealed record ReportData(GetLeagueProfileResponse Profile);

    public async Task<ReportFile> HandleAsync(
        Guid saveId,
        string leagueKey,
        string? format,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (string.IsNullOrWhiteSpace(leagueKey))
        {
            throw new ArgumentException("League key must not be empty.", nameof(leagueKey));
        }

        string normalized = ReportContract.NormalizeFormat(format);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);

        GetLeagueProfileResponse profile = await LoadProfileAsync(context, saveId, leagueKey, cancellationToken).ConfigureAwait(false);
        int seasonCount = await context.Seasons.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        ReportData data = new(profile);
        if (string.Equals(normalized, "md", StringComparison.Ordinal))
        {
            string markdown = LeagueProfileReportMarkdown.Build(saveId, metadata, rules, seasonCount, data);
            return new ReportFile(
                Encoding.UTF8.GetBytes(markdown),
                ReportContract.MarkdownContentType,
                ReportFilenames.LeagueProfileFileName(profile.LeagueKey, "md"));
        }

        string json = LeagueProfileReportJson.Build(saveId, metadata, rules, seasonCount, data);
        return new ReportFile(
            Encoding.UTF8.GetBytes(json),
            ReportContract.JsonContentType,
            ReportFilenames.LeagueProfileFileName(profile.LeagueKey, "json"));
    }

    internal static async Task<GetLeagueProfileResponse> LoadProfileAsync(
        SaveDbContext context,
        Guid saveId,
        string leagueKey,
        CancellationToken cancellationToken)
    {
        if (!LeagueKey.TryParse(leagueKey, out int kind, out int? feederDivision, out int? sportingColor))
        {
            throw new LeagueProfileReportNotFoundException($"League '{leagueKey}' does not exist in this save.");
        }

        List<SeasonEntity> seasons = await context.Seasons
            .AsNoTracking()
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => e.Kind == kind
                && (kind == (int)LeagueKind.Superleague || e.FeederDivision == feederDivision)
                && (kind == (int)LeagueKind.Superleague || e.SportingColor == sportingColor))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count == 0)
        {
            throw new LeagueProfileReportNotFoundException($"League '{leagueKey}' does not exist in this save.");
        }

        HashSet<int> leagueIds = leagues.Select(l => l.Id).ToHashSet();
        List<GetLeagueProfileHandler.SeasonStandingProbe> standings = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => leagueIds.Contains(e.LeagueId))
            .Select(e => new GetLeagueProfileHandler.SeasonStandingProbe(
                e.SeasonId, e.LeagueId, e.SaveAthleteId, e.SeasonRank,
                e.TotalChampionshipPointsThousandths, e.IsChampion))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return GetLeagueProfileHandler.Map(saveId, leagueKey, seasons, leagues, standings, names);
        }
        catch (LeagueProfileNotFoundException ex)
        {
            throw new LeagueProfileReportNotFoundException(ex.Message);
        }
    }

    internal static string BuildJson(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ReportData data)
    {
        return LeagueProfileReportJson.Build(saveId, metadata, rules, seasonCount, data);
    }

    internal static string BuildMarkdown(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ReportData data)
    {
        return LeagueProfileReportMarkdown.Build(saveId, metadata, rules, seasonCount, data);
    }
}
