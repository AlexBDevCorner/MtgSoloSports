using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Tournament plan for the scalable Type Cup format (MSS-062).
/// Pure: no persistence, no RNG consumed. Distinguishes three execution modes:
/// legacy single-field (format v0, phase 0), direct Final (format v1, 1-32 teams,
/// phase Final), and qualification plus Final (format v1, &gt;32 teams, phases
/// Qualification per group plus Final). Each competition stage uses the existing
/// four athlete-rank groups by eight rounds.
/// </summary>
public static class TypeCupTournamentPlan
{
    public sealed record StageKey(int Phase, int QualificationGroup)
    {
        public bool IsQualification => Phase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification;

        public bool IsFinal => Phase == (int)TypeCupTournamentFormat.TournamentPhase.Final;

        public bool IsLegacy => Phase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField;
    }

    public sealed record QualificationStage(
        int QualificationGroup,
        IReadOnlyList<string> TeamTypes,
        int GroupSize,
        int FinalPlaces);

    public sealed record Plan(
        bool IsLegacy,
        bool IsDirectFinal,
        int TeamCount,
        IReadOnlyList<QualificationStage> QualificationStages,
        int TotalRounds)
    {
        public int QualificationGroupCount => QualificationStages.Count;

        public bool IsTournament => !IsLegacy && !IsDirectFinal;
    }

    public static StageKey LegacyKey() =>
        new((int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField, 0);

    public static StageKey FinalKey() =>
        new((int)TypeCupTournamentFormat.TournamentPhase.Final, 0);

    public static StageKey QualificationKey(int qualificationGroup) =>
        new((int)TypeCupTournamentFormat.TournamentPhase.Qualification, qualificationGroup);

    /// <summary>
    /// Ordered stage sequence for the tournament: qualification groups in
    /// persisted group-number order, then the Final. Legacy and direct Final
    /// hold a single stage.
    /// </summary>
    public static IReadOnlyList<StageKey> StageSequence(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsLegacy)
        {
            return [LegacyKey()];
        }

        if (plan.IsDirectFinal)
        {
            return [FinalKey()];
        }

        List<StageKey> stages = new(plan.QualificationGroupCount + 1);
        foreach (QualificationStage stage in plan.QualificationStages.OrderBy(s => s.QualificationGroup))
        {
            stages.Add(QualificationKey(stage.QualificationGroup));
        }

        stages.Add(FinalKey());
        return stages;
    }

    public static Plan BuildLegacy(int teamCount, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new Plan(
            IsLegacy: true,
            IsDirectFinal: false,
            teamCount,
            [],
            rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds);
    }

    public static Plan BuildDirectFinal(int teamCount, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        TypeCupTournamentFormat.ValidateCompetitionFieldSize(teamCount, rules);
        return new Plan(
            IsLegacy: false,
            IsDirectFinal: true,
            teamCount,
            [],
            rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds);
    }

    public static Plan BuildTournament(
        int teamCount,
        IReadOnlyList<QualificationStage> stages,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(rules);
        if (stages.Count == 0)
        {
            throw new InvalidOperationException("Qualification tournament requires at least one group.");
        }

        int perStage = rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds;
        return new Plan(
            IsLegacy: false,
            IsDirectFinal: false,
            teamCount,
            stages.OrderBy(s => s.QualificationGroup).ToList(),
            (stages.Count * perStage) + perStage);
    }

    /// <summary>
    /// Builds the tournament plan from the selected field and the persisted draw.
    /// For format v0 always returns legacy. For format v1 returns direct Final
    /// for 1-32 teams (draw must be empty) or qualification plus Final for larger
    /// fields (draw must match the selection).
    /// </summary>
    public static Plan BuildFromSelection(
        int teamCount,
        IReadOnlyList<string> selectedTypes,
        IReadOnlyList<Persistence.Saves.TypeCupTournamentDrawEntity> draws,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(selectedTypes);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.TypeCupTournamentFormatVersion == RulesV1.LegacyTypeCupTournamentFormatVersion)
        {
            return BuildLegacy(teamCount, rules);
        }

        if (TypeCupTournamentFormat.IsDirectFinal(teamCount, rules))
        {
            if (draws.Count != 0)
            {
                throw new InvalidOperationException(
                    "Direct-Final Type Cup must not persist a qualification draw.");
            }

            return BuildDirectFinal(teamCount, rules);
        }

        if (draws.Count == 0)
        {
            throw new RunTypeCupTeamConflictException(
                "Type Cup qualification draw must be resolved before the tournament can run.");
        }

