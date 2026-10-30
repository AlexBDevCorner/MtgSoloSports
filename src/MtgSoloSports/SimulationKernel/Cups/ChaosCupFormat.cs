using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Pure Chaos Cup format and eight-color quota mathematics (MSS-098).
/// Approved Chaos Cup: 1024 entrants (all source-season league athletes plus
/// random Pool invitations), six 32-athlete group stages with group counts
/// 32/16/8/4/2/1, top 16 from each nonfinal group, 16 game rounds per group,
/// active bonus used but not earned, no league points/promotion, group scores
/// reset, first draw exactly 4/color/group, later draws pure random.
/// The Final is the single 32-athlete stage with official places 1..32.
/// Eliminated athletes carry only an exit-stage category; no global numeric
/// rank is fabricated for nonfinalists.
/// All methods are deterministic integer-only with no RNG, clock, GUID or
/// database ordering. Sporting randomness (Pool invitations, draws) belongs
/// to later slices; this type only centralizes sizes, quotas and invariants.
/// </summary>
public static class ChaosCupFormat
{
    /// <summary>
    /// Versioned Chaos format rule. Persisted by later slices alongside the
    /// draw so future format evolution never silently reinterprets history.
    /// </summary>
    public const int ChaosFormatVersion = 1;

    /// <summary>Exact tournament field: 1024 entrants.</summary>
    public const int FieldSize = 1024;

    /// <summary>Athletes per group in every stage: 32.</summary>
    public const int GroupSize = 32;

    /// <summary>Game rounds per group: 16, identical to a league stage.</summary>
    public const int RoundsPerGroup = 16;

    /// <summary>Athletes advancing from each nonfinal group: top 16.</summary>
    public const int AdvancingPerGroup = 16;

    /// <summary>Official Final field: 32 athletes in one group.</summary>
    public const int FinalFieldSize = 32;

    /// <summary>Entrants per sporting color: 128 (1024 / 8).</summary>
    public const int EntrantsPerColor = 128;

    /// <summary>
    /// First-stage color balance: exactly 4 of each color per group
    /// (8 colors x 4 = 32). Later stages have no color/tier seeding.
    /// </summary>
    public const int FirstStagePerColorPerGroup = 4;

    /// <summary>
    /// Group counts per stage in play order: 32, 16, 8, 4, 2, 1.
    /// Six stages total; the last entry is the official Final.
    /// </summary>
    public static IReadOnlyList<int> StageGroupCounts { get; } = [32, 16, 8, 4, 2, 1];

    /// <summary>Number of group stages: 6.</summary>
    public static int StageCount => StageGroupCounts.Count;

    /// <summary>
    /// Exit-stage categories. Nonfinalists are identified only by the stage
    /// that eliminated them; only <see cref="ChaosExitStage.Finalist"/>
    /// athletes hold official Final places 1..32.
    /// </summary>
    public enum ChaosExitStage
    {
        EliminatedInStageWith32Groups = 0,
        EliminatedInStageWith16Groups = 1,
        EliminatedInStageWith8Groups = 2,
        EliminatedInStageWith4Groups = 3,
        EliminatedInStageWith2Groups = 4,
        Finalist = 5,
    }

    /// <summary>One Chaos entrant: athlete identity plus sporting color.</summary>
    public sealed record ChaosEntrant(int AthleteId, SportingColor Color);

    /// <summary>
    /// Group count for a zero-based stage index (0..5).
    /// </summary>
    public static int GroupCountForStage(int stageIndex)
    {
        if (stageIndex < 0 || stageIndex >= StageGroupCounts.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(stageIndex), $"Chaos stage index must be 0..{StageGroupCounts.Count - 1}, was {stageIndex}.");
        }

