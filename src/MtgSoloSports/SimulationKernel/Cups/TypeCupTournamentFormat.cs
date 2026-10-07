using System.Security.Cryptography;
using System.Text;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Pure scalable Type Cup tournament-format mathematics (MSS-061).
/// At most 32 teams compete in any single competition field: 1-32 selected
/// teams run a direct Final with the current four rank-group by eight-round
/// format, while more than 32 teams are randomly drawn into balanced
/// qualification groups of at most 32 teams each, advancing exactly 32 teams
/// to a fresh Final. All methods are deterministic fixed-point/integer-only;
/// the only randomness is the caller-supplied <see cref="Pcg32V1"/> draw,
/// never strength-seeded, never clock/GUID/database ordering.
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
    /// Final-place quota per qualification group (indexed by 0-based group
    /// position, corresponding to persisted groups 1..G). Every group receives
    /// floor(32 / groupCount); the first remainder groups in deterministic
    /// (size descending, group number ascending) order receive one extra place
    /// so larger groups advance more teams before equal-sized groups. The total
    /// is always exactly 32.
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
    /// Validates a persisted draw: every selected team appears exactly once,
    /// group sizes are balanced (differ by at most one), each group holds at
    /// most 32 teams, quotas sum to exactly 32, and no quota exceeds its group size.
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
