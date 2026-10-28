using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.GetTypeCupTournament;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Reports.CupEditionReport;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Builds a versioned,
/// standalone report for exactly one Cup edition (color or type, historical
/// source season). Reuses the persisted selection squads plus the existing
/// result read models; never resimulates and never substitutes another
/// edition. Unfinished editions report finished vs pending phases and omit
/// unknown results. Read-only: single AsNoTracking context in one committed
/// read transaction, no RNG access, no mutation.
/// </summary>
public sealed class GetCupEditionReportHandler
{
    private readonly SaveStore _store;

    public GetCupEditionReportHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

    internal sealed record SquadMember(
        int AthleteId,
        string Name,
        int SelectionRank,
        int FinalRatingThousandths,
        int? TypeRank);

    internal sealed record SquadTeam(
        string TeamKey,
        string TeamName,
        IReadOnlyList<SquadMember> Members);

    internal sealed record ReportData(
        string Cup,
        int SourceSeasonNumber,
        int SourceSeasonId,
        string State,
        int TeamCount,
        bool HasFullRanking,
        IReadOnlyList<SquadTeam> Squads,
        GetColorCupIndividualResultResponse? ColorIndividual,
        GetColorCupTeamResultResponse? ColorTeam,
        GetTypeCupTournamentResponse? TypeTournament);

    public async Task<ReportFile> HandleAsync(
        Guid saveId,
        string cup,
        int seasonNumber,
        string? format,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        string normalizedCup = ReportContract.NormalizeCup(cup);
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
        SeasonEntity source = await LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);

        ReportData data = normalizedCup switch
        {
            "color" => await LoadColorAsync(context, saveId, source, cancellationToken).ConfigureAwait(false),
            "type" => await LoadTypeAsync(context, saveId, source, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unsupported cup '{cup}'. Use 'color' or 'type'.", nameof(cup)),
        };
        int seasonCount = await context.Seasons.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(normalized, "md", StringComparison.Ordinal))
        {
            string markdown = CupEditionReportMarkdown.Build(saveId, metadata, rules, seasonCount, data);
            return new ReportFile(
                Encoding.UTF8.GetBytes(markdown),
                ReportContract.MarkdownContentType,
                ReportFilenames.CupEditionFileName(normalizedCup, seasonNumber, "md"));
        }

