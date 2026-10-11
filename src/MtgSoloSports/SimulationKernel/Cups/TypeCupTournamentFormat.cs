using System.Security.Cryptography;
using System.Text;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Pure scalable Type Cup tournament-format mathematics (MSS-061, MSS-071).
/// At most 32 teams compete in any single competition field: 1-32 selected
/// teams run a direct Final with the current four rank-group by eight-round
/// format, while more than 32 teams are randomly drawn into balanced
/// qualification groups of at most 32 teams each, advancing exactly 32 teams
/// to a fresh Final. Format v1 (fixed quotas) gives any extra Final places to
/// larger groups, then lower group numbers. Format v2 (MSS-071, current) gives
/// every group the same guaranteed quota plus global performance wildcards so
/// no group number confers an advantage. All methods are deterministic
/// fixed-point/integer-only; the only randomness is the caller-supplied
/// <see cref="Pcg32V1"/> draw, never strength-seeded, never clock/GUID/database
/// ordering.
/// </summary>
public static class TypeCupTournamentFormat
{
    /// <summary>
    /// Tournament phase identity. Legacy is the pre-scalable unbounded
    /// single-field format; Qualification and Final are the scalable format.
    /// Persisted as an integer so qualification and Final rounds cannot collide.
    /// </summary>
    public enum TournamentPhase
    {
        LegacySingleField = 0,
        Qualification = 1,
        Final = 2,
    }

    /// <summary>
    /// One team's qualification-group assignment from the random draw.
    /// Groups are 1-based in persisted draw order.
    /// </summary>
    public sealed record QualificationAssignment(string CreatureType, int QualificationGroup);

    /// <summary>
    /// True when the selected field runs a direct Final with no qualification stage.
    /// </summary>
    public static bool IsDirectFinal(int teamCount, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (teamCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(teamCount), "Team count cannot be negative.");
        }