        List<QualificationStage> stages = BuildQualificationStages(draws, rules);
        ValidateTournamentQuotas(stages, selectedTypes, rules);
        return BuildTournament(teamCount, stages, rules);
    }

    /// <summary>
    /// Maps a global played-round count to the next stage plus rank-group/round.
    /// Each stage holds 4 rank groups by 8 rounds (32 rounds).
    /// </summary>
    public static (StageKey Stage, int RankGroup, int Round) Cursor(int played, Plan plan, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rules);
        if (played < 0 || played >= plan.TotalRounds)
        {
            throw new ArgumentOutOfRangeException(nameof(played));
        }

        int perStage = rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds;
        IReadOnlyList<StageKey> sequence = StageSequence(plan);
        int stageIndex = played / perStage;
        int withinStage = played % perStage;
        StageKey stage = sequence[stageIndex];
        int rankGroup = (withinStage / rules.TypeCupGroupRounds) + 1;
        int round = (withinStage % rules.TypeCupGroupRounds) + 1;
        return (stage, rankGroup, round);
    }

    /// <summary>
    /// Stage index (0-based into <see cref="StageSequence"/>) holding a persisted
    /// round row, or -1 for legacy rows when the plan is new-format (corruption).
    /// </summary>
    public static int StageIndexOf(StageKey key, Plan plan)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(plan);
        IReadOnlyList<StageKey> sequence = StageSequence(plan);
        for (int i = 0; i < sequence.Count; i++)
        {
            if (sequence[i].Phase == key.Phase && sequence[i].QualificationGroup == key.QualificationGroup)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<QualificationStage> BuildQualificationStages(
        IReadOnlyList<Persistence.Saves.TypeCupTournamentDrawEntity> draws,
        RulesV1 rules)
    {
        var groupNumbers = draws.Select(d => d.QualificationGroup).Distinct().OrderBy(g => g).ToList();
        List<QualificationStage> stages = new(groupNumbers.Count);
        foreach (int group in groupNumbers)
        {
            stages.Add(BuildSingleQualificationStage(draws, group, rules));
        }

        return stages;
    }

    private static QualificationStage BuildSingleQualificationStage(
        IReadOnlyList<Persistence.Saves.TypeCupTournamentDrawEntity> draws,
        int group,
        RulesV1 rules)
    {
        var rows = draws.Where(d => d.QualificationGroup == group).ToList();
        var types = rows.Select(r => r.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList();
        int size = rows.First().GroupSize;
        int quota = rows.First().FinalPlacesForGroup;
        if (rows.Any(r => r.GroupSize != size || r.FinalPlacesForGroup != quota))
        {
            throw new InvalidOperationException(
                $"Type Cup draw group {group} has inconsistent size/quota metadata.");
        }

        TypeCupTournamentFormat.ValidateCompetitionFieldSize(size, rules);
        if (types.Count != size)
        {
            throw new InvalidOperationException(
                $"Type Cup draw group {group} must hold exactly {size} teams, was {types.Count}.");
        }

        return new QualificationStage(group, types, size, quota);
    }

    private static void ValidateTournamentQuotas(
        List<QualificationStage> stages,
        IReadOnlyList<string> selectedTypes,
        RulesV1 rules)
    {
        int totalQuota = stages.Sum(s => s.FinalPlaces);
        if (totalQuota != rules.TypeCupFinalTeamCount)
        {
            throw new InvalidOperationException(
                $"Type Cup qualification quotas must total exactly {rules.TypeCupFinalTeamCount}, was {totalQuota}.");
        }

        HashSet<string> assigned = stages.SelectMany(s => s.TeamTypes).ToHashSet(StringComparer.Ordinal);
        HashSet<string> selected = selectedTypes.ToHashSet(StringComparer.Ordinal);
        if (!assigned.SetEquals(selected))
        {
            throw new InvalidOperationException(
                "Type Cup qualification draw does not cover the selected field exactly once.");
        }
    }

    /// <summary>
    /// Selects the Final field (exactly 32 teams) from completed qualification
    /// standings: top quota teams per group by team rank. No wildcard/strength
    /// override outside the persisted quota.
    /// </summary>
    public static IReadOnlyList<string> SelectFinalists(
        Plan plan,
        IReadOnlyDictionary<int, IReadOnlyList<(string Team, int Rank)>> qualStandings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(qualStandings);
        if (!plan.IsTournament)
        {
            throw new InvalidOperationException("Finalist selection requires a qualification tournament.");
        }

        List<string> finalists = new(32);
        foreach (QualificationStage stage in plan.QualificationStages.OrderBy(s => s.QualificationGroup))
        {
            if (!qualStandings.TryGetValue(stage.QualificationGroup, out IReadOnlyList<(string Team, int Rank)>? standings))
            {
                throw new InvalidOperationException(
                    $"Type Cup qualification group {stage.QualificationGroup} has no completed standings.");
            }

            List<string> top = standings
                .OrderBy(s => s.Rank)
                .Take(stage.FinalPlaces)
                .Select(s => s.Team)
                .OrderBy(t => t, StringComparer.Ordinal)
                .ToList();
            if (top.Count != stage.FinalPlaces)
            {
                throw new InvalidOperationException(
                    $"Type Cup qualification group {stage.QualificationGroup} must advance exactly {stage.FinalPlaces} teams.");
            }

            finalists.AddRange(top);
        }

        if (finalists.Count != 32)
        {
            throw new InvalidOperationException(
                $"Type Cup Final must hold exactly 32 teams, was {finalists.Count}.");
        }

        if (finalists.Distinct(StringComparer.Ordinal).Count() != 32)
        {
            throw new InvalidOperationException("Type Cup Finalists contain duplicate teams.");
        }

        return finalists.OrderBy(t => t, StringComparer.Ordinal).ToList();
    }
}
