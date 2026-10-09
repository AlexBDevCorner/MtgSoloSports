using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Qualifiers.GetQualifierRounds;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Read-only access to one
/// feeder qualifier event for Live and history: persisted round list, field
/// provenance (incumbents/challengers, source tiers) and final standings when
/// complete. Pending events (0 rounds) return their computed field so Live can
/// open them before running; completed events in earlier postseasons replay
/// identically via <c>fromSeason</c>. Never locks, never validates as a
/// runner, never resimulates, never consumes RNG.
/// </summary>
public sealed class GetQualifierRoundsHandler
{
    private readonly SaveStore _store;

    public GetQualifierRoundsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetQualifierRoundsResponse> HandleListAsync(
        Guid saveId,
        QualifierBoundary boundary,
        int sportingColor,
        int? fromSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        Validate(saveId, boundary, sportingColor, fromSeasonNumber);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        (SeasonEntity source, SeasonEntity next) = await LoadSeasonsAsync(context, fromSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildListAsync(context, saveId, source, next, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EventRoundView> HandleRoundAsync(
        Guid saveId,
        QualifierBoundary boundary,
        int sportingColor,
        int roundNumber,
        int? fromSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        Validate(saveId, boundary, sportingColor, fromSeasonNumber);
        RulesV1 rules = await LoadRulesAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (roundNumber < 1 || roundNumber > rules.FeederQualifierRounds)
        {
            throw new ArgumentException($"Round must be between 1 and {rules.FeederQualifierRounds}.", nameof(roundNumber));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        (SeasonEntity source, SeasonEntity next) = await LoadSeasonsAsync(context, fromSeasonNumber, cancellationToken).ConfigureAwait(false);
        QualifierRoundEntity row = await context.QualifierRounds.AsNoTracking().SingleOrDefaultAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == sportingColor
                && e.RoundNumber == roundNumber,
            cancellationToken).ConfigureAwait(false)
            ?? throw new QualifierListNotFoundException(
                $"Qualifier {boundary} color {sportingColor} round {roundNumber} for Season {source.SeasonNumber} has not been played yet.");

        BoundaryQualifierRunner.QualifierRoundPayload payload = BoundaryQualifierRunner.QualifierRoundPayload.FromJson(row.PayloadJson);
        EventRoundView baseView = await EventRoundViews.BuildAsync(
            context,
            source.SeasonNumber,
            PostseasonEvents.Qualifier,
            group: null,
            payload.RoundNumber,
            payload.RulesVersion,
            payload.Checksum,
            new Pcg32State(payload.RngBeforeState, payload.RngBeforeStream),
            new Pcg32State(payload.RngAfterState, payload.RngAfterStream),
            payload.Placements,
            cancellationToken).ConfigureAwait(false);
        return baseView with { Title = QualifierIdentity.Display(boundary, sportingColor) };
    }

    internal static void Validate(Guid saveId, QualifierBoundary boundary, int sportingColor, int? fromSeasonNumber)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (boundary != QualifierBoundary.Feeder1Feeder2 && boundary != QualifierBoundary.Feeder2Feeder3)
        {
            throw new ArgumentException($"Boundary must be F1↔F2 or F2↔F3, was {boundary}.", nameof(boundary));
        }

        if (sportingColor < 0 || sportingColor >= 8)
        {
            throw new ArgumentException($"Sporting color must be 0..7, was {sportingColor}.", nameof(sportingColor));
        }

        if (fromSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(fromSeasonNumber));
        }
    }

    internal async Task<RulesV1> LoadRulesAsync(Guid saveId, CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        return await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<(SeasonEntity Source, SeasonEntity Next)> LoadSeasonsAsync(
        SaveDbContext context, int? fromSeasonNumber, CancellationToken cancellationToken)
    {
        if (fromSeasonNumber.HasValue)
        {
            SeasonEntity? source = await context.Seasons.AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == fromSeasonNumber.Value, cancellationToken).ConfigureAwait(false);
            if (source is null || !source.HasSuperleague)
            {
                throw new QualifierListNotFoundException($"No qualifiers for Season {fromSeasonNumber.Value} are available yet.");
            }

            SeasonEntity? next = await context.Seasons.AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == fromSeasonNumber.Value + 1, cancellationToken).ConfigureAwait(false);
            if (next is null || !next.HasSuperleague)
            {
                throw new QualifierListNotFoundException($"No qualifiers for Season {fromSeasonNumber.Value} are available yet.");
            }

            return (source, next);
        }

        // Latest transition with any qualifier rows, else the pending transition
        // (completed source + incomplete next) so Live can open pending events.
        List<QualifierRoundEntity> anyRounds = await context.QualifierRounds.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<QualifierStandingEntity> anyStandings = await context.QualifierStandings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (anyRounds.Count > 0 || anyStandings.Count > 0)
        {
            var all = anyRounds.Cast<object>().Concat(anyStandings).ToList();
            int latestToId = all.OfType<QualifierRoundEntity>().Select(e => e.ToSeasonId)
                .Concat(all.OfType<QualifierStandingEntity>().Select(e => e.ToSeasonId)).Max();
            QualifierRoundEntity? roundSample = anyRounds.FirstOrDefault(e => e.ToSeasonId == latestToId);
            QualifierStandingEntity? standingSample = anyStandings.FirstOrDefault(e => e.ToSeasonId == latestToId);
            int fromId = (roundSample?.FromSeasonId ?? standingSample!.FromSeasonId);
            SeasonEntity? latestNext = await context.Seasons.AsNoTracking().SingleOrDefaultAsync(e => e.Id == latestToId, cancellationToken).ConfigureAwait(false);
            SeasonEntity? latestSource = await context.Seasons.AsNoTracking().SingleOrDefaultAsync(e => e.Id == fromId, cancellationToken).ConfigureAwait(false);
            if (latestNext is null || latestSource is null)
            {
                throw new InvalidOperationException("Qualifier references unknown seasons.");
            }

            return (latestSource, latestNext);
        }

        List<SeasonEntity> candidates = await context.Seasons
            .Where(e => e.IsComplete && e.HasSuperleague)
            .OrderByDescending(e => e.SeasonNumber)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (SeasonEntity candidate in candidates)
        {
            SeasonEntity? successor = await context.Seasons.AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == candidate.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
            if (successor is null || !successor.HasSuperleague || successor.IsComplete)
            {
                continue;
            }

            return (candidate, successor);
        }

        throw new QualifierListNotFoundException("No qualifiers are available yet.");
    }

