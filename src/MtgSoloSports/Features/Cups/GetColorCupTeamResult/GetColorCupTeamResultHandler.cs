using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Cups.GetColorCupTeamResult;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted Color
/// Cup team event for a completed source season (never resimulates): eight
/// ranked color teams with team scores, medals, selection provenance and the
/// official champion team, plus 32 leg standings with group scores, plus round
/// counts, checksum and RNG boundaries for audit/replay. Without
/// <paramref name="sourceSeasonNumber"/> returns the latest resolved team
/// event. Throws <see cref="ColorCupTeamResultNotFoundException"/> (404) when
/// the team event has not been resolved yet, and aborts on corrupt counts.
/// </summary>
public sealed class GetColorCupTeamResultHandler
{
    private readonly SaveStore _store;

    public GetColorCupTeamResultHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetColorCupTeamResultResponse> HandleAsync(
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
                throw new ColorCupTeamResultNotFoundException(
                    $"Color Cup team event for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureResolvedAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
            return explicitSeason;
        }

        List<ColorCupTeamStandingEntity> any = await context.ColorCupTeamStandings
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new ColorCupTeamResultNotFoundException("Color Cup team event has not been resolved yet.");
        }

        int latestSeasonId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latestSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new InvalidOperationException("Color Cup team event references an unknown season.");
        }

        return latest;
    }

    internal static async Task EnsureResolvedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.ColorCupTeamStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            throw new ColorCupTeamResultNotFoundException(
                $"Color Cup team event for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<GetColorCupTeamResultResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        RulesV1Snapshot rules = await LoadRulesVersionAsync(context, cancellationToken).ConfigureAwait(false);
        List<ColorCupTeamStandingEntity> teams = await context.ColorCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.TeamRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.GroupRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ColorCupTeamRoundEntity> rounds = await context.ColorCupTeamRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.RoundNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntity> honours = await context.Honours
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ColorCupTeamInvariants.ValidatePersisted(source, rounds, legs, teams, honours, rules.Rules);
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        return MapResponse(saveId, source, teams, legs, rounds, athletes);
    }

    internal sealed record RulesV1Snapshot(SimulationKernel.Rules.RulesV1 Rules);

    internal static async Task<RulesV1Snapshot> LoadRulesVersionAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        return new RulesV1Snapshot(rules);
    }

    internal static GetColorCupTeamResultResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        List<ColorCupTeamStandingEntity> teams,
        List<ColorCupTeamGroupStandingEntity> legs,
        List<ColorCupTeamRoundEntity> rounds,
        Dictionary<int, SaveAthleteEntity> athletes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(athletes);
        ColorCupTeamRoundPayloadDocument first = ColorCupTeamRoundPayloadDocument.FromStored(
            rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).First().PayloadJson);
        ColorCupTeamRoundPayloadDocument last = ColorCupTeamRoundPayloadDocument.FromStored(
            rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).Last().PayloadJson);
        string checksum = ComputeChecksum(teams);
        List<GetColorCupTeamMember> teamMembers = MapTeams(teams);
        List<GetColorCupTeamLegMember> legMembers = MapLegs(legs, athletes);
        ColorCupTeamStandingEntity champion = teams.Single(s => s.TeamRank == 1);
        string championName = ((SportingColor)champion.SportingColor).ToString();
        return new GetColorCupTeamResultResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            teams.Count,
            4,
            8,
            checksum,
            first.RngBeforeState,
            first.RngBeforeStream,
            last.RngAfterState,
            last.RngAfterStream,
            champion.SportingColor,
            championName,
            teamMembers,
            legMembers);
    }

    internal static List<GetColorCupTeamMember> MapTeams(List<ColorCupTeamStandingEntity> teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        List<GetColorCupTeamMember> members = new(teams.Count);
        foreach (ColorCupTeamStandingEntity team in teams.OrderBy(t => t.TeamRank))
        {
            string name = ((SportingColor)team.SportingColor).ToString();
            members.Add(new GetColorCupTeamMember(
                team.SportingColor,
                name,
                team.TeamRank,
                team.TeamScoreThousandths,
                team.TeamBaseThousandths,
                team.GroupWins,
                team.RoundWins,
                ((ColorCupMedal)team.Medal).ToString()));
        }

        return members;
    }

    internal static List<GetColorCupTeamLegMember> MapLegs(
        List<ColorCupTeamGroupStandingEntity> legs,
        Dictionary<int, SaveAthleteEntity> athletes)
    {
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(athletes);
        List<GetColorCupTeamLegMember> members = new(legs.Count);
        foreach (ColorCupTeamGroupStandingEntity leg in legs.OrderBy(l => l.GroupNumber).ThenBy(l => l.GroupRank))
        {
            athletes.TryGetValue(leg.SaveAthleteId, out SaveAthleteEntity? athlete);
            string color = ((SportingColor)leg.SportingColor).ToString();
            members.Add(new GetColorCupTeamLegMember(
                leg.SaveAthleteId,
                athlete?.Name ?? $"Athlete {leg.SaveAthleteId}",
                color,
                leg.SelectionRank,
                leg.GroupNumber,
                leg.GroupRank,
                leg.GroupScoreThousandths,
                leg.BaseScoreThousandths,
                leg.RoundWins,
                athlete?.ImageUrl));
        }

        return members;
    }

    internal static string ComputeChecksum(List<ColorCupTeamStandingEntity> teams)
    {
        List<ColorCupTeamStandingEntity> ordered = teams.OrderBy(s => s.TeamRank).ToList();
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (ColorCupTeamStandingEntity team in ordered)
        {
            builder.Append(team.TeamRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(team.SportingColor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(team.TeamScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(team.TeamBaseThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
