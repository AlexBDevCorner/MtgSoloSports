using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Cups.GetColorCupIndividualResult;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted Color
/// Cup individual event for a completed source season (never resimulates): 32
/// ranked athletes with Cup scores, medals, selection provenance and the
/// official champion, plus round counts, checksum and RNG boundaries for
/// audit/replay. Without <paramref name="sourceSeasonNumber"/> returns the
/// latest resolved Cup. Throws
/// <see cref="ColorCupIndividualResultNotFoundException"/> (404) when the Cup
/// has not been resolved yet, and aborts on corrupt counts.
/// </summary>
public sealed class GetColorCupIndividualResultHandler
{
    private readonly SaveStore _store;

    public GetColorCupIndividualResultHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetColorCupIndividualResultResponse> HandleAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null)
            {
                throw new ColorCupIndividualResultNotFoundException(
                    $"Color Cup individual event for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureResolvedAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
            return explicitSeason;
        }

        List<ColorCupIndividualStandingEntity> any = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new ColorCupIndividualResultNotFoundException("Color Cup individual event has not been resolved yet.");
        }

        int latestSeasonId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latestSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new InvalidOperationException("Color Cup individual event references an unknown season.");
        }

        return latest;
    }

    internal static async Task EnsureResolvedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            throw new ColorCupIndividualResultNotFoundException(
                $"Color Cup individual event for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<GetColorCupIndividualResultResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        List<ColorCupIndividualStandingEntity> standings = await context.ColorCupIndividualStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.CupRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupIndividualRoundEntity> rounds = await context.ColorCupIndividualRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntity> honours = await context.Honours
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ColorCupIndividualInvariants.ValidatePersisted(source, rounds, standings, honours, rules);
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        return MapResponse(saveId, source, standings, rounds, athletes);
    }

    internal static GetColorCupIndividualResultResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        List<ColorCupIndividualStandingEntity> standings,
        List<ColorCupIndividualRoundEntity> rounds,
        Dictionary<int, SaveAthleteEntity> athletes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(athletes);
        ColorCupIndividualRoundPayloadDocument first = ColorCupIndividualRoundPayloadDocument.FromStored(rounds[0].PayloadJson);
        ColorCupIndividualRoundPayloadDocument last = ColorCupIndividualRoundPayloadDocument.FromStored(rounds[^1].PayloadJson);
        string checksum = ComputeChecksum(standings);
        List<GetColorCupIndividualMember> members = new(standings.Count);
        foreach (ColorCupIndividualStandingEntity standing in standings)
        {
            athletes.TryGetValue(standing.SaveAthleteId, out SaveAthleteEntity? athlete);
            string color = ((SportingColor)standing.SportingColor).ToString();
            members.Add(new GetColorCupIndividualMember(
                standing.SaveAthleteId,
                athlete?.Name ?? $"Athlete {standing.SaveAthleteId}",
                color,
                standing.SelectionRank,
                standing.CupRank,
                standing.CupScoreThousandths,
                standing.BaseScoreThousandths,
                standing.RoundWins,
                ((ColorCupMedal)standing.Medal).ToString(),
                athlete?.ImageUrl));
        }

        ColorCupIndividualStandingEntity champion = standings.Single(s => s.CupRank == 1);
        athletes.TryGetValue(champion.SaveAthleteId, out SaveAthleteEntity? championAthlete);
        string? championName = championAthlete?.Name;
        return new GetColorCupIndividualResultResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            standings.Count,
            rounds.Count,
            checksum,
            first.RngBeforeState,
            first.RngBeforeStream,
            last.RngAfterState,
            last.RngAfterStream,
            champion.SaveAthleteId,
            championName ?? $"Athlete {champion.SaveAthleteId}",
            members);
    }

    internal static string ComputeChecksum(List<ColorCupIndividualStandingEntity> standings)
    {
        List<ColorCupIndividualStandingEntity> ordered = standings.OrderBy(s => s.CupRank).ToList();
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (ColorCupIndividualStandingEntity standing in ordered)
        {
            builder.Append(standing.CupRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(standing.SaveAthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(standing.CupScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(standing.BaseScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
