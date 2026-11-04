using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Cups.GetChaosCupRecords;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads Chaos Cup records and
/// Tournament Stories from completed history only: editions with all six stage
/// standings persisted. Never decodes round payloads, never simulates, draws or
/// consumes sporting RNG. Partial editions are ignored, never reported as
/// records, so prior history stays unchanged. Throws
/// <see cref="GetChaosCupRecordsNotFoundException"/> (404) only when the save
/// has no Chaos edition at all; saves with editions but no completed tournament
/// return vacant records with empty stories. Reads never lock.
/// </summary>
public sealed class GetChaosCupRecordsHandler
{
    private readonly SaveStore _store;

    public GetChaosCupRecordsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetChaosCupRecordsResponse> HandleAsync(
        Guid saveId,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        return await BuildResponseAsync(context, saveId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<GetChaosCupRecordsResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool hasEdition = await context.ChaosCupEditions.AsNoTracking().AnyAsync(cancellationToken).ConfigureAwait(false);
        if (!hasEdition)
        {
            throw new GetChaosCupRecordsNotFoundException("Chaos Cup records have not been resolved yet.");
        }

        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);

        List<StandingProbe> standingRows = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Select(e => new StandingProbe(e.SourceSeasonNumber, e.StageIndex, e.SaveAthleteId, e.GroupRank, e.IsAdvanced))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<EntrantProbe> entrantRows = await context.ChaosCupEntrants
            .AsNoTracking()
            .Select(e => new EntrantProbe(
                e.SourceSeasonNumber, e.SaveAthleteId, e.SportingColor,
                e.SourceLeagueKind, e.SourceFeederDivision, e.IsPoolInvite))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ChaosCupRecordCalculator.EditionInput> editions = BuildEditions(standingRows);
        List<ChaosCupRecordCalculator.EntrantInput> entrants = entrantRows
            .Select(e => new ChaosCupRecordCalculator.EntrantInput(
                e.SourceSeasonNumber, e.SaveAthleteId, e.SportingColor, DivisionKeyFor(e)))
            .ToList();
        List<ChaosCupRecordCalculator.StandingInput> standings = standingRows
            .Select(s => new ChaosCupRecordCalculator.StandingInput(
                s.SourceSeasonNumber, s.StageIndex, s.SaveAthleteId, s.GroupRank, s.IsAdvanced))
            .ToList();

        ChaosCupRecordCalculator.RecordsResult computed = ChaosCupRecordCalculator.ComputeAll(
            editions, entrants, standings, names);
        return Map(saveId, computed);
    }

    internal static List<ChaosCupRecordCalculator.EditionInput> BuildEditions(List<StandingProbe> standingRows)
    {
        Dictionary<int, Dictionary<int, int>> countsBySeason = [];
        foreach (StandingProbe row in standingRows)
        {
            if (!countsBySeason.TryGetValue(row.SourceSeasonNumber, out Dictionary<int, int>? byStage))
            {
                byStage = [];
                countsBySeason[row.SourceSeasonNumber] = byStage;
            }

            byStage.TryGetValue(row.StageIndex, out int current);
            byStage[row.StageIndex] = checked(current + 1);
        }

        List<ChaosCupRecordCalculator.EditionInput> editions = new(countsBySeason.Count);
        foreach ((int season, Dictionary<int, int> byStage) in countsBySeason)
        {
            bool complete = true;
            for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
            {
                byStage.TryGetValue(stage, out int count);
                if (count != ChaosCupFormat.EntrantCountForStage(stage))
                {
                    complete = false;
                    break;
                }
            }

            editions.Add(new ChaosCupRecordCalculator.EditionInput(season, complete));
        }

        return editions.OrderBy(e => e.SourceSeasonNumber).ToList();
    }

    internal static string DivisionKeyFor(EntrantProbe entrant)
    {
        if (entrant.IsPoolInvite)
        {
            return "Pool";
        }

        if (entrant.SourceLeagueKind == (int)LeagueKind.Superleague)
        {
            return "Superleague";
        }

        return LeagueHierarchy.LevelForDivision((FeederDivision)entrant.SourceFeederDivision, isSuperleague: false) switch
        {
            LeagueLevel.Feeder1 => "Feeder1",
            LeagueLevel.Feeder2 => "Feeder2",
            LeagueLevel.Feeder3 => "Feeder3",
            _ => throw new InvalidOperationException($"Chaos Cup entrant {entrant.SaveAthleteId} has unknown division {entrant.SourceFeederDivision}."),
        };
    }

    internal static GetChaosCupRecordsResponse Map(Guid saveId, ChaosCupRecordCalculator.RecordsResult computed)
    {
        List<GetChaosCupRecordsResponse.AthleteRecord> athletes = computed.Athletes
            .Select(a => new GetChaosCupRecordsResponse.AthleteRecord(
                a.SaveAthleteId, a.AthleteName, a.Titles, a.Medals, a.Finals,
                a.BestFinalRank, a.DeepestStage, a.LongestProgressStreak, a.P16Survivals))
            .ToList();
        List<GetChaosCupRecordsResponse.DivisionRecord> divisions = computed.Divisions
            .Select(d => new GetChaosCupRecordsResponse.DivisionRecord(
                d.DivisionKey, d.Label, d.BestFinalRank, d.BestFinalHolderIds, d.DeepestStage))
            .ToList();
        List<GetChaosCupRecordsResponse.ColorRecord> colors = computed.Colors
            .Select(c => new GetChaosCupRecordsResponse.ColorRecord(
                c.SportingColor, c.SportingColorName, c.TitlesMax, c.TitleHolderIds,
                c.BestFinalRank, c.BestRankHolderIds))
            .ToList();
        List<GetChaosCupRecordsResponse.StoryCard> stories = computed.Stories
            .Select(s => new GetChaosCupRecordsResponse.StoryCard(
                s.Kind, s.SaveAthleteId, s.AthleteName, s.SourceSeasonNumber,
                s.RelatedSeasonNumber, s.Detail, s.Text))
            .ToList();
        return new GetChaosCupRecordsResponse(
            saveId,
            ChaosCupRecordThresholds.Version,
            computed.CompletedSeasons,
            athletes,
            divisions,
            colors,
            stories);
    }

    internal sealed record StandingProbe(
        int SourceSeasonNumber,
        int StageIndex,
        int SaveAthleteId,
        int GroupRank,
        bool IsAdvanced);

    internal sealed record EntrantProbe(
        int SourceSeasonNumber,
        int SaveAthleteId,
        int SportingColor,
        int SourceLeagueKind,
        int SourceFeederDivision,
        bool IsPoolInvite);
}
