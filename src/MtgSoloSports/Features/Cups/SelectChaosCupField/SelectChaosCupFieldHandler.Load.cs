using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;

namespace MtgSoloSports.Features.Cups.SelectChaosCupField;

public sealed partial class SelectChaosCupFieldHandler
{
    internal static async Task<SeasonEntity> LoadSourceSeasonAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (sourceSeasonNumber.HasValue)
        {
            return await LoadExplicitSeasonAsync(context, sourceSeasonNumber.Value, cancellationToken).ConfigureAwait(false);
        }

        SeasonEntity? latest = await context.Seasons
            .Where(e => e.IsComplete)
            .OrderByDescending(e => e.SeasonNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new SelectChaosCupFieldConflictException("No completed season is ready for Chaos Cup field selection.");
        }

        return latest;
    }

    private static async Task<SeasonEntity> LoadExplicitSeasonAsync(
        SaveDbContext context,
        int sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        SeasonEntity? season = await context.Seasons
            .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber, cancellationToken)
            .ConfigureAwait(false);
        if (season is null)
        {
            throw new SelectChaosCupFieldConflictException(
                $"Season {sourceSeasonNumber} does not exist for Chaos Cup field selection.");
        }

        return season;
    }

    internal static void EnsureComplete(SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.IsComplete)
        {
            throw new SelectChaosCupFieldConflictException(
                $"Season {source.SeasonNumber} is not complete; the Chaos Cup field runs after all 32 league stages.");
        }
    }

    internal static async Task<FieldInputs> LoadFieldInputsAsync(
        SaveDbContext context,
        SeasonEntity source,
        SimulationKernel.Rules.RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await LoadMembershipsAsync(context, source, rules, cancellationToken).ConfigureAwait(false);
        Dictionary<int, (int Kind, int Division)> leagueMeta = await LoadLeagueMetaAsync(context, source, cancellationToken).ConfigureAwait(false);
        AthleteMaps maps = await LoadAthleteMapsAsync(context, memberships, cancellationToken).ConfigureAwait(false);
        EnsureMembershipColors(memberships, maps, leagueMeta, source);
        return BuildFieldInputs(memberships, leagueMeta, maps);
    }

    private static async Task<List<SeasonMembershipEntity>> LoadMembershipsAsync(
        SaveDbContext context,
        SeasonEntity source,
        SimulationKernel.Rules.RulesV1 rules,
        CancellationToken cancellationToken)
    {
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (memberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Season {source.SeasonNumber} must have exactly {rules.TotalAthletesInSave} memberships, was {memberships.Count}.");
        }

        HashSet<int> seen = new();
        foreach (SeasonMembershipEntity membership in memberships)
        {
            if (membership.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Season {source.SeasonNumber} has corrupt athlete id {membership.SaveAthleteId}.");
            }

            if (!seen.Add(membership.SaveAthleteId))
            {
                throw new InvalidOperationException($"Season {source.SeasonNumber} has duplicate membership for athlete {membership.SaveAthleteId}.");
            }
        }

        return memberships;
    }

    private static async Task<Dictionary<int, (int Kind, int Division)>> LoadLeagueMetaAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return leagues.ToDictionary(l => l.Id, l => (l.Kind, l.FeederDivision));
    }

    internal sealed record AthleteMaps(
        Dictionary<int, string> Names,
        Dictionary<int, SportingColor> Colors);

    private static async Task<AthleteMaps> LoadAthleteMapsAsync(
        SaveDbContext context,
        List<SeasonMembershipEntity> memberships,
        CancellationToken cancellationToken)
    {
        List<int> ids = memberships.Select(m => m.SaveAthleteId).ToList();
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        if (athletes.Count != ids.Count)
        {
            throw new InvalidOperationException("Source season references unknown athletes.");
        }

        Dictionary<int, string> names = new(athletes.Count);
        Dictionary<int, SportingColor> colors = new(athletes.Count);
        foreach ((int id, SaveAthleteEntity athlete) in athletes)
        {
            if (string.IsNullOrWhiteSpace(athlete.Name))
            {
                throw new InvalidOperationException($"Save athlete {id} has an empty name.");
            }

            if (!Enum.IsDefined((SportingColor)athlete.SportingColor))
            {
                throw new InvalidOperationException($"Save athlete {id} has unknown sporting color {athlete.SportingColor}.");
            }

            names[id] = athlete.Name;
            colors[id] = (SportingColor)athlete.SportingColor;
        }

        return new AthleteMaps(names, colors);
    }

    private static void EnsureMembershipColors(
        List<SeasonMembershipEntity> memberships,
        AthleteMaps maps,
        Dictionary<int, (int Kind, int Division)> leagueMeta,
        SeasonEntity source)
    {
        foreach (SeasonMembershipEntity membership in memberships)
        {
            if (!maps.Colors.TryGetValue(membership.SaveAthleteId, out SportingColor permanent))
            {
                throw new InvalidOperationException($"Season {source.SeasonNumber} references unknown athlete {membership.SaveAthleteId}.");
            }

            if (membership.SportingColor != (int)permanent)
            {
                throw new InvalidOperationException(
                    $"Season {source.SeasonNumber} membership for athlete {membership.SaveAthleteId} has color {(SportingColor)membership.SportingColor} mismatching permanent color {permanent}.");
            }

            if (membership.LeagueId.HasValue && !leagueMeta.ContainsKey(membership.LeagueId.Value))
            {
                throw new InvalidOperationException(
                    $"Season {source.SeasonNumber} membership for athlete {membership.SaveAthleteId} references unknown league {membership.LeagueId.Value}.");
            }
        }
    }

    private static FieldInputs BuildFieldInputs(
        List<SeasonMembershipEntity> memberships,
        Dictionary<int, (int Kind, int Division)> leagueMeta,
        AthleteMaps maps)
    {
        List<ActiveMember> actives = memberships
            .Where(m => m.LeagueId.HasValue)
            .Select(m => new ActiveMember(m.SaveAthleteId, m.LeagueId!.Value))
            .ToList();
        Dictionary<SportingColor, int> activePerColor = CountActives(actives, maps);
        Dictionary<SportingColor, IReadOnlyList<SelectChaosCupFieldSelection.PoolCandidate>> poolByColor =
            BuildPool(memberships, maps);
        Dictionary<SportingColor, int> poolAvailable = poolByColor.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        IReadOnlyDictionary<SportingColor, int> required = ChaosCupFormat.ComputePoolInvitations(activePerColor);
        ChaosCupFormat.ValidatePoolAvailability(activePerColor, poolAvailable);
        return new FieldInputs(activePerColor, poolAvailable, required, poolByColor, actives, leagueMeta, maps.Names, maps.Colors);
    }

    private static Dictionary<SportingColor, int> CountActives(List<ActiveMember> actives, AthleteMaps maps)
    {
        Dictionary<SportingColor, int> counts = Enum.GetValues<SportingColor>().ToDictionary(c => c, _ => 0);
        foreach (ActiveMember active in actives)
        {
            SportingColor color = maps.Colors[active.SaveAthleteId];
            counts[color] = checked(counts[color] + 1);
        }

        return counts;
    }

    private static Dictionary<SportingColor, IReadOnlyList<SelectChaosCupFieldSelection.PoolCandidate>> BuildPool(
        List<SeasonMembershipEntity> memberships,
        AthleteMaps maps)
    {
        Dictionary<SportingColor, List<SelectChaosCupFieldSelection.PoolCandidate>> grouped = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            grouped[color] = [];
        }

        foreach (SeasonMembershipEntity membership in memberships.Where(m => !m.LeagueId.HasValue))
        {
            SportingColor color = maps.Colors[membership.SaveAthleteId];
            grouped[color].Add(new SelectChaosCupFieldSelection.PoolCandidate(
                membership.SaveAthleteId, maps.Names[membership.SaveAthleteId], color));
        }

        return grouped.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<SelectChaosCupFieldSelection.PoolCandidate>)kv.Value);
    }
}
