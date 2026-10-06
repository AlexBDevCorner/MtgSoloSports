using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Superleague.GetQualifierResult;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted
/// Superleague qualifier for a postseason transition (never resimulates):
/// 32 ranked athletes with qualifier scores, role/source provenance and the
/// exactly-8 qualified flag, plus round counts, checksum and RNG boundaries
/// for audit/replay. Without <paramref name="fromSeasonNumber"/> returns the
/// latest resolved qualifier. Throws
/// <see cref="QualifierResultNotFoundException"/> (404) when the qualifier has
/// not been resolved yet, and aborts on corrupt counts.
/// </summary>
public sealed class GetQualifierResultHandler
{
    private readonly SaveStore _store;

    public GetQualifierResultHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetQualifierResultResponse> HandleAsync(
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

    internal static async Task<(SeasonEntity Source, SeasonEntity Next)> LoadSeasonsAsync(
        SaveDbContext context, int? fromSeasonNumber, CancellationToken cancellationToken)
    {
        if (fromSeasonNumber.HasValue)
        {
            SeasonEntity? source = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == fromSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (source is null || !source.HasSuperleague)
            {
                throw new QualifierResultNotFoundException(
                    $"Qualifier for Season {fromSeasonNumber.Value} has not been resolved yet.");
            }

            SeasonEntity? next = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == fromSeasonNumber.Value + 1, cancellationToken)
                .ConfigureAwait(false);
            if (next is null || !next.HasSuperleague)
            {
                throw new QualifierResultNotFoundException(
                    $"Qualifier for Season {fromSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureQualifierExistsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
            return (source, next);
        }

        List<QualifierStandingEntity> any = await context.QualifierStandings
            .AsNoTracking()
            .Where(e => e.QualifierBoundary == (int)SimulationKernel.Leagues.QualifierBoundary.Superleague)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new QualifierResultNotFoundException("Qualifier has not been resolved yet.");
        }

        int latestToSeasonId = any.Max(m => m.ToSeasonId);
        QualifierStandingEntity sample = any.First(m => m.ToSeasonId == latestToSeasonId);
        SeasonEntity? latestNext = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == sample.ToSeasonId, cancellationToken)
            .ConfigureAwait(false);
        SeasonEntity? latestSource = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == sample.FromSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latestNext is null || latestSource is null)
        {
            throw new InvalidOperationException("Qualifier references unknown seasons.");
        }

        return (latestSource, latestNext);
    }

    internal static async Task EnsureQualifierExistsAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        int count = await context.QualifierStandings.CountAsync(
            e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)SimulationKernel.Leagues.QualifierBoundary.Superleague,
            cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            throw new QualifierResultNotFoundException(
                $"Qualifier for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<GetQualifierResultResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        CancellationToken cancellationToken)
    {
        List<QualifierStandingEntity> standings = await LoadStandingsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        List<QualifierRoundEntity> rounds = await LoadRoundsAsync(context, source, next, cancellationToken).ConfigureAwait(false);
        ValidateQualifier(standings, rounds);
        Dictionary<int, string> names = await LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await LoadLeaguesAsync(context, cancellationToken).ConfigureAwait(false);
        List<QualifierStandingMember> members = MapMembers(standings, names, leaguesById);
        return MapResponse(saveId, source, next, standings, rounds, members);
    }

    internal static async Task<List<QualifierStandingEntity>> LoadStandingsAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        return await context.QualifierStandings
            .AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)SimulationKernel.Leagues.QualifierBoundary.Superleague)
            .OrderBy(e => e.QualifierRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<List<QualifierRoundEntity>> LoadRoundsAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next, CancellationToken cancellationToken)
    {
        return await context.QualifierRounds
            .AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && e.QualifierBoundary == (int)SimulationKernel.Leagues.QualifierBoundary.Superleague)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, string>> LoadNamesAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, LeagueEntity>> LoadLeaguesAsync(
        SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static List<QualifierStandingMember> MapMembers(
        List<QualifierStandingEntity> standings,
        Dictionary<int, string> names,
        Dictionary<int, LeagueEntity> leaguesById)
    {
        List<QualifierStandingMember> members = new(standings.Count);
        foreach (QualifierStandingEntity standing in standings)
        {
            members.Add(MapSingleMember(standing, names, leaguesById));
        }

        return members;
    }

    internal static QualifierStandingMember MapSingleMember(
        QualifierStandingEntity standing,
        Dictionary<int, string> names,
        Dictionary<int, LeagueEntity> leaguesById)
    {
        names.TryGetValue(standing.SaveAthleteId, out string? name);
        leaguesById.TryGetValue(standing.FromLeagueId, out LeagueEntity? from);
        string role = ((QualifierRole)standing.Role).ToString();
        string color = ((SportingColor)standing.SportingColor).ToString();
        return new QualifierStandingMember(
            standing.SaveAthleteId,
            name ?? $"Athlete {standing.SaveAthleteId}",
            color,
            role,
            standing.FromLeagueId,
            from?.Name ?? $"League {standing.FromLeagueId}",
            standing.FromSeasonRank,
            standing.QualifierRank,
            standing.QualifierScoreThousandths,
            standing.BaseScoreThousandths,
            standing.RoundWins,
            standing.IsQualified);
    }

    internal static GetQualifierResultResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        SeasonEntity next,
        List<QualifierStandingEntity> standings,
        List<QualifierRoundEntity> rounds,
        List<QualifierStandingMember> members)
    {
        QualifierRoundPayloadDocument first = QualifierRoundPayloadDocument.FromJson(rounds[0].PayloadJson);
        QualifierRoundPayloadDocument last = QualifierRoundPayloadDocument.FromJson(rounds[^1].PayloadJson);
        string checksum = ComputeChecksum(standings);
        int incumbents = standings.Count(s => s.Role == (int)QualifierRole.Incumbent && s.IsQualified);
        int challengers = standings.Count(s => s.Role == (int)QualifierRole.Challenger && s.IsQualified);
        return new GetQualifierResultResponse(
            saveId,
            source.SeasonNumber,
            next.SeasonNumber,
            standings.Count,
            rounds.Count,
            standings.Count(s => s.IsQualified),
            checksum,
            first.RngBeforeState,
            first.RngBeforeStream,
            last.RngAfterState,
            last.RngAfterStream,
            members,
            incumbents,
            challengers,
            rounds.Count);
    }

    internal static void ValidateQualifier(List<QualifierStandingEntity> standings, List<QualifierRoundEntity> rounds)
    {
        if (standings.Count != 32)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly 32 standings, was {standings.Count}.");
        }

        if (rounds.Count != 16)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly 16 rounds, was {rounds.Count}.");
        }

        int qualified = standings.Count(s => s.IsQualified);
        if (qualified != 8)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly 8 successful qualifiers, was {qualified}.");
        }

        HashSet<int> ranks = standings.Select(s => s.QualifierRank).ToHashSet();
        if (!ranks.SetEquals(Enumerable.Range(1, 32)))
        {
            throw new InvalidOperationException("Qualifier must cover ranks 1..32 exactly once.");
        }

        HashSet<int> roundNumbers = rounds.Select(r => r.RoundNumber).ToHashSet();
        if (!roundNumbers.SetEquals(Enumerable.Range(1, 16)))
        {
            throw new InvalidOperationException("Qualifier must cover rounds 1..16 exactly once.");
        }
    }

    internal static string ComputeChecksum(List<QualifierStandingEntity> standings)
    {
        List<QualifierStandingEntity> ordered = standings.OrderBy(s => s.QualifierRank).ToList();
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
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

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
