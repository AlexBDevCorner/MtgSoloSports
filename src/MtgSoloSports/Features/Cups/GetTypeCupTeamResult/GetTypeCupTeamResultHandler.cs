using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.GetTypeCupTeamResult;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted Type
/// Cup team event for a completed even source season (never resimulates):
/// N ranked creature-type teams with team scores, medals, selection provenance
/// and the official champion team, plus N x 4 leg standings with group scores,
/// plus round counts, checksum and RNG boundaries for audit/replay. The team
/// count N varies per source season; only the Color Cup has a fixed eight-team
/// field. Without <paramref name="sourceSeasonNumber"/> returns the latest
/// resolved team event. Throws <see cref="TypeCupTeamResultNotFoundException"/>
/// (404) when the team event has not been resolved yet, and aborts on corrupt
/// counts.
/// </summary>
public sealed class GetTypeCupTeamResultHandler
{
    private readonly SaveStore _store;

    public GetTypeCupTeamResultHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetTypeCupTeamResultResponse> HandleAsync(
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
                throw new TypeCupTeamResultNotFoundException(
                    $"Type Cup team event for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureResolvedAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
            return explicitSeason;
        }

        List<TypeCupTeamStandingEntity> any = await context.TypeCupTeamStandings
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new TypeCupTeamResultNotFoundException("Type Cup team event has not been resolved yet.");
        }

        int latestSeasonId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latestSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new InvalidOperationException("Type Cup team event references an unknown season.");
        }

        return latest;
    }

    internal static async Task EnsureResolvedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.TypeCupTeamStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            throw new TypeCupTeamResultNotFoundException(
                $"Type Cup team event for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<GetTypeCupTeamResultResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        RulesV1Snapshot rules = await LoadRulesVersionAsync(context, cancellationToken).ConfigureAwait(false);
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.TeamRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.GroupRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds
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
        TypeCupTeamInvariants.ValidatePersisted(source, rounds, legs, teams, honours, rules.Rules);
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

    internal static GetTypeCupTeamResultResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        List<TypeCupTeamRoundEntity> rounds,
        Dictionary<int, SaveAthleteEntity> athletes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(athletes);
        TypeCupTeamRoundPayloadDocument first = TypeCupTeamRoundPayloadDocument.FromStored(
            rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).First().PayloadJson);
        TypeCupTeamRoundPayloadDocument last = TypeCupTeamRoundPayloadDocument.FromStored(
            rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).Last().PayloadJson);
        string checksum = ComputeChecksum(teams);
        List<GetTypeCupTeamMember> teamMembers = MapTeams(teams);
        List<GetTypeCupTeamLegMember> legMembers = MapLegs(legs, athletes);
        TypeCupTeamStandingEntity champion = teams.Single(s => s.TeamRank == 1);
        return new GetTypeCupTeamResultResponse(
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
            champion.CreatureType,
            champion.CreatureType,
            teamMembers,
            legMembers);
    }

    internal static List<GetTypeCupTeamMember> MapTeams(List<TypeCupTeamStandingEntity> teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        List<GetTypeCupTeamMember> members = new(teams.Count);
        foreach (TypeCupTeamStandingEntity team in teams.OrderBy(t => t.TeamRank))
        {
            members.Add(new GetTypeCupTeamMember(
                team.CreatureType,
                team.CreatureType,
                team.TeamRank,
                team.TeamScoreThousandths,
                team.TeamBaseThousandths,
                team.GroupWins,
                team.RoundWins,
                ((TypeCupMedal)team.Medal).ToString()));
        }

        return members;
    }

    internal static List<GetTypeCupTeamLegMember> MapLegs(
        List<TypeCupTeamGroupStandingEntity> legs,
        Dictionary<int, SaveAthleteEntity> athletes)
    {
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(athletes);
        List<GetTypeCupTeamLegMember> members = new(legs.Count);
        foreach (TypeCupTeamGroupStandingEntity leg in legs.OrderBy(l => l.GroupNumber).ThenBy(l => l.GroupRank))
        {
            athletes.TryGetValue(leg.SaveAthleteId, out SaveAthleteEntity? athlete);
            members.Add(new GetTypeCupTeamLegMember(
                leg.SaveAthleteId,
                athlete?.Name ?? $"Athlete {leg.SaveAthleteId}",
                leg.CreatureType,
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

    internal static string ComputeChecksum(List<TypeCupTeamStandingEntity> teams)
    {
        List<TypeCupTeamStandingEntity> ordered = teams.OrderBy(s => s.TeamRank).ToList();
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (TypeCupTeamStandingEntity team in ordered)
        {
            builder.Append(team.TeamRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(team.CreatureType);
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
