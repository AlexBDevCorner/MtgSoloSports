using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Tournament plan for the scalable Type Cup format (MSS-062, MSS-071).
/// Pure: no persistence, no RNG consumed. Distinguishes three execution modes:
/// legacy single-field (format v0, phase 0), direct Final (1-32 teams, phase
/// Final), and qualification plus Final (&gt;32 teams, phases Qualification per
/// group plus Final). Format v1 uses fixed per-group quotas (extra places to
/// larger groups, then lower numbers); format v2 (MSS-071, current) uses equal
/// guaranteed places per group plus global performance wildcards. Each
/// competition stage uses the existing four athlete-rank groups by eight rounds.
/// </summary>
public static class TypeCupTournamentPlan
{
    public sealed record StageKey(int Phase, int QualificationGroup)
    {
        public bool IsQualification => Phase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification;

        public bool IsFinal => Phase == (int)TypeCupTournamentFormat.TournamentPhase.Final;

        public bool IsLegacy => Phase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField;
    }

    /// <summary>
    /// One qualification stage. <see cref="FinalPlaces"/> is the persisted
    /// per-group quota: the fixed Final quota for v1 draws, the guaranteed
    /// quota for v2 wildcard draws. Use <see cref="Plan.WildcardCount"/> plus
    /// <see cref="Plan.QualificationPolicyVersion"/> to interpret it.
    /// </summary>
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
        int TotalRounds,
        int QualificationPolicyVersion = RulesV1.WildcardTypeCupTournamentFormatVersion,
        int WildcardCount = 0)
    {
        public int QualificationGroupCount => QualificationStages.Count;

        public bool IsTournament => !IsLegacy && !IsDirectFinal;

        public bool IsWildcardPolicy => QualificationPolicyVersion == RulesV1.WildcardTypeCupTournamentFormatVersion;

        /// <summary>Guaranteed places per group (v2) or fixed quota sum check (v1).</summary>
        public int GuaranteedPerGroup => QualificationStages.Count == 0
            ? 0
            : QualificationPolicyVersion == RulesV1.WildcardTypeCupTournamentFormatVersion
                ? RulesV1.DefaultTypeCupFinalTeamCount / QualificationStages.Count
                : 0;
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
            rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds,
            RulesV1.LegacyTypeCupTournamentFormatVersion,
            0);
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
            rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds,
            rules.TypeCupTournamentFormatVersion,
            0);
    }

    public static Plan BuildTournament(
        int teamCount,
        IReadOnlyList<QualificationStage> stages,
        RulesV1 rules,
        int qualificationPolicyVersion,
        int wildcardCount)
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
            (stages.Count * perStage) + perStage,
            qualificationPolicyVersion,
            wildcardCount);
    }

    public static Plan BuildTournament(
        int teamCount,
        IReadOnlyList<QualificationStage> stages,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(rules);
        // Backward-compatible overload: assume wildcard policy for new plans.
        int wildcard = TypeCupTournamentFormat.WildcardCount(stages.Count, rules);
        return BuildTournament(teamCount, stages, rules, RulesV1.WildcardTypeCupTournamentFormatVersion, wildcard);
    }

    /// <summary>
    /// Builds the tournament plan from the selected field and the persisted draw.
    /// For format v0 always returns legacy. For scalable formats returns direct
    /// Final for 1-32 teams (draw must be empty) or qualification plus Final for
    /// larger fields (draw must match the selection). The per-edition draw
    /// version decides the quota policy: v1 draws keep fixed quotas totalling
    /// exactly 32, v2 draws use equal guaranteed quotas plus global wildcards.
    /// Save snapshots at v1 remain able to host v2 editions at an edition
    /// boundary; only legacy snapshots stay on the legacy path.
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

        int policy = draws.First().TournamentFormatVersion;
        if (draws.Any(d => d.TournamentFormatVersion != policy))
        {
            throw new InvalidOperationException(
                "Type Cup qualification draw has mixed tournament format versions within one edition.");
        }

        if (policy != RulesV1.FixedQuotaTypeCupTournamentFormatVersion
            && policy != RulesV1.WildcardTypeCupTournamentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Type Cup draw format version {policy} is not a scalable qualification version.");
        }

        List<QualificationStage> stages = BuildQualificationStages(draws, rules);
        ValidateTournamentQuotas(stages, selectedTypes, rules, policy);
        int wildcards = policy == RulesV1.WildcardTypeCupTournamentFormatVersion
            ? TypeCupTournamentFormat.WildcardCount(stages.Count, rules)
            : 0;
        return BuildTournament(teamCount, stages, rules, policy, wildcards);
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
        RulesV1 rules,
        int policyVersion)
    {
        if (policyVersion == RulesV1.FixedQuotaTypeCupTournamentFormatVersion)
        {
            int totalQuota = stages.Sum(s => s.FinalPlaces);
            if (totalQuota != rules.TypeCupFinalTeamCount)
            {
                throw new InvalidOperationException(
                    $"Type Cup qualification quotas must total exactly {rules.TypeCupFinalTeamCount}, was {totalQuota}.");
            }
        }
        else
        {
            int expectedBase = rules.TypeCupFinalTeamCount / stages.Count;
            foreach (QualificationStage stage in stages)
            {
                if (stage.FinalPlaces != expectedBase)
                {
                    throw new InvalidOperationException(
                        $"Type Cup qualification group {stage.QualificationGroup} guaranteed quota must be {expectedBase} under the wildcard policy, was {stage.FinalPlaces}.");
                }
            }

            int wildcards = TypeCupTournamentFormat.WildcardCount(stages.Count, rules);
            int total = stages.Sum(s => s.FinalPlaces) + wildcards;
            if (total != rules.TypeCupFinalTeamCount)
            {
                throw new InvalidOperationException(
                    $"Type Cup guaranteed quotas plus wildcards must total exactly {rules.TypeCupFinalTeamCount}, was {total}.");
            }
        }

        HashSet<string> assigned = stages.SelectMany(s => s.TeamTypes).ToHashSet(StringComparer.Ordinal);
        HashSet<string> selected = selectedTypes.ToHashSet(StringComparer.Ordinal);
        if (!assigned.SetEquals(selected))
        {
            throw new InvalidOperationException(
                "Type Cup qualification draw does not cover the selected field exactly once.");
        }
    }

    private static void ValidateTournamentQuotas(
        List<QualificationStage> stages,
        IReadOnlyList<string> selectedTypes,
        RulesV1 rules)
    {
        // Backward-compatible overload: infer policy from quota shape.
        int policy = stages.Sum(s => s.FinalPlaces) == rules.TypeCupFinalTeamCount
            ? RulesV1.FixedQuotaTypeCupTournamentFormatVersion
            : RulesV1.WildcardTypeCupTournamentFormatVersion;
        ValidateTournamentQuotas(stages, selectedTypes, rules, policy);
    }

    /// <summary>
    /// Selects the Final field (exactly 32 teams) from completed qualification
    /// standings under the v1 fixed-quota policy: top quota teams per group by
    /// team rank. No wildcard/strength override outside the persisted quota.
    /// Retained for already-persisted v1 editions; new editions use
    /// <see cref="SelectWildcardFinalists"/>.
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

    /// <summary>
    /// One group's completed qualification standings for wildcard selection:
    /// team name, group rank, official team score and base totals plus group size.
    /// Group ranks come from authoritative intra-group standings (including
    /// tiebreaks); cross-group comparison uses only the adjusted score.
    /// </summary>
    public sealed record WildcardGroupStandings(
        int QualificationGroup,
        int GroupSize,
        IReadOnlyList<(string Team, int Rank, int TeamScoreThousandths, int TeamBaseThousandths)> Teams);

    /// <summary>
    /// Wildcard finalist selection outcome: the 32 finalists plus per-group
    /// provenance (guaranteed teams, candidates, wildcard winners) and whether
    /// a seeded tie draw was consumed.
    /// </summary>
    public sealed record WildcardSelection(
        IReadOnlyList<string> Finalists,
        IReadOnlyDictionary<int, IReadOnlyList<string>> GuaranteedByGroup,
        IReadOnlyList<TypeCupTournamentFormat.WildcardCandidate> Candidates,
        IReadOnlyList<TypeCupTournamentFormat.WildcardCandidate> WildcardWinners,
        bool TieDrawConsumed);

    /// <summary>
    /// Selects the Final field under the v2 wildcard policy (MSS-071): top
    /// <c>guaranteed</c> teams per group qualify directly; one next-ranked
    /// candidate per group (rank <c>guaranteed + 1</c>, or rank 1 when
    /// guaranteed is zero) competes for the global wildcard places ranked by
    /// <see cref="TypeCupTournamentFormat.CompareWildcardCandidates"/> with
    /// <see cref="TypeCupTournamentFormat.ResolveWildcards"/> tie handling.
    /// The single authoritative resolver for one-shot and step-by-step runners:
    /// same standings plus same RNG state always give the same finalists and
    /// RNG-after. When <c>wildcardCount</c> is zero this reduces to fixed
    /// 16/16-style quotas with no RNG use.
    /// </summary>
    public static (WildcardSelection Selection, SimulationKernel.Random.Pcg32State RngAfter) SelectWildcardFinalists(
        Plan plan,
        IReadOnlyDictionary<int, WildcardGroupStandings> qualStandings,
        SimulationKernel.Random.Pcg32V1 rng,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(qualStandings);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        EnsureWildcardPlan(plan);
        int guaranteed = rules.TypeCupFinalTeamCount / plan.QualificationGroupCount;
        int wildcardCount = plan.WildcardCount;
        Dictionary<int, IReadOnlyList<string>> guaranteedByGroup = new();
        List<TypeCupTournamentFormat.WildcardCandidate> candidates = new(plan.QualificationGroupCount);
        List<string> finalists = new(rules.TypeCupFinalTeamCount);

        foreach (QualificationStage stage in plan.QualificationStages.OrderBy(s => s.QualificationGroup))
        {
            CollectWildcardGroup(plan, qualStandings, stage, guaranteed, wildcardCount, guaranteedByGroup, candidates, finalists);
        }

        TypeCupTournamentFormat.WildcardResolution resolution =
            TypeCupTournamentFormat.ResolveWildcards(candidates, wildcardCount, rng, rules);
        SimulationKernel.Random.Pcg32State after = rng.Snapshot();
        foreach (TypeCupTournamentFormat.WildcardCandidate winner in resolution.Winners)
        {
            finalists.Add(winner.Team);
        }

        ValidateWildcardFinalists(finalists, rules);
        WildcardSelection selection = new(
            finalists.OrderBy(t => t, StringComparer.Ordinal).ToList(),
            guaranteedByGroup,
            candidates,
            resolution.Winners,
            resolution.TieDrawConsumed);
        return (selection, after);
    }

    private static void EnsureWildcardPlan(Plan plan)
    {
        if (!plan.IsTournament)
        {
            throw new InvalidOperationException("Finalist selection requires a qualification tournament.");
        }

        if (!plan.IsWildcardPolicy)
        {
            throw new InvalidOperationException(
                $"Wildcard selection requires policy v{RulesV1.WildcardTypeCupTournamentFormatVersion}, was v{plan.QualificationPolicyVersion}.");
        }
    }

    private static void CollectWildcardGroup(
        Plan plan,
        IReadOnlyDictionary<int, WildcardGroupStandings> qualStandings,
        QualificationStage stage,
        int guaranteed,
        int wildcardCount,
        Dictionary<int, IReadOnlyList<string>> guaranteedByGroup,
        List<TypeCupTournamentFormat.WildcardCandidate> candidates,
        List<string> finalists)
    {
        if (!qualStandings.TryGetValue(stage.QualificationGroup, out WildcardGroupStandings? group))
        {
            throw new InvalidOperationException(
                $"Type Cup qualification group {stage.QualificationGroup} has no completed standings.");
        }

        if (group.Teams.Count != stage.GroupSize)
        {
            throw new InvalidOperationException(
                $"Type Cup qualification group {stage.QualificationGroup} must hold exactly {stage.GroupSize} teams, was {group.Teams.Count}.");
        }

        List<(string Team, int Rank, int TeamScoreThousandths, int TeamBaseThousandths)> ordered =
            group.Teams.OrderBy(t => t.Rank).ToList();
        ValidateWildcardRanks(stage.QualificationGroup, ordered);
        List<string> guaranteedTeams = ordered
            .Take(guaranteed)
            .Select(t => t.Team)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        guaranteedByGroup[stage.QualificationGroup] = guaranteedTeams;
        finalists.AddRange(guaranteedTeams);

        if (wildcardCount > 0)
        {
            if (ordered.Count <= guaranteed)
            {
                throw new InvalidOperationException(
                    $"Type Cup qualification group {stage.QualificationGroup} has no wildcard candidate at rank {guaranteed + 1}.");
            }

            var candidate = ordered[guaranteed];
            candidates.Add(new TypeCupTournamentFormat.WildcardCandidate(
                stage.QualificationGroup,
                candidate.Team,
                candidate.Rank,
                candidate.TeamScoreThousandths,
                candidate.TeamBaseThousandths,
                stage.GroupSize));
        }

        _ = plan;
    }

    private static void ValidateWildcardRanks(
        int group,
        List<(string Team, int Rank, int TeamScoreThousandths, int TeamBaseThousandths)> ordered)
    {
        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Rank != i + 1)
            {
                throw new InvalidOperationException(
                    $"Type Cup qualification group {group} ranks are corrupt.");
            }
        }
    }

    private static void ValidateWildcardFinalists(List<string> finalists, RulesV1 rules)
    {
        if (finalists.Count != rules.TypeCupFinalTeamCount)
        {
            throw new InvalidOperationException(
                $"Type Cup Final must hold exactly {rules.TypeCupFinalTeamCount} teams, was {finalists.Count}.");
        }

        if (finalists.Distinct(StringComparer.Ordinal).Count() != rules.TypeCupFinalTeamCount)
        {
            throw new InvalidOperationException("Type Cup Finalists contain duplicate teams.");
        }
    }

    /// <summary>
    /// Builds wildcard group standings from simple rank-only standings plus a
    /// score lookup. Used when callers hold ranks separately from persisted
    /// team scores.
    /// </summary>
    public static IReadOnlyDictionary<int, WildcardGroupStandings> ToWildcardStandings(
        Plan plan,
        IReadOnlyDictionary<int, IReadOnlyList<(string Team, int Rank)>> ranks,
        IReadOnlyDictionary<(int Group, string Team), (int Score, int Base)> scores)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ranks);
        ArgumentNullException.ThrowIfNull(scores);
        Dictionary<int, WildcardGroupStandings> result = new();
        foreach (QualificationStage stage in plan.QualificationStages)
        {
            if (!ranks.TryGetValue(stage.QualificationGroup, out var groupRanks))
            {
                throw new InvalidOperationException(
                    $"Type Cup qualification group {stage.QualificationGroup} has no completed standings.");
            }

            List<(string Team, int Rank, int TeamScoreThousandths, int TeamBaseThousandths)> teams = new(groupRanks.Count);
            foreach ((string Team, int Rank) entry in groupRanks)
            {
                if (!scores.TryGetValue((stage.QualificationGroup, entry.Team), out var score))
                {
                    throw new InvalidOperationException(
                        $"Type Cup qualification group {stage.QualificationGroup} team '{entry.Team}' has no persisted score.");
                }

                teams.Add((entry.Team, entry.Rank, score.Score, score.Base));
            }

            result[stage.QualificationGroup] = new WildcardGroupStandings(stage.QualificationGroup, stage.GroupSize, teams);
        }

        return result;
    }
}