        return StageGroupCounts[stageIndex];
    }

    /// <summary>
    /// Entrant count for a stage: groups x 32.
    /// Yields 1024, 512, 256, 128, 64, 32.
    /// </summary>
    public static int EntrantCountForStage(int stageIndex)
    {
        checked
        {
            return GroupCountForStage(stageIndex) * GroupSize;
        }
    }

    /// <summary>
    /// True for the final single-group stage (index 5); its 32 athletes hold
    /// official places 1..32.
    /// </summary>
    public static bool IsFinalStage(int stageIndex) => GroupCountForStage(stageIndex) == 1;

    /// <summary>
    /// Advancing entrants from a stage: groups x 16 for nonfinal stages,
    /// zero for the Final (no further advancement).
    /// </summary>
    public static int AdvancingCountForStage(int stageIndex)
    {
        int groups = GroupCountForStage(stageIndex);
        if (groups == 1)
        {
            return 0;
        }

        checked
        {
            return groups * AdvancingPerGroup;
        }
    }

    /// <summary>
    /// Eliminated entrants in a stage: groups x 16 for nonfinal stages,
    /// zero for the Final (every finalist is ranked 1..32).
    /// </summary>
    public static int EliminatedCountForStage(int stageIndex)
    {
        int groups = GroupCountForStage(stageIndex);
        if (groups == 1)
        {
            return 0;
        }

        checked
        {
            return groups * (GroupSize - AdvancingPerGroup);
        }
    }

    /// <summary>
    /// Exit-stage category for athletes eliminated in a nonfinal stage
    /// (stage index 0..4). The Final (index 5) has no elimination category;
    /// its athletes are <see cref="ChaosExitStage.Finalist"/>.
    /// </summary>
    public static ChaosExitStage ExitStageForElimination(int stageIndex)
    {
        int groups = GroupCountForStage(stageIndex);
        return groups switch
        {
            32 => ChaosExitStage.EliminatedInStageWith32Groups,
            16 => ChaosExitStage.EliminatedInStageWith16Groups,
            8 => ChaosExitStage.EliminatedInStageWith8Groups,
            4 => ChaosExitStage.EliminatedInStageWith4Groups,
            2 => ChaosExitStage.EliminatedInStageWith2Groups,
            _ => throw new InvalidOperationException($"Chaos Final stage {stageIndex} eliminates nobody; its athletes are Finalists."),
        };
    }

    /// <summary>
    /// Pool invitations required for one color: 128 minus active
    /// source-season athletes of that color. Validates 0..128 actives;
    /// more than 128 actives aborts (corrupt roster, never silently capped).
    /// </summary>
    public static int PoolInvitationsForColor(int activeCountForColor)
    {
        if (activeCountForColor < 0 || activeCountForColor > EntrantsPerColor)
        {
            throw new InvalidOperationException($"Chaos active athletes per color must be 0..{EntrantsPerColor}, was {activeCountForColor}.");
        }

        checked
        {
            return EntrantsPerColor - activeCountForColor;
        }
    }

    /// <summary>
    /// Total Pool invitations for a field: 1024 minus total actives.
    /// </summary>
    public static int TotalPoolInvitations(int totalActive)
    {
        if (totalActive < 0 || totalActive > FieldSize)
        {
            throw new InvalidOperationException($"Chaos total actives must be 0..{FieldSize}, was {totalActive}.");
        }

        checked
        {
            return FieldSize - totalActive;
        }
    }

    /// <summary>
    /// Per-color Pool invitations for all eight sporting colors.
    /// Requires exactly the eight known colors; each active count must be
    /// 0..128. The invitation sum always equals 1024 minus total actives
    /// (normal tiered 800/224, Season 1 768/256, legacy 256/768 or 288/736).
    /// </summary>
    public static IReadOnlyDictionary<SportingColor, int> ComputePoolInvitations(
        IReadOnlyDictionary<SportingColor, int> activePerColor)
    {
        ArgumentNullException.ThrowIfNull(activePerColor);
        SportingColor[] colors = Enum.GetValues<SportingColor>();
        if (activePerColor.Count != colors.Length)
        {
            throw new InvalidOperationException($"Chaos quotas require exactly {colors.Length} sporting colors, was {activePerColor.Count}.");
        }

        Dictionary<SportingColor, int> invitations = new(colors.Length);
        foreach (SportingColor color in colors)
        {
            if (!activePerColor.TryGetValue(color, out int active))
            {
                throw new InvalidOperationException($"Chaos quotas miss sporting color {color}.");
            }

            invitations[color] = PoolInvitationsForColor(active);
        }

        return invitations;
    }

    /// <summary>
    /// Validates that each color's common pool holds enough athletes for its
    /// invitations. Pool available must be non-negative and at least the
    /// required invitations; otherwise the mutation aborts.
    /// </summary>
    public static void ValidatePoolAvailability(
        IReadOnlyDictionary<SportingColor, int> activePerColor,
        IReadOnlyDictionary<SportingColor, int> poolAvailablePerColor)
    {
        ArgumentNullException.ThrowIfNull(activePerColor);
        ArgumentNullException.ThrowIfNull(poolAvailablePerColor);
        IReadOnlyDictionary<SportingColor, int> required = ComputePoolInvitations(activePerColor);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            if (!poolAvailablePerColor.TryGetValue(color, out int available))
            {
                throw new InvalidOperationException($"Chaos pool availability misses sporting color {color}.");
            }

            if (available < 0)
            {
                throw new InvalidOperationException($"Chaos pool availability for {color} cannot be negative.");
            }

            if (available < required[color])
            {
                throw new InvalidOperationException(
                    $"Chaos pool for {color} holds {available} athletes but needs {required[color]} invitations.");
            }
        }
    }

    /// <summary>
    /// Validates the frozen 1024-entrant field: exact size, no duplicate
    /// athlete ids, eight known sporting colors, exactly 128 per color.
    /// </summary>
    public static void ValidateEntrantField(IReadOnlyList<ChaosEntrant> entrants)
    {
        ArgumentNullException.ThrowIfNull(entrants);
        if (entrants.Count != FieldSize)
        {
            throw new InvalidOperationException($"Chaos field must hold exactly {FieldSize} entrants, was {entrants.Count}.");
        }

        HashSet<int> ids = new(entrants.Count);
        Dictionary<SportingColor, int> perColor = new();
        foreach (ChaosEntrant entrant in entrants)
        {
            if (entrant is null)
            {
                throw new InvalidOperationException("Chaos field contains a null entrant.");
            }

            if (entrant.AthleteId <= 0)
            {
                throw new InvalidOperationException($"Chaos entrant has corrupt athlete id {entrant.AthleteId}.");
            }

            if (!Enum.IsDefined(entrant.Color))
            {
                throw new InvalidOperationException($"Chaos entrant {entrant.AthleteId} has unknown sporting color {(int)entrant.Color}.");
            }

            if (!ids.Add(entrant.AthleteId))
            {
                throw new InvalidOperationException($"Chaos field contains duplicate athlete {entrant.AthleteId}.");
            }

            perColor.TryGetValue(entrant.Color, out int current);
            checked
            {
                perColor[entrant.Color] = current + 1;
            }
        }

        SportingColor[] colors = Enum.GetValues<SportingColor>();
        if (perColor.Count != colors.Length)
        {
            throw new InvalidOperationException($"Chaos field must cover all {colors.Length} sporting colors, was {perColor.Count}.");
        }

        foreach (SportingColor color in colors)
        {
            if (!perColor.TryGetValue(color, out int count) || count != EntrantsPerColor)
            {
                throw new InvalidOperationException($"Chaos field must hold exactly {EntrantsPerColor} {color} entrants, was {count}.");
            }
        }
    }

    /// <summary>
    /// Validates first-stage groups: 32 groups of 32 covering the field
    /// exactly once, each group holding exactly 4 of every sporting color.
    /// </summary>
    public static void ValidateFirstStageGroups(
        IReadOnlyList<ChaosEntrant> entrants,
        IReadOnlyList<IReadOnlyList<ChaosEntrant>> groups)
    {
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(groups);
        ValidateEntrantField(entrants);
        ValidateStageGroups(0, entrants, groups);
        for (int i = 0; i < groups.Count; i++)
        {
            ValidateSingleFirstStageGroup(groups[i], i);
        }
    }

    /// <summary>
    /// Validates stage groups for any stage index: exact group count and
    /// 32-member groups covering the stage entrants exactly once with no
    /// duplicates. Stage 0 additionally requires 4/color/group; later stages
    /// enforce no color/tier seeding (pure random composition is accepted).
    /// </summary>
    public static void ValidateStageGroups(
        int stageIndex,
        IReadOnlyList<ChaosEntrant> stageEntrants,
        IReadOnlyList<IReadOnlyList<ChaosEntrant>> groups)
    {
        ArgumentNullException.ThrowIfNull(stageEntrants);
        ArgumentNullException.ThrowIfNull(groups);
        int expectedGroups = GroupCountForStage(stageIndex);
        if (groups.Count != expectedGroups)
        {
            throw new InvalidOperationException($"Chaos stage {stageIndex} must hold exactly {expectedGroups} groups, was {groups.Count}.");
        }

        if (stageEntrants.Count != EntrantCountForStage(stageIndex))
        {
            throw new InvalidOperationException($"Chaos stage {stageIndex} must hold exactly {EntrantCountForStage(stageIndex)} entrants, was {stageEntrants.Count}.");
        }

        HashSet<int> field = CheckStageField(stageIndex, stageEntrants);
        CheckStageCoverage(stageIndex, stageEntrants, groups, field);
    }

    private static HashSet<int> CheckStageField(int stageIndex, IReadOnlyList<ChaosEntrant> stageEntrants)
    {
        HashSet<int> field = new(stageEntrants.Count);
        foreach (ChaosEntrant entrant in stageEntrants)
        {
            if (entrant is null)
            {
                throw new InvalidOperationException($"Chaos stage {stageIndex} field contains a null entrant.");
            }

            if (!Enum.IsDefined(entrant.Color))
            {
                throw new InvalidOperationException($"Chaos stage {stageIndex} entrant {entrant.AthleteId} has unknown sporting color {(int)entrant.Color}.");
            }

            if (!field.Add(entrant.AthleteId))
            {
                throw new InvalidOperationException($"Chaos stage {stageIndex} field contains duplicate athlete {entrant.AthleteId}.");
            }
        }

        return field;
    }

    private static void CheckStageCoverage(
        int stageIndex,
        IReadOnlyList<ChaosEntrant> stageEntrants,
        IReadOnlyList<IReadOnlyList<ChaosEntrant>> groups,
        HashSet<int> field)
    {
        HashSet<int> covered = new(stageEntrants.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            CheckSingleStageGroup(stageIndex, groups[i], i, field, covered);
        }

        if (covered.Count != stageEntrants.Count)
        {
            throw new InvalidOperationException($"Chaos stage {stageIndex} groups cover {covered.Count} athletes, expected {stageEntrants.Count}.");
        }
    }

    private static void CheckSingleStageGroup(
        int stageIndex,
        IReadOnlyList<ChaosEntrant> group,
        int groupIndex,
        HashSet<int> field,
        HashSet<int> covered)
    {
        if (group is null)
        {
            throw new InvalidOperationException($"Chaos stage {stageIndex} group {groupIndex + 1} is null.");
        }

        if (group.Count != GroupSize)
        {
            throw new InvalidOperationException($"Chaos stage {stageIndex} group {groupIndex + 1} must hold exactly {GroupSize} athletes, was {group.Count}.");
        }

        foreach (ChaosEntrant entrant in group)
        {
            if (entrant is null)
            {
                throw new InvalidOperationException($"Chaos stage {stageIndex} group {groupIndex + 1} contains a null entrant.");
            }

            if (!field.Contains(entrant.AthleteId))
            {
                throw new InvalidOperationException($"Chaos stage {stageIndex} group {groupIndex + 1} holds athlete {entrant.AthleteId} outside the stage field.");
            }

            if (!covered.Add(entrant.AthleteId))
            {
                throw new InvalidOperationException($"Chaos stage {stageIndex} assigns athlete {entrant.AthleteId} more than once.");
            }
        }
    }

    /// <summary>
    /// Validates that the versioned rules snapshot is compatible with the
    /// Chaos format: 8 colors, 32-athlete groups and 16 rounds per group.
    /// Existing v1/v2/v3 snapshots all satisfy this; incompatible snapshots abort.
    /// </summary>
    public static void ValidateFormatAgainstRules(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();
        if (rules.SportingColorCount != 8)
        {
            throw new InvalidOperationException($"Chaos format requires 8 sporting colors, rules hold {rules.SportingColorCount}.");
        }

        if (rules.LeagueSize != GroupSize)
        {
            throw new InvalidOperationException($"Chaos group size {GroupSize} must equal rules LeagueSize {rules.LeagueSize}.");
        }

        if (rules.RoundsPerStage != RoundsPerGroup)
        {
            throw new InvalidOperationException($"Chaos rounds per group {RoundsPerGroup} must equal rules RoundsPerStage {rules.RoundsPerStage}.");
        }

        if (rules.ScoringTable.Count < GroupSize)
        {
            throw new InvalidOperationException($"Chaos 32-athlete groups need a {GroupSize}-place scoring table, rules hold {rules.ScoringTable.Count}.");
        }

        if (FieldSize != EntrantsPerColor * rules.SportingColorCount)
        {
            throw new InvalidOperationException($"Chaos field {FieldSize} must equal {EntrantsPerColor} x {rules.SportingColorCount}.");
        }

        if (GroupSize != FirstStagePerColorPerGroup * rules.SportingColorCount)
        {
            throw new InvalidOperationException($"Chaos group {GroupSize} must equal {FirstStagePerColorPerGroup} x {rules.SportingColorCount}.");
        }
    }

    private static void ValidateSingleFirstStageGroup(IReadOnlyList<ChaosEntrant> group, int groupIndex)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Count != GroupSize)
        {
            throw new InvalidOperationException($"Chaos first-stage group {groupIndex + 1} must hold exactly {GroupSize} athletes, was {group.Count}.");
        }

        Dictionary<SportingColor, int> perColor = new();
        foreach (ChaosEntrant entrant in group)
        {
            if (entrant is null)
            {
                throw new InvalidOperationException($"Chaos first-stage group {groupIndex + 1} contains a null entrant.");
            }

            if (!Enum.IsDefined(entrant.Color))
            {
                throw new InvalidOperationException($"Chaos first-stage group {groupIndex + 1} holds unknown sporting color {(int)entrant.Color}.");
            }

            perColor.TryGetValue(entrant.Color, out int current);
            checked
            {
                perColor[entrant.Color] = current + 1;
            }
        }

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            if (!perColor.TryGetValue(color, out int count) || count != FirstStagePerColorPerGroup)
            {
                throw new InvalidOperationException(
                    $"Chaos first-stage group {groupIndex + 1} must hold exactly {FirstStagePerColorPerGroup} {color} athletes, was {count}.");
            }
        }
    }
}
