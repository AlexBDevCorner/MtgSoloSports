using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Pure deterministic Type Cup team allocation (Game Rules §15, Technical Design §17).
/// Every creature type capable of fielding four distinct currently active athletes
/// may participate; there is no artificial team limit and each type fields at most
/// one team of four. Capped athletes may represent only their permanent nationality;
/// uncapped athletes may initially represent any printed creature type and prefer
/// the type where they rank higher (for example #1 Wizard over #4 Human).
/// Permanent nationality is eligibility only: it restricts which teams a capped
/// athlete may join and never grants selection priority. Allocation is an exact
/// deterministic global maximum: it maximizes the number of valid
/// four-distinct-athlete teams via branch-and-bound over type subsets with
/// max-flow feasibility (Edmonds-Karp, ordinal deterministic) and backtracking, so
/// overlapping type sets never lose a feasible team to a greedy commitment. Type-name
/// ordinal breaks cardinality ties; within the optimal team set, a deterministic
/// min-cost flow prefers the strongest legal squads first (global selection rating,
/// then per-type rank), then relative type preference to resolve competing placements,
/// then ordinal tie-breakers. All ordering is ordinal by name then athlete id; all
/// scoring is checked fixed-point integers via <see cref="SelectionScore.Combine"/>
/// with the snapshot Cup weights normalized globally across the candidate pool
/// (tied components normalize to 1000).
/// No RNG, clock, GUID ordering, database ordering, double/float or filesystem.
/// Preview allocation never sets permanent nationality; nationality becomes
/// permanent only when an athlete actually participates (a later slice).
/// </summary>
public static class TypeCupAllocation
{
    public sealed record CandidateRaw(
        int AthleteId,
        string Name,
        IReadOnlyList<string> PrintedTypes,
        string? CappedNationality,
        int BonusRawThousandths,
        int PerformanceRawThousandths,
        int FormRaw,
        int PrestigeRaw);

    public sealed record ScoredCandidate(
        int AthleteId,
        string Name,
        IReadOnlyList<string> EligibleTypes,
        string? CappedNationality,
        int BonusRawThousandths,
        int PerformanceRawThousandths,
        int FormRaw,
        int PrestigeRaw,
        int BonusNormThousandths,
        int PerformanceNormThousandths,
        int FormNormThousandths,
        int PrestigeNormThousandths,
        int FinalRatingThousandths);

    public sealed record AllocatedMember(
        int AthleteId,
        string Name,
        int SelectionRank,
        int TypeRank,
        int FinalRatingThousandths,
        int BonusNormThousandths,
        int PerformanceNormThousandths,
        int FormNormThousandths,
        int PrestigeNormThousandths,
        int BonusRawThousandths,
        int PerformanceRawThousandths,
        int FormRaw,
        int PrestigeRaw);

    public sealed record AllocatedTeam(
        string CreatureType,
        IReadOnlyList<AllocatedMember> Members);

    public sealed record AllocationResult(
        IReadOnlyList<AllocatedTeam> Teams,
        IReadOnlyList<int> UnassignedAthleteIds);

