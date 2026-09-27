using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.History.ListStages;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.History.GetRoundReplay;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Replays exactly one
/// persisted historical round: the ONLY history query that decompresses a round
/// payload, and it decompresses exactly one stored payload via
/// <see cref="RoundPayloadCodec"/>. Season/competition/stage lists and round
/// summaries never touch payloads; this handler never loads RNG state, never
/// advances RNG and never mutates save state. Corrupt payloads abort; missing
/// rounds 404.
/// </summary>
public sealed class GetHistoryRoundReplayHandler
{
    private readonly SaveStore _store;

    public GetHistoryRoundReplayHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetHistoryRoundReplayResponse> HandleAsync(Guid saveId, int seasonNumber, int leagueId, int stageNumber, int roundNumber, CancellationToken cancellationToken = default)
    {
        ValidateIds(saveId, seasonNumber, leagueId, stageNumber, roundNumber);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        ReplayQuery query = await LoadQueryAsync(context, seasonNumber, leagueId, stageNumber, roundNumber, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, query, cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateIds(Guid saveId, int seasonNumber, int leagueId, int stageNumber, int roundNumber)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(seasonNumber));
        }

        if (leagueId <= 0)
        {
            throw new ArgumentException("League id must be positive.", nameof(leagueId));
        }

        if (stageNumber < 1)
        {
            throw new ArgumentException("Stage number must be positive.", nameof(stageNumber));
        }

        if (roundNumber < 1)
        {
            throw new ArgumentException("Round number must be positive.", nameof(roundNumber));
        }
    }

    internal sealed record ReplayQuery(
        SeasonEntity Season,
        LeagueEntity League,
        RulesV1 Rules,
        RoundEntity Round);

    internal static async Task<ReplayQuery> LoadQueryAsync(SaveDbContext context, int seasonNumber, int leagueId, int stageNumber, int roundNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SeasonEntity season = await ListHistoryCompetitionsHandler.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await ListHistoryStagesHandler.LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        ValidateRange(stageNumber, roundNumber, rules);
        RoundEntity round = await LoadRoundAsync(context, season, league, stageNumber, roundNumber, cancellationToken).ConfigureAwait(false);
        return new ReplayQuery(season, league, rules, round);
    }

    internal static void ValidateRange(int stageNumber, int roundNumber, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (stageNumber > rules.StagesPerSeason)
        {
            throw new ArgumentException($"Stage number must be between 1 and {rules.StagesPerSeason}.", nameof(stageNumber));
        }

        if (roundNumber > rules.RoundsPerStage)
        {
            throw new ArgumentException($"Round number must be between 1 and {rules.RoundsPerStage}.", nameof(roundNumber));
        }
    }

    internal static async Task<RoundEntity> LoadRoundAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, int stageNumber, int roundNumber, CancellationToken cancellationToken)
    {
        RoundEntity? round = await context.Rounds
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.StageNumber == stageNumber && e.RoundNumber == roundNumber,
                cancellationToken)
            .ConfigureAwait(false);
        if (round is null)
        {
            throw new HistoryNotFoundException($"Round {roundNumber} of stage {stageNumber} for league '{league.Name}' in season {season.SeasonNumber} has not been simulated.");
        }

        return round;
    }

    internal static async Task<GetHistoryRoundReplayResponse> BuildResponseAsync(SaveDbContext context, Guid saveId, ReplayQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        RoundPayloadDocument document = RoundPayloadCodec.DecodeRound(query.Round.PayloadJson);
        ValidateReplay(query.Round, document, query.League, query.Rules);
        Dictionary<int, SaveAthleteEntity> cards = await LoadCardsAsync(context, document, cancellationToken).ConfigureAwait(false);
        List<HistoryRoundPlacement> placements = MapPlacements(document, cards, query.League);
        return MapResponse(saveId, query, document, placements);
    }

    internal static GetHistoryRoundReplayResponse MapResponse(Guid saveId, ReplayQuery query, RoundPayloadDocument document, List<HistoryRoundPlacement> placements)
    {
        return new GetHistoryRoundReplayResponse(
            saveId,
            document.SeasonNumber,
            query.League.Id,
            query.League.Name,
            document.StageNumber,
            document.RoundNumber,
            document.RulesVersion,
            document.Checksum,
            document.RngBeforeState,
            document.RngBeforeStream,
            document.RngAfterState,
            document.RngAfterStream,
            placements);
    }

    internal static void ValidateReplay(RoundEntity round, RoundPayloadDocument document, LeagueEntity league, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(rules);

        if (document.StageNumber != round.StageNumber || document.RoundNumber != round.RoundNumber)
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
                $"League '{league.Name}' stage {round.StageNumber} round {round.RoundNumber} must contain exactly {rules.LeagueSize} placements, was {document.Placements.Count}.");
        }

        HashSet<int> positions = document.Placements.Select(p => p.Position).ToHashSet();
        if (!positions.SetEquals(Enumerable.Range(1, rules.LeagueSize)))
        {
            throw new InvalidOperationException($"Persisted round {round.RoundNumber} has corrupt finishing positions.");
        }
    }

    internal static async Task<Dictionary<int, SaveAthleteEntity>> LoadCardsAsync(SaveDbContext context, RoundPayloadDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(document);
        List<int> athleteIds = document.Placements.Select(p => p.AthleteId).ToList();
        return await context.SaveAthletes
            .AsNoTracking()
            .Where(e => athleteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static List<HistoryRoundPlacement> MapPlacements(RoundPayloadDocument document, Dictionary<int, SaveAthleteEntity> cards, LeagueEntity league)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(league);
        List<HistoryRoundPlacement> placements = new(document.Placements.Count);
        foreach (RoundPayloadEntry placement in document.Placements.OrderBy(p => p.Position))
        {
            placements.Add(MapPlacement(placement, cards, league, document.RoundNumber));
        }

        return placements;
    }

    internal static HistoryRoundPlacement MapPlacement(RoundPayloadEntry placement, Dictionary<int, SaveAthleteEntity> cards, LeagueEntity league, int roundNumber)
    {
        if (!cards.TryGetValue(placement.AthleteId, out SaveAthleteEntity? card))
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' round {roundNumber} references unknown athlete {placement.AthleteId}.");
        }

        return new HistoryRoundPlacement(
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
            card.TypeLine);
    }
}
