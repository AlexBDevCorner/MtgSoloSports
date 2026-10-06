using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Lists all persisted
/// qualifier events for a postseason transition (never resimulates): 1 for v1,
/// 17 for ordinary tiered transitions. Each entry carries boundary identity,
/// field size, winners, checksum and RNG boundaries for audit/replay.
/// Also serves a single event by boundary + color.
/// </summary>
public sealed class GetQualifierListHandler
{
    private readonly SaveStore _store;

    public GetQualifierListHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetQualifierListResponse> HandleAsync(
        Guid saveId,
        int? fromSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (fromSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(fromSeasonNumber));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        (SeasonEntity source, SeasonEntity next) = await LoadSeasonsAsync(context, fromSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, source, next, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GetQualifierEventResponse> HandleSingleAsync(
        Guid saveId,
        QualifierBoundary boundary,
        int? sportingColor,
        int? fromSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        QualifierIdentity.Validate(boundary, sportingColor);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        (SeasonEntity source, SeasonEntity next) = await LoadSeasonsAsync(context, fromSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildSingleAsync(context, saveId, source, next, boundary, sportingColor, cancellationToken).ConfigureAwait(false);
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
                throw new QualifierListNotFoundException($"No qualifiers for Season {fromSeasonNumber.Value} have been resolved yet.");
            }

            SeasonEntity? next = await context.Seasons.AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == fromSeasonNumber.Value + 1, cancellationToken).ConfigureAwait(false);
            if (next is null || !next.HasSuperleague)
            {
                throw new QualifierListNotFoundException($"No qualifiers for Season {fromSeasonNumber.Value} have been resolved yet.");
            }

            bool hasAny = await context.QualifierStandings.AnyAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id, cancellationToken).ConfigureAwait(false);
            if (!hasAny)
            {
                throw new QualifierListNotFoundException($"No qualifiers for Season {source.SeasonNumber} have been resolved yet.");
            }

            return (source, next);
        }

        List<QualifierStandingEntity> all = await context.QualifierStandings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        if (all.Count == 0)
        {
            throw new QualifierListNotFoundException("No qualifiers have been resolved yet.");
        }

        int latestToSeasonId = all.Max(m => m.ToSeasonId);
        QualifierStandingEntity sample = all.First(m => m.ToSeasonId == latestToSeasonId);
        SeasonEntity? latestNext = await context.Seasons.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == sample.ToSeasonId, cancellationToken).ConfigureAwait(false);
        SeasonEntity? latestSource = await context.Seasons.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == sample.FromSeasonId, cancellationToken).ConfigureAwait(false);
        if (latestNext is null || latestSource is null)
        {
            throw new InvalidOperationException("Qualifier references unknown seasons.");
        }

        return (latestSource, latestNext);
    }

    internal static async Task<GetQualifierListResponse> BuildResponseAsync(
        SaveDbContext context, Guid saveId, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        List<QualifierStandingEntity> standings = await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<QualifierRoundEntity> rounds = await context.QualifierRounds.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var groups = standings
            .GroupBy(s => (s.QualifierBoundary, s.QualifierSportingColor))
            .OrderBy(g => g.Key.QualifierBoundary)
            .ThenBy(g => g.Key.QualifierSportingColor)
            .ToList();

        List<GetQualifierEventResponse> events = new(groups.Count);
        foreach (var group in groups)
        {
            QualifierBoundary boundary = (QualifierBoundary)group.Key.QualifierBoundary;
            int storageColor = group.Key.QualifierSportingColor;
            int? color = boundary == QualifierBoundary.Superleague ? null : storageColor;
            List<QualifierStandingEntity> eventStandings = group.OrderBy(s => s.QualifierRank).ToList();
            List<QualifierRoundEntity> eventRounds = rounds
                .Where(r => r.QualifierBoundary == group.Key.QualifierBoundary
                    && r.QualifierSportingColor == group.Key.QualifierSportingColor)
                .OrderBy(r => r.RoundNumber)
                .ToList();
            events.Add(MapEvent(saveId, source, next, boundary, color, eventStandings, eventRounds));
        }

        return new GetQualifierListResponse(saveId, source.SeasonNumber, next.SeasonNumber, events);
    }

    internal static async Task<GetQualifierEventResponse> BuildSingleAsync(
        SaveDbContext context, Guid saveId, SeasonEntity source, SeasonEntity next,
        QualifierBoundary boundary, int? sportingColor, CancellationToken cancellationToken)
    {
        int storageColor = QualifierIdentity.ToStorageColor(sportingColor);
        List<QualifierStandingEntity> standings = await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == storageColor)
            .OrderBy(e => e.QualifierRank)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (standings.Count == 0)
        {
            string colorText = sportingColor.HasValue
                ? sportingColor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "-";
            throw new QualifierListNotFoundException(
                $"Qualifier {boundary} color {colorText} for Season {source.SeasonNumber} has not been resolved yet.");
        }

        List<QualifierRoundEntity> rounds = await context.QualifierRounds.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)boundary && e.QualifierSportingColor == storageColor)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return MapEvent(saveId, source, next, boundary, sportingColor, standings, rounds);
    }

    internal static GetQualifierEventResponse MapEvent(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        QualifierBoundary boundary,
        int? sportingColor,
        List<QualifierStandingEntity> standings,
        List<QualifierRoundEntity> rounds)
    {
        string colorName = sportingColor.HasValue
            ? ((SportingColor)sportingColor.Value).ToString()
            : "-";
        int winners = standings.Count(s => s.IsQualified);
        string checksum = ComputeChecksum(standings);
        ulong rngBeforeState = 0;
        ulong rngBeforeStream = 0;
        ulong rngAfterState = 0;
        ulong rngAfterStream = 0;
        if (rounds.Count > 0)
        {
            rngBeforeState = unchecked((ulong)rounds[0].RngBeforeState);
            rngBeforeStream = unchecked((ulong)rounds[0].RngBeforeStream);
            rngAfterState = unchecked((ulong)rounds[^1].RngAfterState);
            rngAfterStream = unchecked((ulong)rounds[^1].RngAfterStream);
        }

        List<QualifierEventMember> members = MapMembers(standings);
        return new GetQualifierEventResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            (int)boundary,
            boundary.ToString(),
            sportingColor,
            colorName,
            standings.Count,
            rounds.Count,
            winners,
            checksum,
            rngBeforeState,
            rngBeforeStream,
            rngAfterState,
            rngAfterStream,
            members);
    }

    internal static List<QualifierEventMember> MapMembers(List<QualifierStandingEntity> standings)
    {
        List<QualifierEventMember> members = new(standings.Count);
        foreach (QualifierStandingEntity standing in standings.OrderBy(s => s.QualifierRank))
        {
            members.Add(new QualifierEventMember(
                standing.SaveAthleteId,
                ((SportingColor)standing.SportingColor).ToString(),
                ((QualifierRole)standing.Role).ToString(),
                standing.FromLeagueId,
                standing.FromSeasonRank,
                standing.QualifierRank,
                standing.QualifierScoreThousandths,
                standing.BaseScoreThousandths,
                standing.RoundWins,
                standing.IsQualified));
        }

        return members;
    }

    internal static string ComputeChecksum(List<QualifierStandingEntity> standings)
    {
        List<QualifierStandingEntity> ordered = standings.OrderBy(s => s.QualifierRank).ToList();
        using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
        System.Text.StringBuilder builder = new();
        foreach (QualifierStandingEntity standing in ordered)
        {
            builder.Append(standing.QualifierRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(standing.SaveAthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(standing.QualifierScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(standing.BaseScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
