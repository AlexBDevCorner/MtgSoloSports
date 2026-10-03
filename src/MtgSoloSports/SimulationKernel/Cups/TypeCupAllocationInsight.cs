using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Pure deterministic Type Cup allocation together with the standings that
/// explain it. Runs exactly the scoring, grouping and optimal matching of
/// <see cref="TypeCupAllocation.Allocate"/> and additionally reports, for every
/// creature type able to field four athletes, the full type ranking and where
/// each ranked athlete ended up. Presentation uses this to show why an athlete
/// represents a type and who missed out; it never changes the allocation.
/// No RNG, clock, database ordering or floating point.
/// </summary>
public static class TypeCupAllocationInsight
{
    /// <summary>One athlete's position in one creature type's ranking.</summary>
    public sealed record RankedCandidate(
        int TypeRank,
        TypeCupAllocation.ScoredCandidate Candidate,
        string? AssignedType);

    /// <summary>
    /// One creature type with at least four eligible athletes, ranked #1..#N.
    /// <see cref="FieldsTeam"/> is false when the type lost its athletes to
    /// other teams in the maximum-team allocation.
    /// </summary>
    public sealed record TypeStanding(
        string CreatureType,
        bool FieldsTeam,
        IReadOnlyList<RankedCandidate> Ranking);

    public sealed record Result(
        TypeCupAllocation.AllocationResult Allocation,
        IReadOnlyList<TypeStanding> Types,
        int CandidateCount);

    public static Result Allocate(IReadOnlyList<TypeCupAllocation.CandidateRaw> candidates, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rules);
        List<TypeCupAllocation.ScoredCandidate> scored = TypeCupAllocation.ScoreAll(candidates, rules);
        Dictionary<string, List<TypeCupAllocation.ScoredCandidate>> byType = TypeCupAllocation.GroupByType(scored);
        Dictionary<string, List<TypeCupAllocation.ScoredCandidate>> viable = TypeCupAllocation.KeepViable(byType, rules);
        Dictionary<int, List<string>> preferences = TypeCupAllocation.BuildPreferences(scored, byType);
        TypeCupAllocation.AllocationResult allocation = TypeCupAllocation.AllocateOptimal(scored, viable, preferences, rules);
        return new Result(allocation, BuildStandings(viable, allocation), scored.Count);
    }

    internal static List<TypeStanding> BuildStandings(
        Dictionary<string, List<TypeCupAllocation.ScoredCandidate>> viable,
        TypeCupAllocation.AllocationResult allocation)
    {
        ArgumentNullException.ThrowIfNull(viable);
        ArgumentNullException.ThrowIfNull(allocation);
        Dictionary<int, string> assigned = [];
        foreach (TypeCupAllocation.AllocatedTeam team in allocation.Teams)
        {
            foreach (TypeCupAllocation.AllocatedMember member in team.Members)
            {
                assigned[member.AthleteId] = team.CreatureType;
            }
        }

        HashSet<string> fielded = allocation.Teams.Select(t => t.CreatureType).ToHashSet(StringComparer.Ordinal);
        List<TypeStanding> standings = new(viable.Count);
        foreach (string type in viable.Keys.OrderBy(t => t, StringComparer.Ordinal))
        {
            List<TypeCupAllocation.ScoredCandidate> list = viable[type];
            List<RankedCandidate> ranking = new(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                assigned.TryGetValue(list[i].AthleteId, out string? assignedType);
                ranking.Add(new RankedCandidate(i + 1, list[i], assignedType));
            }

            standings.Add(new TypeStanding(type, fielded.Contains(type), ranking));
        }

        return standings;
    }
}