    internal static async Task<GetQualifierRoundsResponse> BuildListAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        QualifierBoundary boundary,
        int sportingColor,
        CancellationToken cancellationToken)
    {
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        (LeagueEntity upper, LeagueEntity lower) = await BoundaryQualifierRunner.LoadBoundaryLeaguesAsync(
            context, source, boundary, sportingColor, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> upperStandings = await BoundaryQualifierRunner.LoadLeagueStandingsAsync(
            context, source, upper, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonStandingEntity> lowerStandings = await BoundaryQualifierRunner.LoadLeagueStandingsAsync(
            context, source, lower, rules, cancellationToken).ConfigureAwait(false);
        List<SeasonMembershipEntity> sourceMemberships = await BoundaryQualifierRunner.LoadSourceMembershipsAsync(
            context, source, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SeasonMembershipEntity> membershipByAthlete = sourceMemberships.ToDictionary(m => m.SaveAthleteId);
        Dictionary<int, string> namesByAthlete = await BoundaryQualifierRunner.LoadAthleteNamesAsync(context, cancellationToken).ConfigureAwait(false);

        FeederQualifierFieldSelection.FeederField field = FeederQualifierFieldSelection.Select(
            boundary, sportingColor, upperStandings, upper, lowerStandings, lower,
            membershipByAthlete, namesByAthlete, rules);

        List<QualifierRoundEntity> roundRows = await context.QualifierRounds.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == sportingColor)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<QualifierStandingEntity> standingRows = await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == sportingColor)
            .OrderBy(e => e.QualifierRank)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        bool isComplete = standingRows.Count > 0;
        int roundsPlayed = roundRows.Count;
        string checksum = isComplete
            ? GetQualifierListHandler.ComputeChecksum(standingRows)
            : string.Empty;

        Dictionary<int, string> leagueNames = await GetQualifierListHandler.LoadLeagueNamesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, SaveAthleteEntity> cards = await LoadCardsAsync(context, field, cancellationToken).ConfigureAwait(false);
        List<QualifierFieldMember> fieldMembers = MapField(field, leagueNames, cards);
        List<QualifierRoundSummary> rounds = roundRows
            .Select(r => new QualifierRoundSummary(r.RoundNumber, r.RulesVersion, r.PayloadChecksum))
            .ToList();
        List<QualifierEventMember> standings = GetQualifierListHandler.MapMembers(standingRows, namesByAthlete, leagueNames);

        return new GetQualifierRoundsResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            boundary.ToString(),
            (int)boundary,
            sportingColor,
            ((SportingColor)sportingColor).ToString(),
            roundsPlayed,
            rules.FeederQualifierRounds,
            isComplete,
            checksum,
            rounds,
            fieldMembers,
            standings);
    }

    internal static async Task<Dictionary<int, SaveAthleteEntity>> LoadCardsAsync(
        SaveDbContext context,
        FeederQualifierFieldSelection.FeederField field,
        CancellationToken cancellationToken)
    {
        List<int> ids = field.All.Select(p => p.SaveAthleteId).ToList();
        return await context.SaveAthletes.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
    }

    internal static List<QualifierFieldMember> MapField(
        FeederQualifierFieldSelection.FeederField field,
        IReadOnlyDictionary<int, string> leagueNames,
        IReadOnlyDictionary<int, SaveAthleteEntity> cards)
    {
        List<QualifierFieldMember> members = new(field.All.Count);
        foreach (FeederQualifierFieldSelection.FeederPick pick in field.All.OrderBy(p => p.Role).ThenBy(p => p.FromSeasonRank))
        {
            leagueNames.TryGetValue(pick.FromLeagueId, out string? leagueName);
            cards.TryGetValue(pick.SaveAthleteId, out SaveAthleteEntity? card);
            members.Add(new QualifierFieldMember(
                pick.SaveAthleteId,
                pick.Name,
                ((SportingColor)pick.SportingColor).ToString(),
                pick.Role.ToString(),
                pick.FromLeagueId,
                leagueName ?? $"League {pick.FromLeagueId}",
                pick.FromSeasonRank,
                card?.ImageUrl,
                card?.SetCode,
                card?.TypeLine ?? string.Empty));
        }

        return members;
    }
}
