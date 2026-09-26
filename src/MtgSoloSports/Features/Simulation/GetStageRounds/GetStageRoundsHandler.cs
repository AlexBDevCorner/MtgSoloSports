using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Leagues.CurrentStandings;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Simulation.GetStageRounds;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads all persisted rounds
/// for one league stage from the compact immutable round payloads (never
/// resimulates, never touches RNG state) and enriches each placement with the
/// save-owned card art reference for display. Invariant failures abort the
/// read; corrupted payloads are never silently repaired.
/// </summary>
public sealed class GetStageRoundsHandler
{
    private readonly SaveStore _store;

    public GetStageRoundsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetStageRoundsResponse> HandleAsync(Guid saveId, int leagueId, int stageNumber, CancellationToken cancellationToken = default)
    {
        ValidateIds(saveId, leagueId, stageNumber);

        using SaveDbContext context = _store.OpenDbContext(saveId);
        StageQuery query = await LoadQueryAsync(context, saveId, leagueId, stageNumber, cancellationToken).ConfigureAwait(false);
        ValidateRounds(query.Rounds, query.League, stageNumber, query.Rules);

        Dictionary<int, SaveAthleteEntity> cards = await LoadCardsAsync(context, query.Rounds, cancellationToken).ConfigureAwait(false);
        List<GetStageRound> entries = MapRounds(query.Rounds, cards, query.League);
        return MapResponse(saveId, stageNumber, query, entries);
    }

    internal sealed record StageQuery(
        SeasonEntity Season,
        LeagueEntity League,
        RulesV1 Rules,
        List<RoundEntity> Rounds,
        StageEntity? Stage);

    internal static void ValidateIds(Guid saveId, int leagueId, int stageNumber)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (leagueId <= 0)
        {
            throw new ArgumentException("League id must be positive.", nameof(leagueId));
        }

        if (stageNumber <= 0)
        {
            throw new ArgumentException("Stage number must be positive.", nameof(stageNumber));
        }
    }

    internal static async Task<StageQuery> LoadQueryAsync(
        SaveDbContext context,
        Guid saveId,
        int leagueId,
        int stageNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        if (stageNumber > rules.StagesPerSeason)
        {
            throw new ArgumentException($"Stage number must be between 1 and {rules.StagesPerSeason}.", nameof(stageNumber));
        }

        SeasonEntity season = await AdvanceRoundHandler.LoadSeasonAsync(context, metadata, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await GetCurrentStandingsHandler.LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);

        List<RoundEntity> rounds = await context.Rounds
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.StageNumber == stageNumber)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        StageEntity? stage = await context.Stages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.StageNumber == stageNumber,
                cancellationToken)
            .ConfigureAwait(false);

        return new StageQuery(season, league, rules, rounds, stage);
    }

    internal static GetStageRoundsResponse MapResponse(Guid saveId, int stageNumber, StageQuery query, List<GetStageRound> entries)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(entries);
        int completedRounds = query.Rounds.Count;
        bool isComplete = query.Stage?.IsComplete ?? false;
        if (query.Stage is not null && query.Stage.CompletedRounds != completedRounds)
        {
            throw new InvalidOperationException(
                $"League '{query.League.Name}' stage {stageNumber} cursor says {query.Stage.CompletedRounds} rounds but {completedRounds} round rows exist.");
        }

        return new GetStageRoundsResponse(
            saveId,
            query.Season.SeasonNumber,
            query.League.Id,
            query.League.Name,
            stageNumber,
            completedRounds,
            query.Rules.RoundsPerStage,
            isComplete,
            entries);
    }

    internal static void ValidateRounds(List<RoundEntity> rounds, LeagueEntity league, int stageNumber, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(rules);

        if (rounds.Count > rules.RoundsPerStage)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' stage {stageNumber} has {rounds.Count} persisted rounds, more than {rules.RoundsPerStage}.");
        }

        for (int i = 0; i < rounds.Count; i++)
        {
            RoundEntity round = rounds[i];
            if (round.RoundNumber != i + 1)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {stageNumber} has non-sequential round rows; rounds must advance in order.");
            }

            if (round.StageNumber != stageNumber)
            {
                throw new InvalidOperationException($"Persisted round {round.Id} has corrupt stage identity.");
            }

            RoundPayloadDocument document = RoundPayloadDocument.FromJson(round.PayloadJson);
            if (document.StageNumber != stageNumber || document.RoundNumber != round.RoundNumber)
            {
                throw new InvalidOperationException($"Persisted round {round.Id} has corrupt stage/round identity.");
            }

            if (!string.Equals(document.Checksum, round.PayloadChecksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Persisted round {round.RoundNumber} payload checksum mismatch.");
            }

            if (document.Placements.Count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {stageNumber} round {round.RoundNumber} must contain exactly {rules.LeagueSize} placements, was {document.Placements.Count}.");
            }
        }
    }

    internal static async Task<Dictionary<int, SaveAthleteEntity>> LoadCardsAsync(
        SaveDbContext context,
        List<RoundEntity> rounds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rounds);

        if (rounds.Count == 0)
        {
            return new Dictionary<int, SaveAthleteEntity>();
        }

        RoundPayloadDocument first = RoundPayloadDocument.FromJson(rounds[0].PayloadJson);
        List<int> athleteIds = first.Placements.Select(p => p.AthleteId).ToList();
        return await context.SaveAthletes
            .AsNoTracking()
            .Where(e => athleteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static List<GetStageRound> MapRounds(
        List<RoundEntity> rounds,
        Dictionary<int, SaveAthleteEntity> cards,
        LeagueEntity league)
    {
        List<GetStageRound> entries = new(rounds.Count);
        foreach (RoundEntity round in rounds)
        {
            RoundPayloadDocument document = RoundPayloadDocument.FromJson(round.PayloadJson);
            List<GetStageRoundPlacement> placements = new(document.Placements.Count);
            foreach (RoundPayloadEntry placement in document.Placements.OrderBy(p => p.Position))
            {
                if (!cards.TryGetValue(placement.AthleteId, out SaveAthleteEntity? card))
                {
                    throw new InvalidOperationException(
                        $"League '{league.Name}' round {round.RoundNumber} references unknown athlete {placement.AthleteId}.");
                }

                placements.Add(new GetStageRoundPlacement(
                    placement.AthleteId,
                    placement.Name,
                    placement.Position,
                    placement.BaseThousandths,
                    placement.ActiveBonusThousandths,
                    placement.FinalThousandths,
                    placement.CumulativeBeforeThousandths,
                    placement.CumulativeAfterThousandths,
                    placement.RankBefore,
                    placement.RankAfter,
                    placement.RankMovement,
                    card.ImageUrl,
                    card.SetCode,
                    card.TypeLine));
            }

            entries.Add(new GetStageRound(
                round.RoundNumber,
                round.RulesVersion,
                round.PayloadChecksum,
                placements));
        }

        return entries;
    }
}
