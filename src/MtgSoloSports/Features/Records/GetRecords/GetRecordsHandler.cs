using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records.GetRecords;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Serves current career
/// records plus scoring records and recent break history. Career values are
/// computed live from normalized standings, memberships, movements, qualifier
/// standings and career projections via <see cref="RecordLoader"/> plus
/// <see cref="RecordCalculator"/>; neither <c>Rounds.PayloadJson</c> nor
/// <c>QualifierRounds.PayloadJson</c> is selected for that path. Scoring
/// records are derived from historical persisted round/stage/event results via
/// <see cref="ScoreRecordLoader"/> plus <see cref="ScoreRecordCalculator"/>:
/// single-round maxima decode the immutable compact payloads (the same bytes
/// used by exact-round replay) while stage/season totals use normalized
/// standings; nothing is resimulated. History reads persisted
/// <c>new_record</c> story events. Read-only: no lock, no RNG, no mutation.
/// </summary>
public sealed class GetRecordsHandler
{
    public const int HistoryTake = 20;

    private readonly SaveStore _store;

    public GetRecordsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetRecordsResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        RecordLoader.RecordInputs inputs = await RecordLoader.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            inputs.AthleteNames,
            inputs.SeasonStandings,
            inputs.StageStandings,
            inputs.Memberships,
            inputs.Careers,
            inputs.Promotions);
        ScoreRecordLoader.ScoringInputs scoringInputs = await ScoreRecordLoader.LoadAsync(
            context, inputs.AthleteNames, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> scoringComputed = ScoreRecordCalculator.ComputeAll(
            scoringInputs.AthleteNames,
            scoringInputs.LeagueRounds,
            scoringInputs.LeagueStages,
            scoringInputs.LeaguePoints,
            scoringInputs.ColourRounds,
            scoringInputs.ColourStages,
            scoringInputs.QualifierRounds,
            scoringInputs.QualifierStages,
            scoringInputs.ColourLegRounds,
            scoringInputs.ColourLegStages,
            scoringInputs.ColourTotals,
            scoringInputs.ColourTeamRounds,
            scoringInputs.TypeLegRounds,
            scoringInputs.TypeLegStages,
            scoringInputs.TypeTotals,
            scoringInputs.TypeTeamRounds);
        List<RecordHistoryEntry> history = await LoadHistoryAsync(context, cancellationToken).ConfigureAwait(false);
        return Map(saveId, computed, scoringComputed, inputs.AthleteNames, history);
    }

    internal static GetRecordsResponse Map(
        Guid saveId,
        IReadOnlyList<RecordCalculator.RecordHolders> computed,
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> scoringComputed,
        Dictionary<int, string> names,
        List<RecordHistoryEntry> history)
    {
        List<RecordEntry> records = new(computed.Count);
        foreach (RecordCalculator.RecordHolders holders in computed)
        {
            records.Add(MapSingle(holders, names));
        }

        List<ScoringRecordEntry> scoring = MapScoring(scoringComputed, names);
        return new GetRecordsResponse(saveId, records, history, scoring);
    }

    internal static RecordEntry MapSingle(
        RecordCalculator.RecordHolders holders,
        Dictionary<int, string> names)
    {
        bool isBonus = RecordKey.IsBonus(holders.RecordKey);
        bool isVacant = holders.HolderAthleteIds.Count == 0;
        List<RecordHolderEntry> entries = new(holders.HolderAthleteIds.Count);
        foreach (int athleteId in holders.HolderAthleteIds)
        {
            names.TryGetValue(athleteId, out string? name);
            entries.Add(new RecordHolderEntry(athleteId, name ?? $"Athlete {athleteId}"));
        }

        return new RecordEntry(
            holders.RecordKey,
            Stories.StoryEventRenderer.FormatRecordKey(holders.RecordKey),
            holders.Value,
            Stories.StoryEventRenderer.FormatRecordValue(holders.RecordKey, holders.Value),
            isBonus,
            isVacant,
            entries);
    }

    internal static List<ScoringRecordEntry> MapScoring(
        IReadOnlyList<ScoreRecordCalculator.ScoringRecordResult> computed,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(computed);
        ArgumentNullException.ThrowIfNull(names);
        List<ScoringRecordEntry> entries = new(computed.Count);
        foreach (ScoreRecordCalculator.ScoringRecordResult result in computed)
        {
            entries.Add(MapScoringSingle(result, names));
        }

        return entries;
    }

    internal static ScoringRecordEntry MapScoringSingle(
        ScoreRecordCalculator.ScoringRecordResult result,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(names);
        bool isVacant = result.Holders.Count == 0;
        List<ScoringRecordHolderEntry> holders = new(result.Holders.Count);
        foreach (ScoreRecordCalculator.ScoringHolder holder in result.Holders)
        {
            string? athleteName = null;
            if (holder.SaveAthleteId is not null)
            {
                names.TryGetValue(holder.SaveAthleteId.Value, out string? resolved);
                athleteName = resolved ?? $"Athlete {holder.SaveAthleteId.Value}";
            }

            string teamName = string.IsNullOrWhiteSpace(holder.TeamKey) ? string.Empty : holder.TeamKey;
            holders.Add(new ScoringRecordHolderEntry(
                holder.SaveAthleteId,
                athleteName,
                holder.TeamKey ?? string.Empty,
                teamName,
                holder.Value,
                ScoreRecordKey.FormatPoints(holder.Value),
                holder.SeasonNumber,
                holder.Competition,
                holder.LeagueName,
                holder.StageNumber,
                holder.RoundNumber,
                holder.GroupNumber));
        }

        return new ScoringRecordEntry(
            result.RecordKey,
            ScoreRecordKey.LabelOf(result.RecordKey),
            ScoreRecordKey.CategoryOf(result.RecordKey),
            ScoreRecordKey.ScopeLabelOf(result.RecordKey),
            result.Value,
            ScoreRecordKey.FormatPoints(result.Value),
            isVacant,
            holders);
    }

    internal static async Task<List<RecordHistoryEntry>> LoadHistoryAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<StoryEventEntity> rows = await context.StoryEvents
            .AsNoTracking()
            .Where(e => e.EventType == Stories.StoryEventType.NewRecord)
            .OrderByDescending(e => e.Id)
            .Take(HistoryTake)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => rows.Select(r => r.SaveAthleteId).Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        List<RecordHistoryEntry> history = new(rows.Count);
        foreach (StoryEventEntity row in rows)
        {
            Stories.StoryEventPayload payload = Stories.StoryEventRenderer.Parse(row.ContextJson);
            names.TryGetValue(row.SaveAthleteId, out string? name);
            history.Add(new RecordHistoryEntry(
                row.SaveAthleteId,
                name ?? payload.AthleteName,
                payload.RecordKey ?? string.Empty,
                payload.RecordValue ?? 0,
                payload.PriorRecordValue ?? 0,
                row.SeasonNumber,
                Stories.StoryEventRenderer.Render(row.EventType, row.ContextJson)));
        }

        return history;
    }
}