        return teamCount <= rules.TypeCupMaxDirectFinalTeams;
    }

    /// <summary>
    /// Qualification-group count for a selected field: 0 for a direct Final,
    /// otherwise ceil(teamCount / maxDirectFinalTeams). Every qualification
    /// group then holds at most 32 teams.
    /// </summary>
    public static int QualificationGroupCount(int teamCount, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (teamCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(teamCount), "Team count cannot be negative.");
        }

        if (teamCount <= rules.TypeCupMaxDirectFinalTeams)
        {
            return 0;
        }

        checked
        {
            return (teamCount + rules.TypeCupMaxDirectFinalTeams - 1) / rules.TypeCupMaxDirectFinalTeams;
        }
    }

    /// <summary>
    /// Balanced qualification-group sizes: as equal as mathematically possible,
    /// differing by at most one team, summing to teamCount, each at most 32.
    /// Empty for a direct Final. The remainder is distributed to the earliest
    /// groups so larger groups come first in draw order.
    /// </summary>
    public static IReadOnlyList<int> BalancedQualificationGroupSizes(int teamCount, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (teamCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(teamCount), "Team count cannot be negative.");
        }

        int groupCount = QualificationGroupCount(teamCount, rules);
        if (groupCount == 0)
        {
            return [];
        }

        checked
        {
            int baseSize = teamCount / groupCount;
            int remainder = teamCount % groupCount;
            List<int> sizes = new(groupCount);
            for (int i = 0; i < groupCount; i++)
            {
                sizes.Add(baseSize + (i < remainder ? 1 : 0));
            }

            return sizes;
        }
    }

    /// <summary>
    /// Final-place quota per qualification group for the legacy v1 fixed-quota
    /// policy (indexed by 0-based group position, corresponding to persisted
    /// groups 1..G). Every group receives floor(32 / groupCount); the first
    /// remainder groups in deterministic (size descending, group number
    /// ascending) order receive one extra place so larger groups advance more
    /// teams before equal-sized groups. The total is always exactly 32.
    /// Retained only to validate/replay already-persisted v1 draws and results;
    /// new draws use <see cref="AllocateGuaranteedFinalPlaces"/> plus
    /// <see cref="WildcardCount"/>.
    /// </summary>
    public static IReadOnlyList<int> AllocateFinalPlaces(IReadOnlyList<int> groupSizes, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(groupSizes);
        ArgumentNullException.ThrowIfNull(rules);
        if (groupSizes.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupSizes), "Qualification requires at least one group.");
        }

        int groupCount = groupSizes.Count;
        checked
        {
            int basePlaces = rules.TypeCupFinalTeamCount / groupCount;
            int remainder = rules.TypeCupFinalTeamCount % groupCount;

            // Deterministic extra-place order: larger groups first, ties by group number.
            List<int> order = Enumerable.Range(0, groupCount).ToList();
            order.Sort((a, b) =>
            {
                int sizeCompare = groupSizes[b].CompareTo(groupSizes[a]);
                return sizeCompare != 0 ? sizeCompare : a.CompareTo(b);
            });

            int[] quotas = new int[groupCount];
            for (int i = 0; i < groupCount; i++)
            {
                quotas[i] = basePlaces;
            }

            for (int i = 0; i < remainder; i++)
            {
                quotas[order[i]] += 1;
            }

            return quotas;
        }
    }

    /// <summary>
    /// Guaranteed Final places per group under the v2 wildcard policy (MSS-071):
    /// every group receives exactly <c>floor(32 / G)</c>, independent of group
    /// size and group number. The remaining <see cref="WildcardCount"/> places
    /// are global performance wildcards decided after all qualifiers complete.
    /// For 84 teams (G=3) this is 10/10/10 guaranteed plus 2 wildcards; for
    /// 33-64 teams (G=2) it is 16/16 with zero wildcards. The total guaranteed
    /// plus wildcards is always exactly 32.
    /// </summary>
    public static IReadOnlyList<int> AllocateGuaranteedFinalPlaces(IReadOnlyList<int> groupSizes, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(groupSizes);
        ArgumentNullException.ThrowIfNull(rules);
        if (groupSizes.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupSizes), "Qualification requires at least one group.");
        }

        int groupCount = groupSizes.Count;
        checked
        {
            int guaranteed = rules.TypeCupFinalTeamCount / groupCount;
            int[] quotas = new int[groupCount];
            for (int i = 0; i < groupCount; i++)
            {
                quotas[i] = guaranteed;
            }

            return quotas;
        }
    }

    /// <summary>
    /// Global wildcard count under the v2 policy: <c>32 - G * floor(32 / G)</c>.
    /// Zero when 32 divides evenly by the group count (e.g. two groups give
    /// 16/16 with no wildcards); otherwise one next-ranked candidate per group
    /// competes and the best <c>wildcardCount</c> advance.
    /// </summary>
    public static int WildcardCount(int groupCount, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (groupCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupCount), "Qualification requires at least one group.");
        }

        checked
        {
            int guaranteed = rules.TypeCupFinalTeamCount / groupCount;
            return rules.TypeCupFinalTeamCount - (groupCount * guaranteed);
        }
    }

    /// <summary>
    /// Guaranteed quota for a group count (v2 policy): <c>floor(32 / G)</c>.
    /// </summary>
    public static int GuaranteedPlacesPerGroup(int groupCount, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (groupCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(groupCount), "Qualification requires at least one group.");
        }

        return rules.TypeCupFinalTeamCount / groupCount;
    }

    /// <summary>
    /// True for the v2 wildcard policy version; false for the legacy v1 fixed
    /// quota version. Legacy single-field (0) is neither.
    /// </summary>
    public static bool IsWildcardPolicy(int tournamentFormatVersion) =>
        tournamentFormatVersion == RulesV1.WildcardTypeCupTournamentFormatVersion;

    /// <summary>
    /// Expected base-point sum for one rank-group round of <paramref name="groupSize"/>
    /// teams, in thousandths: the sum of the first <paramref name="groupSize"/>
    /// snapshot scoring-table entries times <c>FixedScale</c>. Pure integer math
    /// from the versioned table, so existing universes keep their meaning when
    /// defaults evolve.
    /// </summary>
    public static long ExpectedBaseSumThousandths(int groupSize, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (groupSize < 1 || groupSize > rules.ScoringTable.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(groupSize),
                $"Qualification group size must be 1..{rules.ScoringTable.Count}, was {groupSize}.");
        }

        checked
        {
            long sum = 0;
            for (int i = 0; i < groupSize; i++)
            {
                sum += (long)rules.ScoringTable[i] * RulesV1.FixedScale;
            }

            return sum;
        }
    }

    /// <summary>
    /// One wildcard candidate: the next-ranked team of a qualification group at
    /// group rank <c>guaranteed + 1</c> (rank 1 when guaranteed is zero).
    /// <c>TeamScoreThousandths</c> is the official cumulative qualification team
    /// total (final points with active bonus, summed over the group's four legs
    /// times eight rounds); <c>TeamBaseThousandths</c> is retained for auditing
    /// but never decides wildcards. Group standings and intra-group tiebreaks
    /// are authoritative for which team is the candidate; cross-group comparison
    /// uses only the adjusted score below.
    /// </summary>
    public sealed record WildcardCandidate(
        int QualificationGroup,
        string Team,
        int GroupRank,
        int TeamScoreThousandths,
        int TeamBaseThousandths,
        int GroupSize);

    /// <summary>
    /// Wildcard resolution outcome: which candidates earned the global wildcard
    /// places, in deterministic selection order, plus whether a seeded draw was
    /// consumed (only when the cutoff splits exactly tied adjusted performances).
    /// </summary>
    public sealed record WildcardResolution(
        IReadOnlyList<WildcardCandidate> Winners,
        IReadOnlyList<WildcardCandidate> Eliminated,
        bool TieDrawConsumed);

    /// <summary>
    /// Compares two wildcard candidates best-first by adjusted performance
    /// (MSS-071): <c>teamScore * groupSize / expectedBaseSum(groupSize)</c>.
    /// The team total is official final points (base times active bonus, so
    /// bonus advantage is preserved proportionally); the divisor is the
    /// expected average base per entrant from the versioned 32-place table, so
    /// different group sizes are comparable. The common four-leg times
    /// eight-round factor cancels and is omitted. Comparison is exact integer
    /// cross-multiplication (no floating point): returns negative when
    /// <paramref name="left"/> ranks ahead, positive when behind, zero when
    /// exactly tied. For equal-sized groups the group-size and expected-sum
    /// factors are identical, so this reduces exactly to comparing official
    /// team point totals highest-first. Never considers selection rating, group
    /// number, strength estimates or alphabetical names.
    /// </summary>
    public static int CompareWildcardCandidates(WildcardCandidate left, WildcardCandidate right, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrWhiteSpace(left.Team) || string.IsNullOrWhiteSpace(right.Team))
        {
            throw new InvalidOperationException("Wildcard candidate team name must not be empty.");
        }

        if (left.TeamScoreThousandths < 0 || right.TeamScoreThousandths < 0)
        {
            throw new InvalidOperationException("Wildcard candidate team score cannot be negative.");
        }

        long leftSum = ExpectedBaseSumThousandths(left.GroupSize, rules);
        long rightSum = ExpectedBaseSumThousandths(right.GroupSize, rules);
        checked
        {
            // Compare left.Score * left.N / left.Sum vs right.Score * right.N / right.Sum
            // via cross-multiplication: left.Score * left.N * right.Sum vs right.Score * right.N * left.Sum.
            long lhs = (long)left.TeamScoreThousandths * left.GroupSize * rightSum;
            long rhs = (long)right.TeamScoreThousandths * right.GroupSize * leftSum;
            if (lhs != rhs)
            {
                return lhs > rhs ? -1 : 1;
            }

            return 0;
        }
    }

    /// <summary>
    /// Resolves global wildcards from one candidate per group (MSS-071). The
    /// input order never affects the result: candidates are canonically ordered
    /// by adjusted performance best-first, ties by team-name ordinal, before any
    /// RNG use. The best <paramref name="wildcardCount"/> advance. RNG is
    /// consumed only when the cutoff splits exactly tied adjusted performances:
    /// the tied run straddling the cutoff (already name-ordered) is shuffled
    /// once with <paramref name="rng"/> and the needed prefix advances, so
    /// group numbers and alphabetical names never confer an advantage. Untied
    /// cutoffs and fully-decided tie runs never advance the RNG. Returns winners
    /// in selection order (adjusted best-first, shuffled order inside a split
    /// tie) plus the eliminated remainder.
    /// </summary>
    public static WildcardResolution ResolveWildcards(
        IReadOnlyList<WildcardCandidate> candidates,
        int wildcardCount,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        ValidateWildcardRequest(candidates, wildcardCount, rules);
        if (candidates.Count == 0)
        {
            return new WildcardResolution([], [], false);
        }

        List<WildcardCandidate> ordered = OrderWildcardCandidates(candidates, rules);
        if (wildcardCount == 0 || wildcardCount == ordered.Count)
        {
            return wildcardCount == 0
                ? new WildcardResolution([], ordered, false)
                : new WildcardResolution(ordered, [], false);
        }

        return ResolveWildcardCutoff(ordered, wildcardCount, rng, rules);
    }

    private static void ValidateWildcardRequest(
        IReadOnlyList<WildcardCandidate> candidates,
        int wildcardCount,
        RulesV1 rules)
    {
        if (wildcardCount < 0 || wildcardCount > candidates.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(wildcardCount),
                $"Wildcard count must be 0..{candidates.Count}, was {wildcardCount}.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (WildcardCandidate candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Team))
            {
                throw new InvalidOperationException("Wildcard candidate team name must not be empty.");
            }

            if (!seen.Add(candidate.Team))
            {
                throw new InvalidOperationException($"Wildcard candidates contain duplicate team '{candidate.Team}'.");
            }

            if (candidate.GroupSize < 1 || candidate.GroupSize > rules.ScoringTable.Count)
            {
                throw new InvalidOperationException($"Wildcard candidate group size {candidate.GroupSize} is out of range.");
            }
        }
    }

    private static List<WildcardCandidate> OrderWildcardCandidates(
        IReadOnlyList<WildcardCandidate> candidates,
        RulesV1 rules)
    {
        List<WildcardCandidate> ordered = [.. candidates];
        ordered.Sort((a, b) =>
        {
            int adjusted = CompareWildcardCandidates(a, b, rules);
            return adjusted != 0
                ? adjusted
                : string.Compare(a.Team, b.Team, StringComparison.Ordinal);
        });
        return ordered;
    }

    private static WildcardResolution ResolveWildcardCutoff(
        List<WildcardCandidate> ordered,
        int wildcardCount,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        (int runStart, int runEnd) = WildcardTieRun(ordered, wildcardCount, rules);
        bool cutoffSplitsTie = runStart < wildcardCount && runEnd > wildcardCount;
        if (!cutoffSplitsTie)
        {
            List<WildcardCandidate> winners = ordered.GetRange(0, wildcardCount);
            List<WildcardCandidate> eliminated = ordered.GetRange(wildcardCount, ordered.Count - wildcardCount);
            return new WildcardResolution(winners, eliminated, false);
        }

        return ResolveSplitTie(ordered, wildcardCount, runStart, runEnd, rng);
    }

    private static (int RunStart, int RunEnd) WildcardTieRun(
        List<WildcardCandidate> ordered,
        int wildcardCount,
        RulesV1 rules)
    {
        int runStart = wildcardCount - 1;
        while (runStart > 0 && CompareWildcardCandidates(ordered[runStart - 1], ordered[wildcardCount - 1], rules) == 0)
        {
            runStart--;
        }

        int runEnd = wildcardCount;
        while (runEnd < ordered.Count && CompareWildcardCandidates(ordered[runEnd], ordered[wildcardCount - 1], rules) == 0)
        {
            runEnd++;
        }

        return (runStart, runEnd);
    }

    private static WildcardResolution ResolveSplitTie(
        List<WildcardCandidate> ordered,
        int wildcardCount,
        int runStart,
        int runEnd,
        Pcg32V1 rng)
    {
        // The straddling run is already name-ordered; shuffle once so the RNG
        // alone decides which tied candidates advance.
        List<WildcardCandidate> tied = ordered.GetRange(runStart, runEnd - runStart);
        DeterministicShuffle.Shuffle(tied, rng);
        List<WildcardCandidate> resolved = new(ordered.Count);
        resolved.AddRange(ordered.GetRange(0, runStart));
        resolved.AddRange(tied);
        resolved.AddRange(ordered.GetRange(runEnd, ordered.Count - runEnd));

        List<WildcardCandidate> splitWinners = resolved.GetRange(0, wildcardCount);
        List<WildcardCandidate> splitEliminated = resolved.GetRange(wildcardCount, resolved.Count - wildcardCount);
        return new WildcardResolution(splitWinners, splitEliminated, true);
    }

    /// <summary>
    /// Randomly distributes all selected teams into balanced qualification groups
    /// using only the supplied versioned RNG. The input is canonicalized by
    /// ordinal sort so database ordering never affects the draw; the sorted
    /// list is shuffled with <see cref="DeterministicShuffle"/> and dealt into
    /// contiguous balanced chunks numbered 1..G in draw order. Equivalent
    /// starting state plus RNG produces the same draw. Throws for a direct
    /// Final (no draw needed).
    /// </summary>
    public static IReadOnlyList<QualificationAssignment> DrawQualificationGroups(
        IReadOnlyList<string> creatureTypes,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(creatureTypes);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        if (creatureTypes.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(creatureTypes), "Draw requires at least one team.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string type in creatureTypes)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                throw new InvalidOperationException("Draw contains an empty creature type.");
            }

            if (!seen.Add(type))
            {
                throw new InvalidOperationException($"Draw contains duplicate creature type '{type}'.");
            }
        }

        if (IsDirectFinal(creatureTypes.Count, rules))
        {
            throw new InvalidOperationException(
                $"Draw requires more than {rules.TypeCupMaxDirectFinalTeams} teams; {creatureTypes.Count} teams run a direct Final.");
        }

        IReadOnlyList<int> sizes = BalancedQualificationGroupSizes(creatureTypes.Count, rules);
        List<string> canonical = creatureTypes.OrderBy(t => t, StringComparer.Ordinal).ToList();
        string[] shuffled = DeterministicShuffle.ShuffledCopy(canonical, rng);

        List<QualificationAssignment> assignments = new(creatureTypes.Count);
        int index = 0;
        for (int group = 1; group <= sizes.Count; group++)
        {
            for (int i = 0; i < sizes[group - 1]; i++)
            {
                assignments.Add(new QualificationAssignment(shuffled[index], group));
                index++;
            }
        }

        return assignments;
    }

    /// <summary>
    /// Validates a persisted v1 fixed-quota draw: every selected team appears exactly
    /// once, group sizes are balanced (differ by at most one), each group holds at
    /// most 32 teams, quotas sum to exactly 32, and no quota exceeds its group size.
    /// Retained for already-persisted v1 editions; new draws use
    /// <see cref="ValidateWildcardTournamentDraw"/>.
    /// </summary>
    public static void ValidateTournamentDraw(
        IReadOnlyList<string> selectedTypes,
        IReadOnlyList<QualificationAssignment> assignments,
        IReadOnlyList<int> groupSizes,
        IReadOnlyList<int> finalPlaces,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(selectedTypes);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(groupSizes);
        ArgumentNullException.ThrowIfNull(finalPlaces);
        ArgumentNullException.ThrowIfNull(rules);

        CheckDrawField(selectedTypes, groupSizes, rules);
        CheckDrawAssignments(selectedTypes, assignments, groupSizes);
        CheckDrawQuotas(groupSizes, finalPlaces, rules);
    }

    /// <summary>
    /// Validates a persisted v2 wildcard draw: same field/assignment checks as v1,
    /// but quotas are equal guaranteed places (<c>floor(32 / G)</c> per group) and
    /// the global wildcard count completes the 32-team Final. Each guaranteed
    /// quota must equal the base, must not exceed its group size, and
    /// <c>G * base + wildcardCount</c> must equal exactly 32.
    /// </summary>
    public static void ValidateWildcardTournamentDraw(
        IReadOnlyList<string> selectedTypes,
        IReadOnlyList<QualificationAssignment> assignments,
        IReadOnlyList<int> groupSizes,
        IReadOnlyList<int> guaranteedPlaces,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(selectedTypes);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(groupSizes);
        ArgumentNullException.ThrowIfNull(guaranteedPlaces);
        ArgumentNullException.ThrowIfNull(rules);

        CheckDrawField(selectedTypes, groupSizes, rules);
        CheckDrawAssignments(selectedTypes, assignments, groupSizes);
        CheckWildcardQuotas(groupSizes, guaranteedPlaces, rules);
    }

    /// <summary>
    /// Validates a persisted draw of either policy version. Version 1 uses fixed
    /// quotas totalling exactly 32; version 2 uses equal guaranteed quotas plus
    /// global wildcards totalling exactly 32. Unknown versions throw.
    /// </summary>
    public static void ValidateTournamentDrawVersioned(
        IReadOnlyList<string> selectedTypes,
        IReadOnlyList<QualificationAssignment> assignments,
        IReadOnlyList<int> groupSizes,
        IReadOnlyList<int> finalPlaces,
        int tournamentFormatVersion,
        RulesV1 rules)
    {
        if (tournamentFormatVersion == RulesV1.FixedQuotaTypeCupTournamentFormatVersion)
        {
            ValidateTournamentDraw(selectedTypes, assignments, groupSizes, finalPlaces, rules);
            return;
        }

        if (tournamentFormatVersion == RulesV1.WildcardTypeCupTournamentFormatVersion)
        {
            ValidateWildcardTournamentDraw(selectedTypes, assignments, groupSizes, finalPlaces, rules);
            return;
        }

        throw new InvalidOperationException(
            $"Type Cup draw format version {tournamentFormatVersion} is not a scalable qualification version.");
    }

    private static void CheckDrawField(
        IReadOnlyList<string> selectedTypes,
        IReadOnlyList<int> groupSizes,
        RulesV1 rules)
    {
        if (IsDirectFinal(selectedTypes.Count, rules))
        {
            throw new InvalidOperationException("Direct-Final fields have no qualification draw to validate.");
        }

        int expectedGroups = QualificationGroupCount(selectedTypes.Count, rules);
        if (groupSizes.Count != expectedGroups)
        {
            throw new InvalidOperationException(
                $"Draw must hold exactly {expectedGroups} qualification groups, was {groupSizes.Count}.");
        }

        IReadOnlyList<int> expectedSizes = BalancedQualificationGroupSizes(selectedTypes.Count, rules);
        if (!groupSizes.SequenceEqual(expectedSizes))
        {
            throw new InvalidOperationException("Draw group sizes are not balanced as mathematically required.");
        }

        foreach (int size in groupSizes)
        {
            if (size > rules.TypeCupMaxDirectFinalTeams)
            {
                throw new InvalidOperationException($"Qualification group size {size} exceeds 32 teams.");
            }
        }

        if (groupSizes.Max() - groupSizes.Min() > 1)
        {
            throw new InvalidOperationException("Qualification group sizes differ by more than one team.");
        }

        if (new HashSet<string>(selectedTypes, StringComparer.Ordinal).Count != selectedTypes.Count)
        {
            throw new InvalidOperationException("Selected field contains duplicate creature types.");
        }
    }

    private static void CheckDrawAssignments(
        IReadOnlyList<string> selectedTypes,
        IReadOnlyList<QualificationAssignment> assignments,
        IReadOnlyList<int> groupSizes)
    {
        HashSet<string> selected = new(selectedTypes, StringComparer.Ordinal);
        if (assignments.Count != selectedTypes.Count)
        {
            throw new InvalidOperationException(
                $"Draw must assign exactly {selectedTypes.Count} teams, was {assignments.Count}.");
        }

        Dictionary<int, int> counts = new();
        HashSet<string> assigned = new(StringComparer.Ordinal);
        foreach (QualificationAssignment assignment in assignments)
        {
            CheckSingleAssignment(assignment, selected, assigned, groupSizes.Count);
            counts.TryGetValue(assignment.QualificationGroup, out int current);
            counts[assignment.QualificationGroup] = current + 1;
        }

        for (int group = 1; group <= groupSizes.Count; group++)
        {
            if (!counts.TryGetValue(group, out int count) || count != groupSizes[group - 1])
            {
                throw new InvalidOperationException($"Draw group {group} must hold exactly {groupSizes[group - 1]} teams.");
            }
        }
    }

    private static void CheckSingleAssignment(
        QualificationAssignment assignment,
        HashSet<string> selected,
        HashSet<string> assigned,
        int groupCount)
    {
        if (!selected.Contains(assignment.CreatureType))
        {
            throw new InvalidOperationException($"Draw assigns unknown team '{assignment.CreatureType}'.");
        }

        if (!assigned.Add(assignment.CreatureType))
        {
            throw new InvalidOperationException($"Draw assigns team '{assignment.CreatureType}' more than once.");
        }

        if (assignment.QualificationGroup < 1 || assignment.QualificationGroup > groupCount)
        {
            throw new InvalidOperationException($"Draw group {assignment.QualificationGroup} is out of range.");
        }
    }

    private static void CheckDrawQuotas(
        IReadOnlyList<int> groupSizes,
        IReadOnlyList<int> finalPlaces,
        RulesV1 rules)
    {
        if (finalPlaces.Count != groupSizes.Count)
        {
            throw new InvalidOperationException("Final-place quotas must cover every qualification group.");
        }

        int total = 0;
        checked
        {
            foreach (int quota in finalPlaces)
            {
                if (quota < 0)
                {
                    throw new InvalidOperationException("Final-place quota cannot be negative.");
                }

                total += quota;
            }
        }

        if (total != rules.TypeCupFinalTeamCount)
        {
            throw new InvalidOperationException($"Final-place quotas must total exactly {rules.TypeCupFinalTeamCount}, was {total}.");
        }

        for (int i = 0; i < groupSizes.Count; i++)
        {
            if (finalPlaces[i] > groupSizes[i])
            {
                throw new InvalidOperationException($"Group {i + 1} quota {finalPlaces[i]} exceeds its size {groupSizes[i]}.");
            }
        }
    }

    private static void CheckWildcardQuotas(
        IReadOnlyList<int> groupSizes,
        IReadOnlyList<int> guaranteedPlaces,
        RulesV1 rules)
    {
        if (guaranteedPlaces.Count != groupSizes.Count)
        {
            throw new InvalidOperationException("Guaranteed quotas must cover every qualification group.");
        }

        int groupCount = groupSizes.Count;
        int expectedBase = rules.TypeCupFinalTeamCount / groupCount;
        int expectedWildcards = rules.TypeCupFinalTeamCount - (groupCount * expectedBase);
        for (int i = 0; i < groupCount; i++)
        {
            if (guaranteedPlaces[i] != expectedBase)
            {
                throw new InvalidOperationException(
                    $"Group {i + 1} guaranteed quota must be {expectedBase} under the wildcard policy, was {guaranteedPlaces[i]}.");
            }

            if (guaranteedPlaces[i] < 0 || guaranteedPlaces[i] > groupSizes[i])
            {
                throw new InvalidOperationException($"Group {i + 1} guaranteed quota {guaranteedPlaces[i]} exceeds its size {groupSizes[i]}.");
            }
        }

        checked
        {
            int total = 0;
            foreach (int quota in guaranteedPlaces)
            {
                total += quota;
            }

            if (total + expectedWildcards != rules.TypeCupFinalTeamCount)
            {
                throw new InvalidOperationException(
                    $"Guaranteed quotas ({total}) plus wildcards ({expectedWildcards}) must total exactly {rules.TypeCupFinalTeamCount}.");
            }
        }
    }

    /// <summary>
    /// Validates that a new-format competition field never exceeds 32 teams.
    /// Qualification groups and the Final must use the standard scoring table;
    /// the legacy greater-than-32 minimum-point extension is reserved for reading
    /// historical single-field Cups and must never be generated by new draws.
    /// </summary>
    public static void ValidateCompetitionFieldSize(int fieldSize, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (fieldSize < 1 || fieldSize > rules.TypeCupMaxDirectFinalTeams)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fieldSize),
                $"New-format competition field must hold 1..{rules.TypeCupMaxDirectFinalTeams} teams, was {fieldSize}.");
        }
    }

    /// <summary>
    /// Content fingerprint for a qualification draw: lowercase hex SHA-256 over
    /// a header line plus one line per team in (group, type-ordinal) order.
    /// SHA-256 is a fingerprint here, never sporting randomness.
    /// </summary>
    public static string ComputeDrawChecksum(
        IReadOnlyList<QualificationAssignment> assignments,
        int teamCount,
        IReadOnlyList<int> groupSizes)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(groupSizes);
        List<QualificationAssignment> ordered = assignments
            .OrderBy(a => a.QualificationGroup)
            .ThenBy(a => a.CreatureType, StringComparer.Ordinal)
            .ToList();
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        builder.Append(teamCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(groupSizes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append('\n');
        foreach (QualificationAssignment assignment in ordered)
        {
            builder.Append(assignment.QualificationGroup.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(assignment.CreatureType);
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