    /// <summary>
    /// Scores every candidate globally and allocates at most one four-athlete team
    /// per creature type. Candidates with no eligible type are reported as
    /// unassigned rather than aborting. Types with fewer than four eligible
    /// candidates never participate. The allocation is the exact maximum-cardinality
    /// feasible team set (deterministic branch-and-bound with max-flow); it never
    /// commits teams greedily without backtracking.
    /// </summary>
    public static AllocationResult Allocate(IReadOnlyList<CandidateRaw> candidates, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rules);
        List<ScoredCandidate> scored = ScoreAll(candidates, rules);
        Dictionary<string, List<ScoredCandidate>> byType = GroupByType(scored);
        Dictionary<string, List<ScoredCandidate>> viable = KeepViable(byType, rules);
        Dictionary<int, List<string>> preferences = BuildPreferences(scored, byType);
        return AllocateOptimal(scored, viable, preferences, rules);
    }

    /// <summary>
    /// Returns the eligible creature types for one candidate: the permanent
    /// nationality alone when capped, otherwise the distinct trimmed printed types.
    /// Empty or whitespace-only printed types are ignored.
    /// </summary>
    public static IReadOnlyList<string> EligibleTypesFor(CandidateRaw candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateSingleCandidate(candidate);
        if (!string.IsNullOrWhiteSpace(candidate.CappedNationality))
        {
            return [candidate.CappedNationality.Trim()];
        }

        SortedSet<string> distinct = new(StringComparer.Ordinal);
        foreach (string printed in candidate.PrintedTypes)
        {
            if (string.IsNullOrWhiteSpace(printed))
            {
                continue;
            }

            distinct.Add(printed.Trim());
        }

        return [.. distinct];
    }

    internal static List<ScoredCandidate> ScoreAll(IReadOnlyList<CandidateRaw> candidates, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rules);
        if (candidates.Count == 0)
        {
            return [];
        }

        HashSet<int> ids = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (CandidateRaw candidate in candidates)
        {
            ValidateSingleCandidate(candidate);
            if (!ids.Add(candidate.AthleteId))
            {
                throw new InvalidOperationException($"Type Cup allocation contains duplicate athlete {candidate.AthleteId}.");
            }

            if (!names.Add(candidate.Name))
            {
                throw new InvalidOperationException($"Type Cup allocation contains duplicate athlete name '{candidate.Name}'.");
            }
        }

        GlobalRange range = ComputeGlobalRange(candidates);
        List<ScoredCandidate> scored = new(candidates.Count);
        foreach (CandidateRaw candidate in candidates)
        {
            IReadOnlyList<string> eligible = EligibleTypesFor(candidate);
            int bonusNorm = ColorCupSelection.Normalize(candidate.BonusRawThousandths, range.BonusMin, range.BonusMax);
            int perfNorm = ColorCupSelection.Normalize(candidate.PerformanceRawThousandths, range.PerformanceMin, range.PerformanceMax);
            int formNorm = ColorCupSelection.Normalize(candidate.FormRaw, range.FormMin, range.FormMax);
            int prestigeNorm = ColorCupSelection.Normalize(candidate.PrestigeRaw, range.PrestigeMin, range.PrestigeMax);
            SelectionScore final = SelectionScore.Combine(
                bonusNorm, perfNorm, formNorm, prestigeNorm,
                rules.CupBonusWeightPermille, rules.CupPerformanceWeightPermille,
                rules.CupFormWeightPermille, rules.CupPrestigeWeightPermille);
            scored.Add(new ScoredCandidate(
                candidate.AthleteId, candidate.Name, eligible,
                string.IsNullOrWhiteSpace(candidate.CappedNationality) ? null : candidate.CappedNationality.Trim(),
                candidate.BonusRawThousandths, candidate.PerformanceRawThousandths, candidate.FormRaw, candidate.PrestigeRaw,
                bonusNorm, perfNorm, formNorm, prestigeNorm, final.Thousandths));
        }

        scored.Sort(CompareScored);
        return scored;
    }

    internal sealed record GlobalRange(
        int BonusMin, int BonusMax,
        int PerformanceMin, int PerformanceMax,
        int FormMin, int FormMax,
        int PrestigeMin, int PrestigeMax);

    internal static void ValidateSingleCandidate(CandidateRaw candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Type Cup candidate has corrupt athlete {candidate.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(candidate.Name))
        {
            throw new InvalidOperationException($"Type Cup candidate {candidate.AthleteId} has an empty name.");
        }

        if (candidate.PrintedTypes is null)
        {
            throw new ArgumentNullException(nameof(candidate), "Printed types must not be null.");
        }

        foreach (string printed in candidate.PrintedTypes)
        {
            if (printed is null)
            {
                throw new InvalidOperationException($"Type Cup candidate '{candidate.Name}' has a null creature type.");
            }
        }

        if (candidate.BonusRawThousandths < 0 || candidate.PerformanceRawThousandths < 0
            || candidate.FormRaw < 0 || candidate.PrestigeRaw < 0)
        {
            throw new InvalidOperationException($"Type Cup candidate '{candidate.Name}' has corrupt negative selection inputs.");
        }

        if (candidate.CappedNationality is not null && string.IsNullOrWhiteSpace(candidate.CappedNationality))
        {
            throw new InvalidOperationException($"Type Cup candidate '{candidate.Name}' has a corrupt empty nationality.");
        }
    }

    internal static GlobalRange ComputeGlobalRange(IReadOnlyList<CandidateRaw> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return new GlobalRange(
            candidates.Min(c => c.BonusRawThousandths), candidates.Max(c => c.BonusRawThousandths),
            candidates.Min(c => c.PerformanceRawThousandths), candidates.Max(c => c.PerformanceRawThousandths),
            candidates.Min(c => c.FormRaw), candidates.Max(c => c.FormRaw),
            candidates.Min(c => c.PrestigeRaw), candidates.Max(c => c.PrestigeRaw));
    }

    internal static int CompareScored(ScoredCandidate left, ScoredCandidate right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        int final = right.FinalRatingThousandths.CompareTo(left.FinalRatingThousandths);
        if (final != 0)
        {
            return final;
        }

        int bonus = right.BonusNormThousandths.CompareTo(left.BonusNormThousandths);
        if (bonus != 0)
        {
            return bonus;
        }

        int perf = right.PerformanceNormThousandths.CompareTo(left.PerformanceNormThousandths);
        if (perf != 0)
        {
            return perf;
        }

        int form = right.FormNormThousandths.CompareTo(left.FormNormThousandths);
        if (form != 0)
        {
            return form;
        }

        int prestige = right.PrestigeNormThousandths.CompareTo(left.PrestigeNormThousandths);
        if (prestige != 0)
        {
            return prestige;
        }

        int name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        if (name != 0)
        {
            return name;
        }

        return left.AthleteId.CompareTo(right.AthleteId);
    }

    internal static Dictionary<string, List<ScoredCandidate>> GroupByType(List<ScoredCandidate> scored)
    {
        ArgumentNullException.ThrowIfNull(scored);
        Dictionary<string, List<ScoredCandidate>> byType = new(StringComparer.Ordinal);
        foreach (ScoredCandidate candidate in scored)
        {
            foreach (string type in candidate.EligibleTypes)
            {
                if (!byType.TryGetValue(type, out List<ScoredCandidate>? list))
                {
                    list = [];
                    byType[type] = list;
                }

                list.Add(candidate);
            }
        }

        foreach (List<ScoredCandidate> list in byType.Values)
        {
            list.Sort(CompareScored);
        }

        return byType;
    }

    internal static Dictionary<string, List<ScoredCandidate>> KeepViable(
        Dictionary<string, List<ScoredCandidate>> byType, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(byType);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<string, List<ScoredCandidate>> viable = new(StringComparer.Ordinal);
        foreach ((string type, List<ScoredCandidate> list) in byType)
        {
            if (list.Count >= rules.TypeCupMinTeamSize)
            {
                viable[type] = list;
            }
        }

        return viable;
    }

    internal static Dictionary<int, List<string>> BuildPreferences(
        List<ScoredCandidate> scored, Dictionary<string, List<ScoredCandidate>> byType)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(byType);
        Dictionary<int, Dictionary<string, int>> rankByAthlete = [];
        foreach ((string type, List<ScoredCandidate> list) in byType)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!rankByAthlete.TryGetValue(list[i].AthleteId, out Dictionary<string, int>? ranks))
                {
                    ranks = new Dictionary<string, int>(StringComparer.Ordinal);
                    rankByAthlete[list[i].AthleteId] = ranks;
                }

                ranks[type] = i + 1;
            }
        }

        Dictionary<int, List<string>> preferences = [];
        foreach (ScoredCandidate candidate in scored)
        {
            if (!rankByAthlete.TryGetValue(candidate.AthleteId, out Dictionary<string, int>? ranks))
            {
                preferences[candidate.AthleteId] = [];
                continue;
            }

            List<string> ordered = [.. candidate.EligibleTypes];
            ordered.Sort((left, right) =>
            {
                int rank = ranks[left].CompareTo(ranks[right]);
                return rank != 0 ? rank : string.Compare(left, right, StringComparison.Ordinal);
            });
            preferences[candidate.AthleteId] = ordered;
        }

        return preferences;
    }

    internal static AllocationResult AllocateGreedy(
        List<ScoredCandidate> scored,
        Dictionary<string, List<ScoredCandidate>> viable,
        Dictionary<int, List<string>> preferences,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(viable);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(rules);

        Dictionary<int, ScoredCandidate> byId = scored.ToDictionary(c => c.AthleteId);
        Dictionary<string, int> rankLookup = BuildRankLookup(viable);
        HashSet<int> remaining = scored.Select(c => c.AthleteId).ToHashSet();
        foreach (ScoredCandidate candidate in scored)
        {
            if (candidate.EligibleTypes.Count == 0)
            {
                remaining.Remove(candidate.AthleteId);
            }
        }

        HashSet<string> open = viable.Keys.ToHashSet(StringComparer.Ordinal);
        List<AllocatedTeam> teams = [];

        while (true)
        {
            string? next = PickNextType(open, viable, remaining, rules);
            if (next is null)
            {
                break;
            }

            List<ScoredCandidate> pool = viable[next]
                .Where(c => remaining.Contains(c.AthleteId))
                .ToList();
            List<ScoredCandidate> picked = PickFour(next, pool, remaining, open, preferences, rankLookup, byId);
            List<AllocatedMember> members = OrderTeamMembers(next, picked, rankLookup);
            teams.Add(new AllocatedTeam(next, members));
            foreach (AllocatedMember member in members)
            {
                remaining.Remove(member.AthleteId);
            }

            open.Remove(next);
        }

        teams.Sort(static (left, right) => string.Compare(left.CreatureType, right.CreatureType, StringComparison.Ordinal));
        List<int> unassigned = scored
            .Where(c => !teams.Any(t => t.Members.Any(m => m.AthleteId == c.AthleteId)))
            .Select(c => c.AthleteId)
            .ToList();
        return new AllocationResult(teams, unassigned);
    }

    internal static Dictionary<string, int> BuildRankLookup(Dictionary<string, List<ScoredCandidate>> viable)
    {
        ArgumentNullException.ThrowIfNull(viable);
        Dictionary<string, int> lookup = new(StringComparer.Ordinal);
        foreach ((string type, List<ScoredCandidate> list) in viable)
        {
            for (int i = 0; i < list.Count; i++)
            {
                lookup[type + "\u0000" + list[i].AthleteId] = i + 1;
            }
        }

        return lookup;
    }

    internal static int TypeRankOf(string type, int athleteId, Dictionary<string, int> lookup)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(lookup);
        return lookup.TryGetValue(type + "\u0000" + athleteId, out int rank) ? rank : int.MaxValue;
    }

    internal static string? PickNextType(
        HashSet<string> open,
        Dictionary<string, List<ScoredCandidate>> viable,
        HashSet<int> remaining,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(viable);
        ArgumentNullException.ThrowIfNull(remaining);
        ArgumentNullException.ThrowIfNull(rules);
        string? best = null;
        int bestCount = int.MaxValue;
        foreach (string type in open)
        {
            int count = viable[type].Count(c => remaining.Contains(c.AthleteId));
            if (count < rules.TypeCupMinTeamSize)
            {
                continue;
            }

            if (best is null || count < bestCount
                || (count == bestCount && string.Compare(type, best, StringComparison.Ordinal) < 0))
            {
                best = type;
                bestCount = count;
            }
        }

        return best;
    }

    internal static List<ScoredCandidate> PickFour(
        string type,
        List<ScoredCandidate> pool,
        HashSet<int> remaining,
        HashSet<string> open,
        Dictionary<int, List<string>> preferences,
        Dictionary<string, int> rankLookup,
        Dictionary<int, ScoredCandidate> byId)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(remaining);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(rankLookup);
        ArgumentNullException.ThrowIfNull(byId);
        if (pool.Count < 4)
        {
            throw new InvalidOperationException($"Type Cup type '{type}' has only {pool.Count} remaining candidates.");
        }

        // Legacy greedy helper kept consistent with AllocateOptimal: permanent
        // nationality is eligibility only (expressed by the pool), so capped status
        // grants no priority. Strongest selection ordering first, then relative
        // preference, then fewest remaining alternatives, all ordinal deterministic.
        List<ScoredCandidate> ordered = [.. pool];
        ordered.Sort((left, right) =>
        {
            int quality = CompareScored(left, right);
            if (quality != 0)
            {
                return quality;
            }

            int leftPref = PreferenceIndex(left.AthleteId, type, preferences);
            int rightPref = PreferenceIndex(right.AthleteId, type, preferences);
            if (leftPref != rightPref)
            {
                return leftPref.CompareTo(rightPref);
            }

            int leftAlt = RemainingAlternatives(left.AthleteId, open, remaining, byId);
            int rightAlt = RemainingAlternatives(right.AthleteId, open, remaining, byId);
            if (leftAlt != rightAlt)
            {
                return leftAlt.CompareTo(rightAlt);
            }

            int leftRank = TypeRankOf(type, left.AthleteId, rankLookup);
            int rightRank = TypeRankOf(type, right.AthleteId, rankLookup);
            if (leftRank != rightRank)
            {
                return leftRank.CompareTo(rightRank);
            }

            int name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            return name != 0 ? name : left.AthleteId.CompareTo(right.AthleteId);
        });
        return ordered.GetRange(0, 4);
    }

    internal static int PreferenceIndex(int athleteId, string type, Dictionary<int, List<string>> preferences)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(preferences);
        if (!preferences.TryGetValue(athleteId, out List<string>? ordered))
        {
            return int.MaxValue;
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            if (string.Equals(ordered[i], type, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    internal static int RemainingAlternatives(
        int athleteId,
        HashSet<string> open,
        HashSet<int> remaining,
        Dictionary<int, ScoredCandidate> byId)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(remaining);
        ArgumentNullException.ThrowIfNull(byId);
        if (!byId.TryGetValue(athleteId, out ScoredCandidate? candidate))
        {
            return int.MaxValue;
        }

        int count = 0;
        foreach (string type in candidate.EligibleTypes)
        {
            if (open.Contains(type))
            {
                checked
                {
                    count++;
                }
            }
        }

        return count;
    }

    internal static bool IsEligibleFor(ScoredCandidate candidate, string type)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(type);
        foreach (string eligible in candidate.EligibleTypes)
        {
            if (string.Equals(eligible, type, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static List<AllocatedMember> OrderTeamMembers(
        string type,
        List<ScoredCandidate> picked,
        Dictionary<string, int> rankLookup)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(picked);
        ArgumentNullException.ThrowIfNull(rankLookup);
        if (picked.Count != 4)
        {
            throw new InvalidOperationException($"Type Cup team '{type}' must hold exactly 4 athletes, was {picked.Count}.");
        }

        HashSet<int> ids = new();
        foreach (ScoredCandidate member in picked)
        {
            if (!ids.Add(member.AthleteId))
            {
                throw new InvalidOperationException($"Type Cup team '{type}' contains duplicate athlete {member.AthleteId}.");
            }

            if (!IsEligibleFor(member, type))
            {
                throw new InvalidOperationException($"Type Cup team '{type}' contains ineligible athlete '{member.Name}'.");
            }
        }

        List<ScoredCandidate> ordered = [.. picked];
        ordered.Sort(CompareScored);
        List<AllocatedMember> members = new(4);
        for (int i = 0; i < ordered.Count; i++)
        {
            ScoredCandidate entry = ordered[i];
            members.Add(new AllocatedMember(
                entry.AthleteId, entry.Name, i + 1,
                TypeRankOf(type, entry.AthleteId, rankLookup),
                entry.FinalRatingThousandths,
                entry.BonusNormThousandths, entry.PerformanceNormThousandths,
                entry.FormNormThousandths, entry.PrestigeNormThousandths,
                entry.BonusRawThousandths, entry.PerformanceRawThousandths,
                entry.FormRaw, entry.PrestigeRaw));
        }

        return members;
    }

    /// <summary>
    /// Exact deterministic maximum-cardinality allocation. Maximizes the number of
    /// valid four-distinct-athlete teams via branch-and-bound over type subsets with
    /// max-flow feasibility and backtracking. A type having four exclusive athletes
    /// proves a team can exist but never freezes which four play: every viable type
    /// is split into sharing-connected components and solved independently, so the
    /// strongest eligible roster wins. Each component optimum is assigned via
    /// deterministic min-cost flow preferring stronger selection rating, then
    /// stronger per-type rank, then relative type preference, then ordinal tie-breaks.
    /// Capped status is eligibility only and never appears in the assignment cost.
    /// </summary>
    internal static AllocationResult AllocateOptimal(
        List<ScoredCandidate> scored,
        Dictionary<string, List<ScoredCandidate>> viable,
        Dictionary<int, List<string>> preferences,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(viable);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(rules);
        if (viable.Count == 0)
        {
            return new AllocationResult([], scored.Select(c => c.AthleteId).ToList());
        }

        Dictionary<int, ScoredCandidate> byId = scored.ToDictionary(c => c.AthleteId);
        Dictionary<string, int> rankLookup = BuildRankLookup(viable);
        List<string> allTypes = viable.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
        Dictionary<int, List<string>> eligibleViable = BuildEligibleViable(scored, viable);
        Dictionary<int, List<string>> eligibleRemaining = BuildEligibleRemaining(eligibleViable, allTypes);
        List<List<string>> components = SplitComponents(allTypes, eligibleRemaining);
        List<AllocatedTeam> allTeams = SolveComponents(components, eligibleRemaining, byId, preferences, rankLookup, rules);
        return BuildResultFromTeams(scored, allTeams);
    }

    internal static List<AllocatedTeam> SolveComponents(
        List<List<string>> components,
        Dictionary<int, List<string>> eligibleRemaining,
        Dictionary<int, ScoredCandidate> byId,
        Dictionary<int, List<string>> preferences,
        Dictionary<string, int> rankLookup,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(eligibleRemaining);
        ArgumentNullException.ThrowIfNull(byId);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(rankLookup);
        ArgumentNullException.ThrowIfNull(rules);
        List<AllocatedTeam> teams = [];
        foreach (List<string> component in components)
        {
            teams.AddRange(SolveSingleComponent(component, eligibleRemaining, byId, preferences, rankLookup, rules));
        }

        return teams;
    }

    internal static List<AllocatedTeam> SolveSingleComponent(
        List<string> component,
        Dictionary<int, List<string>> eligibleRemaining,
        Dictionary<int, ScoredCandidate> byId,
        Dictionary<int, List<string>> preferences,
        Dictionary<string, int> rankLookup,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(eligibleRemaining);
        ArgumentNullException.ThrowIfNull(byId);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(rankLookup);
        ArgumentNullException.ThrowIfNull(rules);
        List<int> pool = CollectPoolAthletes(eligibleRemaining, component, byId);
        List<string> best = FindMaximumFeasibleSubset(component, pool, eligibleRemaining, rules);
        if (best.Count == 0)
        {
            return [];
        }

        Dictionary<string, List<ScoredCandidate>> assignment =
            AssignMinCost(best, pool, eligibleRemaining, byId, preferences, rankLookup);
        List<AllocatedTeam> teams = new(best.Count);
        foreach (string type in best)
        {
            teams.Add(new AllocatedTeam(type, OrderTeamMembers(type, assignment[type], rankLookup)));
        }

        return teams;
    }

    internal static AllocationResult BuildResultFromTeams(
        List<ScoredCandidate> scored,
        List<AllocatedTeam> teams)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(teams);
        teams.Sort(static (left, right) => string.Compare(left.CreatureType, right.CreatureType, StringComparison.Ordinal));
        HashSet<int> assigned = teams.SelectMany(t => t.Members).Select(m => m.AthleteId).ToHashSet();
        List<int> unassigned = scored.Where(c => !assigned.Contains(c.AthleteId)).Select(c => c.AthleteId).ToList();
        return new AllocationResult(teams, unassigned);
    }

    internal static Dictionary<int, List<string>> BuildEligibleViable(
        List<ScoredCandidate> scored,
        Dictionary<string, List<ScoredCandidate>> viable)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(viable);
        Dictionary<int, List<string>> map = new(scored.Count);
        foreach (ScoredCandidate candidate in scored)
        {
            List<string> filtered = candidate.EligibleTypes.Where(t => viable.ContainsKey(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
            map[candidate.AthleteId] = filtered;
        }

        return map;
    }

    internal static Dictionary<int, List<string>> BuildEligibleRemaining(
        Dictionary<int, List<string>> eligibleViable,
        List<string> remainingTypes)
    {
        ArgumentNullException.ThrowIfNull(eligibleViable);
        ArgumentNullException.ThrowIfNull(remainingTypes);
        HashSet<string> remaining = remainingTypes.ToHashSet(StringComparer.Ordinal);
        Dictionary<int, List<string>> map = new(eligibleViable.Count);
        foreach ((int athleteId, List<string> elig) in eligibleViable)
        {
            List<string> filtered = elig.Where(t => remaining.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();
            map[athleteId] = filtered;
        }

        return map;
    }

    internal static List<int> CollectPoolAthletes(
        Dictionary<int, List<string>> eligibleRemaining,
        List<string> component,
        Dictionary<int, ScoredCandidate> byId)
    {
        ArgumentNullException.ThrowIfNull(eligibleRemaining);
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(byId);
        HashSet<string> inComponent = component.ToHashSet(StringComparer.Ordinal);
        List<int> pool = [];
        foreach ((int athleteId, List<string> elig) in eligibleRemaining)
        {
            bool touches = false;
            foreach (string type in elig)
            {
                if (inComponent.Contains(type))
                {
                    touches = true;
                    break;
                }
            }

            if (touches)
            {
                pool.Add(athleteId);
            }
        }

        pool.Sort((left, right) =>
        {
            ScoredCandidate l = byId[left];
            ScoredCandidate r = byId[right];
            int name = string.Compare(l.Name, r.Name, StringComparison.Ordinal);
            return name != 0 ? name : left.CompareTo(right);
        });
        return pool;
    }

    internal static List<List<string>> SplitComponents(
        List<string> remainingTypes,
        Dictionary<int, List<string>> eligibleRemaining)
    {
        ArgumentNullException.ThrowIfNull(remainingTypes);
        ArgumentNullException.ThrowIfNull(eligibleRemaining);
        TypeDisjointSet sets = new(remainingTypes);
        sets.UnionSharedTypes(eligibleRemaining);
        return sets.Groups();
    }

    internal sealed class TypeDisjointSet
    {
        private readonly Dictionary<string, string> _parent;

        public TypeDisjointSet(List<string> types)
        {
            ArgumentNullException.ThrowIfNull(types);
            _parent = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string type in types)
            {
                _parent[type] = type;
            }
        }

        public string Find(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            string root = value;
            while (!string.Equals(_parent[root], root, StringComparison.Ordinal))
            {
                root = _parent[root];
            }

            string cursor = value;
            while (!string.Equals(_parent[cursor], root, StringComparison.Ordinal))
            {
                string next = _parent[cursor];
                _parent[cursor] = root;
                cursor = next;
            }

            return root;
        }

        public void Union(string left, string right)
        {
            ArgumentNullException.ThrowIfNull(left);
            ArgumentNullException.ThrowIfNull(right);
            string rl = Find(left);
            string rr = Find(right);
            if (string.Equals(rl, rr, StringComparison.Ordinal))
            {
                return;
            }

            if (string.Compare(rl, rr, StringComparison.Ordinal) < 0)
            {
                _parent[rr] = rl;
            }
            else
            {
                _parent[rl] = rr;
            }
        }

        public void UnionSharedTypes(Dictionary<int, List<string>> eligibleRemaining)
        {
            ArgumentNullException.ThrowIfNull(eligibleRemaining);
            foreach (List<string> elig in eligibleRemaining.Values)
            {
                UnionEligibleList(elig);
            }
        }

        private void UnionEligibleList(List<string> elig)
        {
            ArgumentNullException.ThrowIfNull(elig);
            if (elig.Count < 2)
            {
                return;
            }

            for (int i = 1; i < elig.Count; i++)
            {
                if (_parent.ContainsKey(elig[0]) && _parent.ContainsKey(elig[i]))
                {
                    Union(elig[0], elig[i]);
                }
            }
        }

        public List<List<string>> Groups()
        {
            Dictionary<string, List<string>> groups = new(StringComparer.Ordinal);
            foreach (string type in _parent.Keys.OrderBy(t => t, StringComparer.Ordinal))
            {
                string root = Find(type);
                if (!groups.TryGetValue(root, out List<string>? list))
                {
                    list = [];
                    groups[root] = list;
                }

                list.Add(type);
            }

            List<List<string>> components = groups.Values.ToList();
            foreach (List<string> component in components)
            {
                component.Sort(StringComparer.Ordinal);
            }

            components.Sort(static (left, right) => string.Compare(left[0], right[0], StringComparison.Ordinal));
            return components;
        }
    }

    internal static List<string> FindMaximumFeasibleSubset(
        List<string> ctypes,
        List<int> poolAthletes,
        Dictionary<int, List<string>> eligibleRemaining,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(ctypes);
        ArgumentNullException.ThrowIfNull(poolAthletes);
        ArgumentNullException.ThrowIfNull(eligibleRemaining);
        ArgumentNullException.ThrowIfNull(rules);
        List<string> ordered = ctypes.OrderBy(t => t, StringComparer.Ordinal).ToList();
        List<string> best = [];
        List<string> chosen = [];
        SearchSubset(0, ordered, poolAthletes, eligibleRemaining, rules, chosen, best);
        return best;
    }

    internal static void SearchSubset(
        int index,
        List<string> ordered,
        List<int> poolAthletes,
        Dictionary<int, List<string>> eligibleRemaining,
        RulesV1 rules,
        List<string> chosen,
        List<string> best)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(poolAthletes);
        ArgumentNullException.ThrowIfNull(eligibleRemaining);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(best);
        if (chosen.Count + (ordered.Count - index) <= best.Count)
        {
            return;
        }

        if (index >= ordered.Count)
        {
            if (chosen.Count > best.Count)
            {
                best.Clear();
                best.AddRange(chosen);
            }

            return;
        }

        string type = ordered[index];
        chosen.Add(type);
        if (IsFeasibleSubset(chosen, poolAthletes, eligibleRemaining, rules))
        {
            if (chosen.Count > best.Count)
            {
                best.Clear();
                best.AddRange(chosen);
            }

            SearchSubset(index + 1, ordered, poolAthletes, eligibleRemaining, rules, chosen, best);
        }

        chosen.RemoveAt(chosen.Count - 1);
        SearchSubset(index + 1, ordered, poolAthletes, eligibleRemaining, rules, chosen, best);
    }

    internal static bool IsFeasibleSubset(
        List<string> chosen,
        List<int> poolAthletes,
        Dictionary<int, List<string>> eligibleRemaining,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(poolAthletes);
        ArgumentNullException.ThrowIfNull(eligibleRemaining);
        ArgumentNullException.ThrowIfNull(rules);
        if (chosen.Count == 0)
        {
            return true;
        }

        List<string> types = chosen.OrderBy(t => t, StringComparer.Ordinal).ToList();
        MaxFlowGraph graph = MaxFlowGraph.Build(poolAthletes, types, eligibleRemaining);
        int flow = graph.MaxFlow();
        checked
        {
            return flow >= types.Count * rules.TypeCupMinTeamSize;
        }
    }

    internal static Dictionary<string, List<ScoredCandidate>> AssignMinCost(
        List<string> best,
        List<int> poolAthletes,
        Dictionary<int, List<string>> eligibleRemaining,
        Dictionary<int, ScoredCandidate> byId,
        Dictionary<int, List<string>> preferences,
        Dictionary<string, int> rankLookup)
    {
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(poolAthletes);
        ArgumentNullException.ThrowIfNull(eligibleRemaining);
        ArgumentNullException.ThrowIfNull(byId);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(rankLookup);
        List<string> types = best.OrderBy(t => t, StringComparer.Ordinal).ToList();
        MinCostFlowGraph graph = MinCostFlowGraph.Build(poolAthletes, types, eligibleRemaining, byId, preferences, rankLookup);
        Dictionary<int, string> athleteToType = graph.MinCostFlow();
        Dictionary<string, List<ScoredCandidate>> assignment = new(StringComparer.Ordinal);
        foreach (string type in types)
        {
            assignment[type] = [];
        }

        foreach ((int athleteId, string type) in athleteToType)
        {
            assignment[type].Add(byId[athleteId]);
        }

        foreach (string type in types)
        {
            if (assignment[type].Count != 4)
            {
                throw new InvalidOperationException($"Type Cup team '{type}' must hold exactly 4 athletes, was {assignment[type].Count}.");
            }
        }

        return assignment;
    }

    /// <summary>
    /// Lexicographic assignment cost with explicit priority, all fixed-point integers:
    /// strongest global selection rating first, then stronger per-type rank, then
    /// relative type preference for competing placements, then ordinal graph order.
    /// Capped status never appears: its entire meaning is already expressed by the
    /// eligibility edges. One rating point (1_000_000) outweighs any realistic total
    /// preference penalty, and one rank step (10_000) outweighs any realistic total
    /// preference penalty, so preference resolves placement without benching a
    /// stronger eligible athlete. Ineligible (MaxValue) inputs map to large finite
    /// constants; feasible edges are always eligible so those branches never decide
    /// a real assignment.
    /// </summary>
    internal const long AssignmentRatingScale = 1_000_000L;

    internal const long AssignmentRankScale = 10_000L;

    internal const long AssignmentPreferenceScale = 1L;

    internal static long ComputeAssignmentCost(
        ScoredCandidate candidate,
        string type,
        Dictionary<int, List<string>> preferences,
        Dictionary<string, int> rankLookup)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(rankLookup);
        int prefIndex = PreferenceIndex(candidate.AthleteId, type, preferences);
        int rank = TypeRankOf(type, candidate.AthleteId, rankLookup);
        checked
        {
            long ratingPart = (long)(1000 - candidate.FinalRatingThousandths) * AssignmentRatingScale;
            long rankPart = rank == int.MaxValue
                ? AssignmentRankScale * 100_000L
                : (long)(rank - 1) * AssignmentRankScale;
            long prefPart = prefIndex == int.MaxValue
                ? AssignmentRatingScale
                : (long)prefIndex * AssignmentPreferenceScale;
            return ratingPart + rankPart + prefPart;
        }
    }

    internal sealed class MaxFlowGraph
    {
        private readonly List<List<FlowEdge>> _adj;
        private readonly int _source;
        private readonly int _sink;

        private MaxFlowGraph(List<List<FlowEdge>> adj, int source, int sink)
        {
            _adj = adj;
            _source = source;
            _sink = sink;
        }

        internal sealed class FlowEdge
        {
            public int To;
            public int Rev;
            public int Cap;

            public FlowEdge(int to, int rev, int cap)
            {
                To = to;
                Rev = rev;
                Cap = cap;
            }
        }

        public static MaxFlowGraph Build(
            List<int> poolAthletes,
            List<string> types,
            Dictionary<int, List<string>> eligibleRemaining)
        {
            ArgumentNullException.ThrowIfNull(poolAthletes);
            ArgumentNullException.ThrowIfNull(types);
            ArgumentNullException.ThrowIfNull(eligibleRemaining);
            int athleteCount = poolAthletes.Count;
            int typeCount = types.Count;
            int source = 0;
            int sink = 1 + athleteCount + typeCount;
            List<List<FlowEdge>> adj = CreateAdjacency(sink + 1);
            Dictionary<int, int> athleteNode = BuildAthleteNodes(poolAthletes);
            Dictionary<string, int> typeNode = BuildTypeNodes(types, athleteCount);
            AddSourceEdges(adj, source, poolAthletes, athleteNode);
            AddAthleteEdges(adj, poolAthletes, types, eligibleRemaining, athleteNode, typeNode);
            AddSinkEdges(adj, sink, types, typeNode);
            return new MaxFlowGraph(adj, source, sink);
        }

        private static List<List<FlowEdge>> CreateAdjacency(int total)
        {
            List<List<FlowEdge>> adj = new(total);
            for (int i = 0; i < total; i++)
            {
                adj.Add([]);
            }

            return adj;
        }

        private static Dictionary<int, int> BuildAthleteNodes(List<int> poolAthletes)
        {
            ArgumentNullException.ThrowIfNull(poolAthletes);
            Dictionary<int, int> map = new(poolAthletes.Count);
            for (int i = 0; i < poolAthletes.Count; i++)
            {
                map[poolAthletes[i]] = 1 + i;
            }

            return map;
        }

        private static Dictionary<string, int> BuildTypeNodes(List<string> types, int athleteCount)
        {
            ArgumentNullException.ThrowIfNull(types);
            Dictionary<string, int> map = new(StringComparer.Ordinal);
            for (int i = 0; i < types.Count; i++)
            {
                map[types[i]] = 1 + athleteCount + i;
            }

            return map;
        }

        private static void AddFlowEdge(List<List<FlowEdge>> adj, int from, int to, int cap)
        {
            ArgumentNullException.ThrowIfNull(adj);
            FlowEdge fwd = new(to, adj[to].Count, cap);
            FlowEdge rev = new(from, adj[from].Count, 0);
            adj[from].Add(fwd);
            adj[to].Add(rev);
        }

        private static void AddSourceEdges(
            List<List<FlowEdge>> adj,
            int source,
            List<int> poolAthletes,
            Dictionary<int, int> athleteNode)
        {
            ArgumentNullException.ThrowIfNull(adj);
            ArgumentNullException.ThrowIfNull(poolAthletes);
            ArgumentNullException.ThrowIfNull(athleteNode);
            foreach (int athleteId in poolAthletes)
            {
                AddFlowEdge(adj, source, athleteNode[athleteId], 1);
            }
        }

        private static void AddAthleteEdges(
            List<List<FlowEdge>> adj,
            List<int> poolAthletes,
            List<string> types,
            Dictionary<int, List<string>> eligibleRemaining,
            Dictionary<int, int> athleteNode,
            Dictionary<string, int> typeNode)
        {
            ArgumentNullException.ThrowIfNull(adj);
            ArgumentNullException.ThrowIfNull(poolAthletes);
            ArgumentNullException.ThrowIfNull(types);
            ArgumentNullException.ThrowIfNull(eligibleRemaining);
            ArgumentNullException.ThrowIfNull(athleteNode);
            ArgumentNullException.ThrowIfNull(typeNode);
            foreach (int athleteId in poolAthletes)
            {
                if (!eligibleRemaining.TryGetValue(athleteId, out List<string>? elig))
                {
                    continue;
                }

                int from = athleteNode[athleteId];
                foreach (string type in elig.OrderBy(t => t, StringComparer.Ordinal))
                {
                    if (typeNode.TryGetValue(type, out int to))
                    {
                        AddFlowEdge(adj, from, to, 1);
                    }
                }
            }
        }

        private static void AddSinkEdges(
            List<List<FlowEdge>> adj,
            int sink,
            List<string> types,
            Dictionary<string, int> typeNode)
        {
            ArgumentNullException.ThrowIfNull(adj);
            ArgumentNullException.ThrowIfNull(types);
            ArgumentNullException.ThrowIfNull(typeNode);
            foreach (string type in types)
            {
                AddFlowEdge(adj, typeNode[type], sink, 4);
            }
        }

        public int MaxFlow()
        {
            int flow = 0;
            int total = _adj.Count;
            while (true)
            {
                int[] prevNode = new int[total];
                int[] prevEdge = new int[total];
                Array.Fill(prevNode, -1);
                Queue<int> queue = new();
                queue.Enqueue(_source);
                prevNode[_source] = _source;
                while (queue.Count > 0 && prevNode[_sink] == -1)
                {
                    int current = queue.Dequeue();
                    for (int i = 0; i < _adj[current].Count; i++)
                    {
                        FlowEdge edge = _adj[current][i];
                        if (edge.Cap > 0 && prevNode[edge.To] == -1)
                        {
                            prevNode[edge.To] = current;
                            prevEdge[edge.To] = i;
                            queue.Enqueue(edge.To);
                            if (edge.To == _sink)
                            {
                                break;
                            }
                        }
                    }
                }

                if (prevNode[_sink] == -1)
                {
                    break;
                }

                int cursor = _sink;
                while (cursor != _source)
                {
                    int parent = prevNode[cursor];
                    int edgeIndex = prevEdge[cursor];
                    FlowEdge edge = _adj[parent][edgeIndex];
                    edge.Cap -= 1;
                    _adj[cursor][edge.Rev].Cap += 1;
                    cursor = parent;
                }

                checked
                {
                    flow += 1;
                }
            }

            return flow;
        }
    }

    internal sealed class MinCostFlowGraph
    {
        private readonly List<List<CostEdge>> _adj;
        private readonly int _source;
        private readonly int _sink;
        private readonly List<int> _poolAthletes;
        private readonly List<string> _types;
        private readonly Dictionary<int, int> _athleteNode;
        private readonly Dictionary<string, int> _typeNode;

        private MinCostFlowGraph(
            List<List<CostEdge>> adj,
            int source,
            int sink,
            List<int> poolAthletes,
            List<string> types,
            Dictionary<int, int> athleteNode,
            Dictionary<string, int> typeNode)
        {
            _adj = adj;
            _source = source;
            _sink = sink;
            _poolAthletes = poolAthletes;
            _types = types;
            _athleteNode = athleteNode;
            _typeNode = typeNode;
        }

        internal sealed class CostEdge
        {
            public int To;
            public int Rev;
            public int Cap;
            public long Cost;

            public CostEdge(int to, int rev, int cap, long cost)
            {
                To = to;
                Rev = rev;
                Cap = cap;
                Cost = cost;
            }
        }

        public static MinCostFlowGraph Build(
            List<int> poolAthletes,
            List<string> types,
            Dictionary<int, List<string>> eligibleRemaining,
            Dictionary<int, ScoredCandidate> byId,
            Dictionary<int, List<string>> preferences,
            Dictionary<string, int> rankLookup)
        {
            ArgumentNullException.ThrowIfNull(poolAthletes);
            ArgumentNullException.ThrowIfNull(types);
            ArgumentNullException.ThrowIfNull(eligibleRemaining);
            ArgumentNullException.ThrowIfNull(byId);
            ArgumentNullException.ThrowIfNull(preferences);
            ArgumentNullException.ThrowIfNull(rankLookup);
            int athleteCount = poolAthletes.Count;
            int typeCount = types.Count;
            int source = 0;
            int sink = 1 + athleteCount + typeCount;
            List<List<CostEdge>> adj = CreateCostAdjacency(sink + 1);
            Dictionary<int, int> athleteNode = BuildCostAthleteNodes(poolAthletes);
            Dictionary<string, int> typeNode = BuildCostTypeNodes(types, athleteCount);
            AddCostSourceEdges(adj, source, poolAthletes, athleteNode);
            AddCostAthleteEdges(adj, poolAthletes, eligibleRemaining, byId, preferences, rankLookup, athleteNode, typeNode);
            AddCostSinkEdges(adj, sink, types, typeNode);
            return new MinCostFlowGraph(adj, source, sink, poolAthletes, types, athleteNode, typeNode);
        }

        private static List<List<CostEdge>> CreateCostAdjacency(int total)
        {
            List<List<CostEdge>> adj = new(total);
            for (int i = 0; i < total; i++)
            {
                adj.Add([]);
            }

            return adj;
        }

        private static Dictionary<int, int> BuildCostAthleteNodes(List<int> poolAthletes)
        {
            ArgumentNullException.ThrowIfNull(poolAthletes);
            Dictionary<int, int> map = new(poolAthletes.Count);
            for (int i = 0; i < poolAthletes.Count; i++)
            {
                map[poolAthletes[i]] = 1 + i;
            }

            return map;
        }

        private static Dictionary<string, int> BuildCostTypeNodes(List<string> types, int athleteCount)
        {
            ArgumentNullException.ThrowIfNull(types);
            Dictionary<string, int> map = new(StringComparer.Ordinal);
            for (int i = 0; i < types.Count; i++)
            {
                map[types[i]] = 1 + athleteCount + i;
            }

            return map;
        }

        private static void AddCostEdge(List<List<CostEdge>> adj, int from, int to, int cap, long cost)
        {
            ArgumentNullException.ThrowIfNull(adj);
            CostEdge fwd = new(to, adj[to].Count, cap, cost);
            CostEdge rev = new(from, adj[from].Count, 0, -cost);
            adj[from].Add(fwd);
            adj[to].Add(rev);
        }

        private static void AddCostSourceEdges(
            List<List<CostEdge>> adj,
            int source,
            List<int> poolAthletes,
            Dictionary<int, int> athleteNode)
        {
            ArgumentNullException.ThrowIfNull(adj);
            ArgumentNullException.ThrowIfNull(poolAthletes);
            ArgumentNullException.ThrowIfNull(athleteNode);
            foreach (int athleteId in poolAthletes)
            {
                AddCostEdge(adj, source, athleteNode[athleteId], 1, 0);
            }
        }

        private static void AddCostAthleteEdges(
            List<List<CostEdge>> adj,
            List<int> poolAthletes,
            Dictionary<int, List<string>> eligibleRemaining,
            Dictionary<int, ScoredCandidate> byId,
            Dictionary<int, List<string>> preferences,
            Dictionary<string, int> rankLookup,
            Dictionary<int, int> athleteNode,
            Dictionary<string, int> typeNode)
        {
            ArgumentNullException.ThrowIfNull(adj);
            ArgumentNullException.ThrowIfNull(poolAthletes);
            ArgumentNullException.ThrowIfNull(eligibleRemaining);
            ArgumentNullException.ThrowIfNull(byId);
            ArgumentNullException.ThrowIfNull(preferences);
            ArgumentNullException.ThrowIfNull(rankLookup);
            ArgumentNullException.ThrowIfNull(athleteNode);
            ArgumentNullException.ThrowIfNull(typeNode);
            foreach (int athleteId in poolAthletes)
            {
                AddSingleAthleteEdges(adj, athleteId, eligibleRemaining, byId, preferences, rankLookup, athleteNode, typeNode);
            }
        }

        private static void AddSingleAthleteEdges(
            List<List<CostEdge>> adj,
            int athleteId,
            Dictionary<int, List<string>> eligibleRemaining,
            Dictionary<int, ScoredCandidate> byId,
            Dictionary<int, List<string>> preferences,
            Dictionary<string, int> rankLookup,
            Dictionary<int, int> athleteNode,
            Dictionary<string, int> typeNode)
        {
            ArgumentNullException.ThrowIfNull(adj);
            ArgumentNullException.ThrowIfNull(eligibleRemaining);
            ArgumentNullException.ThrowIfNull(byId);
            ArgumentNullException.ThrowIfNull(preferences);
            ArgumentNullException.ThrowIfNull(rankLookup);
            ArgumentNullException.ThrowIfNull(athleteNode);
            ArgumentNullException.ThrowIfNull(typeNode);
            if (!eligibleRemaining.TryGetValue(athleteId, out List<string>? elig))
            {
                return;
            }

            if (!byId.TryGetValue(athleteId, out ScoredCandidate? candidate))
            {
                return;
            }

            int from = athleteNode[athleteId];
            foreach (string type in elig.OrderBy(t => t, StringComparer.Ordinal))
            {
                if (typeNode.TryGetValue(type, out int to))
                {
                    long cost = ComputeAssignmentCost(candidate, type, preferences, rankLookup);
                    AddCostEdge(adj, from, to, 1, cost);
                }
            }
        }

        private static void AddCostSinkEdges(
            List<List<CostEdge>> adj,
            int sink,
            List<string> types,
            Dictionary<string, int> typeNode)
        {
            ArgumentNullException.ThrowIfNull(adj);
            ArgumentNullException.ThrowIfNull(types);
            ArgumentNullException.ThrowIfNull(typeNode);
            foreach (string type in types)
            {
                AddCostEdge(adj, typeNode[type], sink, 4, 0);
            }
        }

        public Dictionary<int, string> MinCostFlow()
        {
            int required;
            checked
            {
                required = _types.Count * 4;
            }

            int flow = 0;
            while (flow < required)
            {
                if (!TryAugmentOnce())
                {
                    throw new InvalidOperationException("Type Cup optimal team set is infeasible for min-cost assignment.");
                }

                checked
                {
                    flow += 1;
                }
            }

            return ExtractAssignment();
        }

        private bool TryAugmentOnce()
        {
            int total = _adj.Count;
            long[] dist = CreateDistArray(total);
            int[] prevNode = CreatePrevArray(total);
            int[] prevEdge = new int[total];
            RunBellmanFord(dist, prevNode, prevEdge);
            if (dist[_sink] == long.MaxValue / 4)
            {
                return false;
            }

            AugmentPath(prevNode, prevEdge);
            return true;
        }

        private static long[] CreateDistArray(int total)
        {
            const long INF = long.MaxValue / 4;
            long[] dist = new long[total];
            for (int i = 0; i < total; i++)
            {
                dist[i] = INF;
            }

            return dist;
        }

        private static int[] CreatePrevArray(int total)
        {
            int[] prev = new int[total];
            Array.Fill(prev, -1);
            return prev;
        }

        private void RunBellmanFord(long[] dist, int[] prevNode, int[] prevEdge)
        {
            ArgumentNullException.ThrowIfNull(dist);
            ArgumentNullException.ThrowIfNull(prevNode);
            ArgumentNullException.ThrowIfNull(prevEdge);
            const long INF = long.MaxValue / 4;
            dist[_source] = 0;
            bool updated = true;
            for (int iter = 0; iter < _adj.Count && updated; iter++)
            {
                updated = RelaxAllEdges(dist, prevNode, prevEdge, INF);
            }
        }

        private bool RelaxAllEdges(long[] dist, int[] prevNode, int[] prevEdge, long inf)
        {
            ArgumentNullException.ThrowIfNull(dist);
            ArgumentNullException.ThrowIfNull(prevNode);
            ArgumentNullException.ThrowIfNull(prevEdge);
            bool updated = false;
            for (int u = 0; u < _adj.Count; u++)
            {
                if (dist[u] == inf)
                {
                    continue;
                }

                updated |= RelaxNodeEdges(u, dist, prevNode, prevEdge);
            }

            return updated;
        }

        private bool RelaxNodeEdges(int node, long[] dist, int[] prevNode, int[] prevEdge)
        {
            ArgumentNullException.ThrowIfNull(dist);
            ArgumentNullException.ThrowIfNull(prevNode);
            ArgumentNullException.ThrowIfNull(prevEdge);
            bool updated = false;
            for (int ei = 0; ei < _adj[node].Count; ei++)
            {
                CostEdge edge = _adj[node][ei];
                if (edge.Cap <= 0)
                {
                    continue;
                }

                checked
                {
                    long nd = dist[node] + edge.Cost;
                    if (nd < dist[edge.To])
                    {
                        dist[edge.To] = nd;
                        prevNode[edge.To] = node;
                        prevEdge[edge.To] = ei;
                        updated = true;
                    }
                }
            }

            return updated;
        }

        private void AugmentPath(int[] prevNode, int[] prevEdge)
        {
            ArgumentNullException.ThrowIfNull(prevNode);
            ArgumentNullException.ThrowIfNull(prevEdge);
            int cursor = _sink;
            while (cursor != _source)
            {
                int parent = prevNode[cursor];
                int edgeIndex = prevEdge[cursor];
                CostEdge edge = _adj[parent][edgeIndex];
                edge.Cap -= 1;
                _adj[cursor][edge.Rev].Cap += 1;
                cursor = parent;
            }
        }

        private Dictionary<int, string> ExtractAssignment()
        {
            Dictionary<int, string> nodeToType = new();
            foreach ((string type, int node) in _typeNode)
            {
                nodeToType[node] = type;
            }

            Dictionary<int, string> athleteToType = new();
            foreach ((int athleteId, int athleteN) in _athleteNode)
            {
                string? found = FindAssignedType(athleteN, nodeToType);
                if (found is not null)
                {
                    athleteToType[athleteId] = found;
                }
            }

            return athleteToType;
        }

        private string? FindAssignedType(int athleteNode, Dictionary<int, string> nodeToType)
        {
            ArgumentNullException.ThrowIfNull(nodeToType);
            foreach (CostEdge edge in _adj[athleteNode])
            {
                if (!nodeToType.TryGetValue(edge.To, out string? type))
                {
                    continue;
                }

                if (edge.Cost >= 0 && edge.Cap == 0)
                {
                    return type;
                }
            }

            return null;
        }
    }
}