        string json = CupEditionReportJson.Build(saveId, metadata, rules, seasonCount, data);
        return new ReportFile(
            Encoding.UTF8.GetBytes(json),
            ReportContract.JsonContentType,
            ReportFilenames.CupEditionFileName(normalizedCup, seasonNumber, "json"));
    }

    internal static async Task<SeasonEntity> LoadSeasonAsync(
        SaveDbContext context,
        int seasonNumber,
        CancellationToken cancellationToken)
    {
        SeasonEntity? season = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber, cancellationToken)
            .ConfigureAwait(false);
        if (season is null)
        {
            throw new CupEditionReportNotFoundException($"Cup edition for Season {seasonNumber} does not exist in this save.");
        }

        return season;
    }

    internal static async Task<ReportData> LoadColorAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<ColorCupSelectionEntity> selections = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.SportingColor)
            .ThenBy(e => e.SelectionRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (selections.Count == 0)
        {
            throw new CupEditionReportNotFoundException($"Color Cup edition for Season {source.SeasonNumber} has not been selected yet.");
        }

        Dictionary<int, string> names = await LoadNamesAsync(context, selections.Select(s => s.SaveAthleteId).ToList(), cancellationToken).ConfigureAwait(false);
        bool hasFullRanking = await HasSelectionReportAsync(context, source.Id, CupExtensionPoint.ColorCup, cancellationToken).ConfigureAwait(false);

        GetColorCupIndividualResultResponse? individual = await TryLoadColorIndividualAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
        GetColorCupTeamResultResponse? team = await TryLoadColorTeamAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);

        bool teamComplete = team is not null;
        bool individualComplete = individual is not null;
        bool anyPlayed = teamComplete || individualComplete || await AnyColorRoundsAsync(context, source.Id, cancellationToken).ConfigureAwait(false);
        string state = CupEditionState.For(anyPlayed, teamComplete && individualComplete);

        return new ReportData(
            "Color",
            source.SeasonNumber,
            source.Id,
            state,
            selections.Select(s => s.SportingColor).Distinct().Count(),
            hasFullRanking,
            BuildColorSquads(selections, names),
            individual,
            team,
            null);
    }

    internal static async Task<ReportData> LoadTypeAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<TypeCupSelectionEntity> selections = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.CreatureType)
            .ThenBy(e => e.SelectionRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (selections.Count == 0)
        {
            throw new CupEditionReportNotFoundException($"Type Cup edition for Season {source.SeasonNumber} has not been selected yet.");
        }

        Dictionary<int, string> names = await LoadNamesAsync(context, selections.Select(s => s.SaveAthleteId).ToList(), cancellationToken).ConfigureAwait(false);
        bool hasFullRanking = await HasSelectionReportAsync(context, source.Id, CupExtensionPoint.TypeCup, cancellationToken).ConfigureAwait(false);

        GetTypeCupTournamentResponse? tournament = await TryLoadTournamentAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
        bool anyPlayed = tournament is not null || await AnyTypeRoundsAsync(context, source.Id, cancellationToken).ConfigureAwait(false);
        bool complete = tournament is not null && tournament.Final is not null;
        string state = CupEditionState.For(anyPlayed, complete);

        return new ReportData(
            "Type",
            source.SeasonNumber,
            source.Id,
            state,
            selections.Select(s => s.CreatureType).Distinct(StringComparer.Ordinal).Count(),
            hasFullRanking,
            BuildTypeSquads(selections, names),
            null,
            null,
            tournament);
    }

    internal static List<SquadTeam> BuildColorSquads(List<ColorCupSelectionEntity> selections, Dictionary<int, string> names)
    {
        return selections
            .GroupBy(s => s.SportingColor)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                string key = CupTeamKeys.ColorKey((SportingColor)g.Key);
                string teamName = CupTeamKeys.ColorName((SportingColor)g.Key);
                List<SquadMember> members = g
                    .OrderBy(s => s.SelectionRank)
                    .Select(s => new SquadMember(
                        s.SaveAthleteId,
                        names.GetValueOrDefault(s.SaveAthleteId, $"Athlete {s.SaveAthleteId}"),
                        s.SelectionRank,
                        s.FinalRatingThousandths,
                        null))
                    .ToList();
                return new SquadTeam(key, teamName, members);
            })
            .ToList();
    }

    internal static List<SquadTeam> BuildTypeSquads(List<TypeCupSelectionEntity> selections, Dictionary<int, string> names)
    {
        return selections
            .GroupBy(s => s.CreatureType, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                List<SquadMember> members = g
                    .OrderBy(s => s.SelectionRank)
                    .Select(s => new SquadMember(
                        s.SaveAthleteId,
                        names.GetValueOrDefault(s.SaveAthleteId, $"Athlete {s.SaveAthleteId}"),
                        s.SelectionRank,
                        s.FinalRatingThousandths,
                        s.TypeRank))
                    .ToList();
                return new SquadTeam(g.Key, g.Key, members);
            })
            .ToList();
    }

    internal static async Task<Dictionary<int, string>> LoadNamesAsync(
        SaveDbContext context,
        List<int> athleteIds,
        CancellationToken cancellationToken)
    {
        List<int> distinct = athleteIds.Distinct().ToList();
        return await context.SaveAthletes
            .AsNoTracking()
            .Where(e => distinct.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<bool> HasSelectionReportAsync(
        SaveDbContext context,
        int sourceSeasonId,
        string cup,
        CancellationToken cancellationToken)
    {
        return await context.CupSelectionReports
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == sourceSeasonId && e.Cup == cup, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<GetColorCupIndividualResultResponse?> TryLoadColorIndividualAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return null;
        }

        return await GetColorCupIndividualResultHandler.BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<GetColorCupTeamResultResponse?> TryLoadColorTeamAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.ColorCupTeamStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            return null;
        }

        return await GetColorCupTeamResultHandler.BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<GetTypeCupTournamentResponse?> TryLoadTournamentAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool hasFinal = await context.TypeCupTeamStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id
                && (e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                    || e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final), cancellationToken)
            .ConfigureAwait(false);
        if (!hasFinal)
        {
            return null;
        }

        return await GetTypeCupTournamentHandler.BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> AnyColorRoundsAsync(SaveDbContext context, int sourceSeasonId, CancellationToken cancellationToken)
    {
        bool teamRounds = await context.ColorCupTeamRounds
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == sourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (teamRounds)
        {
            return true;
        }

        return await context.ColorCupIndividualRounds
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == sourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<bool> AnyTypeRoundsAsync(SaveDbContext context, int sourceSeasonId, CancellationToken cancellationToken)
    {
        return await context.TypeCupTeamRounds
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == sourceSeasonId, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string BuildJson(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ReportData data)
    {
        return CupEditionReportJson.Build(saveId, metadata, rules, seasonCount, data);
    }

    internal static string BuildMarkdown(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ReportData data)
    {
        return CupEditionReportMarkdown.Build(saveId, metadata, rules, seasonCount, data);
    }
}
