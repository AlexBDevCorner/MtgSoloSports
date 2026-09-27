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
/// Allocation maximizes the number of valid four-athlete teams with a deterministic
/// most-constrained-first greedy: types with the fewest remaining candidates form
/// first (type-name ordinal breaks ties), and within a type capped athletes, then
/// athletes that prefer the type, then the fewest remaining alternatives, then the
/// stronger type rank/rating win the four seats. All ordering is ordinal by name
/// then athlete id; all scoring is checked fixed-point integers via
/// <see cref="SelectionScore.Combine"/> with the snapshot Cup weights normalized
/// globally across the candidate pool (tied components normalize to 1000).
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
    /// candidates never participate.
    /// </summary>
    public static AllocationResult Allocate(IReadOnlyList<CandidateRaw> candidates, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rules);
        List<ScoredCandidate> scored = ScoreAll(candidates, rules);
        Dictionary<string, List<ScoredCandidate>> byType = GroupByType(scored);
        Dictionary<string, List<ScoredCandidate>> viable = KeepViable(byType, rules);
        Dictionary<int, List<string>> preferences = BuildPreferences(scored, byType);
        return AllocateGreedy(scored, viable, preferences, rules);
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

        List<ScoredCandidate> ordered = [.. pool];
        ordered.Sort((left, right) =>
        {
            bool leftCapped = string.Equals(left.CappedNationality, type, StringComparison.Ordinal);
            bool rightCapped = string.Equals(right.CappedNationality, type, StringComparison.Ordinal);
            if (leftCapped != rightCapped)
            {
                return leftCapped ? -1 : 1;
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

            int rating = right.FinalRatingThousandths.CompareTo(left.FinalRatingThousandths);
            if (rating != 0)
            {
                return rating;
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
}
