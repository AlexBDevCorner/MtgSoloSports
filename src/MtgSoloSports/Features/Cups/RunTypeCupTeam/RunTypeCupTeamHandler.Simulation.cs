using System.Security.Cryptography;
using System.Text;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

public sealed partial class RunTypeCupTeamHandler
{
    /// <summary>
    /// Tournament execution state: plan, per-stage fields, ordered played
    /// payloads, global active bonuses and the current save RNG (after the
    /// last persisted step including tie-breaks).
    /// </summary>
    internal sealed record TournamentState(
        RulesV1 Rules,
        SaveMetadataEntity Metadata,
        SeasonEntity Source,
        List<TypeCupSelectionEntity> Selection,
        int TeamCount,
        TypeCupTournamentPlan.Plan Plan,
        Dictionary<TypeCupTournamentPlan.StageKey, TypeCupStageField> Fields,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> PlayedOrdered,
        Dictionary<int, Bonus> ActiveBonuses,
        IReadOnlyList<TypeCupTournamentDrawEntity> Draws,
        Pcg32State Rng,
        int StageCountBefore,
        int SeasonCountBefore,
        int RoundCountBefore,
        long LifetimeBefore,
        long EffectiveBefore,
        long ChampionshipBefore)
    {
        /// <summary>Played payloads only, in global tournament order.</summary>
        public List<TypeCupTeamRoundPayloadDocument> Played => PlayedOrdered.Select(o => o.Payload).ToList();

        public int PlayedCount => PlayedOrdered.Count;
    }

    /// <summary>
    /// One stage's simulation (legs plus teams) with its RNG-after (after the
    /// stage's team ranking tie-break).
    /// </summary>
    internal sealed record StageSimulation(
        TypeCupTournamentPlan.StageKey Key,
        int FieldSize,
        IReadOnlyList<TeamEvent.TeamLegRanked> Legs,
        IReadOnlyList<TeamEvent.TeamRanked> Teams,
        Pcg32State RngAfter,
        string Checksum);

    internal sealed record TournamentSimulation(
        IReadOnlyList<StageSimulation> Stages,
        StageSimulation Final,
        Pcg32State RngAfter,
        string Checksum,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> Ordered);

    /// <summary>
    /// Legacy single-event simulation shape for callers that still need it.
    /// New code prefers <see cref="TournamentSimulation"/>.
    /// </summary>
    internal sealed record TeamSimulation(
        List<TypeCupTeamRoundPayloadDocument> Payloads,
        IReadOnlyList<TeamEvent.TeamLegRanked> Legs,
        IReadOnlyList<TeamEvent.TeamRanked> Teams,
        Pcg32State RngAfter,
        string Checksum);

    /// <summary>
    /// Legacy single-event state for callers that still need it.
    /// New code prefers <see cref="TournamentState"/>.
    /// </summary>
    internal sealed record TeamState(
        RulesV1 Rules,
        SaveMetadataEntity Metadata,
        SeasonEntity Source,
        Dictionary<int, List<TypeCupSelectionEntity>> Groups,
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> Rosters,
        Dictionary<int, Bonus> ActiveBonuses,
        List<TypeCupTeamRoundPayloadDocument> Played,
        List<TypeCupSelectionEntity> Selection,
        int TeamCount,
        Dictionary<string, int> TeamIds,
        Pcg32State Rng,
        int StageCountBefore,
        int SeasonCountBefore,
        int RoundCountBefore,
        long LifetimeBefore,
        long EffectiveBefore,
        long ChampionshipBefore)
    {
        public PostseasonEvents.EventShape Shape => PostseasonEvents.Shape(PostseasonEvents.TypeCupTeam, Rules);
    }

    /// <summary>
    /// Plays exactly the next tournament round in stage order. When it is the
    /// last round of a rank group 1..3, the group's leg tie-break is drawn
    /// immediately; when it is the last round of a qualification stage, that
    /// stage's final leg plus team ranking is drawn immediately so the next
    /// stage starts in the same place in one-shot and step-by-step runs.
    /// The tournament Final's team ranking is deferred to completion, matching
    /// the legacy single-event chain.
    /// </summary>
    internal static (TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload, Pcg32State RngAfterStep) PlayNextRound(
        TournamentState state,
        IReadOnlyList<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        Pcg32State rngBefore)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ordered);
        (TypeCupTournamentPlan.StageKey stage, int rankGroup, int round) =
            TypeCupTournamentPlan.Cursor(ordered.Count, state.Plan, state.Rules);
        if (!state.Fields.TryGetValue(stage, out TypeCupStageField? field))
        {
            // Final field for a tournament is built once qualifiers are known.
            // Step-by-step callers reach the Final only after every qual group
            // is complete with persisted standings, so the field must exist.
            throw new InvalidOperationException(
                $"Type Cup tournament stage phase {stage.Phase} qual {stage.QualificationGroup} has no field; qualification must complete first.");
        }

        List<AdvanceRoundHandler.MemberRow> roster = field.Rosters[rankGroup];
        Dictionary<int, Points> cumulative = new(roster.Count);
        if (round > 1)
        {
            var prev = ordered.Last(o => o.Key.Phase == stage.Phase
                && o.Key.QualificationGroup == stage.QualificationGroup
                && o.Payload.GroupNumber == rankGroup);
            foreach (RoundPayloadEntry entry in prev.Payload.Placements)
            {
                cumulative[entry.AthleteId] = Points.FromThousandths(entry.CumulativeAfterThousandths);
            }
        }

        RoundSimulationResult simulation = SimulateGroupRound(roster, cumulative, state.ActiveBonuses, rngBefore, state.Rules);
        // Stage selection rows for this rank group (for payload provenance).
        List<TypeCupSelectionEntity> members = field.Groups[rankGroup];
        TypeCupTeamRoundPayloadDocument payload = BuildGroupPayload(
            members, state.Source, state.Rules, rankGroup, round, rngBefore, simulation);
        TypeCupTeamInvariants.ValidateRound(payload, state.Rules, field.FieldSize, rngBefore.State, rngBefore.Stream);
        if (!state.Plan.IsLegacy)
        {
            TypeCupTournamentFormat.ValidateCompetitionFieldSize(field.FieldSize, state.Rules);
        }

        Pcg32State after = AdvanceAfterRound(state, stage, rankGroup, round, ordered, payload);
        return (stage, payload, after);
    }

    private static Pcg32State AdvanceAfterRound(
        TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        int rankGroup,
        int round,
        IReadOnlyList<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        TypeCupTeamRoundPayloadDocument payload)
    {
        Pcg32State after = new(payload.RngAfterState, payload.RngAfterStream);
        if (round == state.Rules.TypeCupGroupRounds && rankGroup < state.Rules.TypeCupMinTeamSize)
        {
            List<TypeCupTeamRoundPayloadDocument> groupPayloads = ordered
                .Where(o => o.Key.Phase == stage.Phase && o.Key.QualificationGroup == stage.QualificationGroup && o.Payload.GroupNumber == rankGroup)
                .Select(o => o.Payload)
                .Append(payload)
                .ToList();
            return RankStageGroupLeg(state, stage, rankGroup, groupPayloads).RngAfter;
        }

        if (IsQualStageFinalRound(state, stage, rankGroup, round))
        {
            List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> stageOrdered =
                ordered.Where(o => o.Key.Phase == stage.Phase && o.Key.QualificationGroup == stage.QualificationGroup).ToList();
            stageOrdered.Add((stage, payload));
            return RankStageTeams(state, stage, stageOrdered.Select(o => o.Payload).ToList()).RngAfter;
        }

        return after;
    }

    private static bool IsQualStageFinalRound(
        TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        int rankGroup,
        int round) =>
        round == state.Rules.TypeCupGroupRounds
            && rankGroup == state.Rules.TypeCupMinTeamSize
            && !IsTournamentFinalStage(state, stage);

    /// <summary>
    /// Legacy single-event round player kept for binary compatibility with
    /// stepwise callers compiled against the old shape. New code prefers
    /// <see cref="PlayNextRound"/>.
    /// </summary>
    internal static (TypeCupTeamRoundPayloadDocument Payload, Pcg32State RngAfterStep) PlayRound(
        TeamState state,
        IReadOnlyList<TypeCupTeamRoundPayloadDocument> played,
        Pcg32State rngBefore)
    {
        PostseasonEvents.EventShape shape = state.Shape;
        (int group, int round) = PostseasonEvents.Cursor(played.Count, shape);
        List<AdvanceRoundHandler.MemberRow> roster = state.Rosters[group];
        Dictionary<int, Points> cumulative = new(roster.Count);
        if (round > 1)
        {
            foreach (RoundPayloadEntry entry in played[^1].Placements)
            {
                cumulative[entry.AthleteId] = Points.FromThousandths(entry.CumulativeAfterThousandths);
            }
        }

        RoundSimulationResult simulation = SimulateGroupRound(roster, cumulative, state.ActiveBonuses, rngBefore, state.Rules);
        TypeCupTeamRoundPayloadDocument payload = BuildGroupPayload(
            state.Groups[group], state.Source, state.Rules, group, round, rngBefore, simulation);
        TypeCupTeamInvariants.ValidateRound(payload, state.Rules, state.TeamCount, rngBefore.State, rngBefore.Stream);

        Pcg32State after = simulation.RngAfter;
        if (round == shape.RoundsPerGroup && group < shape.GroupCount)
        {
            List<TypeCupTeamRoundPayloadDocument> groupPayloads = [.. played.Where(p => p.GroupNumber == group), payload];
            after = RankGroupLeg(state, group, groupPayloads).RngAfter;
        }

        return (payload, after);
    }

    /// <summary>Ranks one completed rank group within a tournament stage.</summary>
    internal static (IReadOnlyList<TeamEvent.TeamLegRanked> Legs, Pcg32State RngAfter) RankStageGroupLeg(
        TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        int rankGroup,
        IReadOnlyList<TypeCupTeamRoundPayloadDocument> groupPayloads)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Fields.TryGetValue(stage, out TypeCupStageField? field))
        {
            throw new InvalidOperationException($"Type Cup tournament stage phase {stage.Phase} qual {stage.QualificationGroup} has no field.");
        }

        List<TypeCupSelectionEntity> members = field.Groups[rankGroup];
        List<TypeCupTeamRoundPayloadDocument> ordered = groupPayloads.OrderBy(p => p.RoundNumber).ToList();
        List<List<TeamEvent.TeamGroupRoundEntry>> groupRounds = ordered
            .Select(p => ToGroupEntries(p, members, field.TeamIds))
            .ToList();
        IReadOnlyList<TeamEvent.TeamLegTotals> totals = TeamEvent.AccumulateLeg(
            groupRounds, field.FieldSize, state.Rules.TypeCupGroupRounds, state.Rules);
        TypeCupTeamRoundPayloadDocument last = ordered[^1];
        Pcg32V1 legRng = Pcg32V1.Restore(new Pcg32State(last.RngAfterState, last.RngAfterStream));
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked = TeamEvent.RankLeg(totals, legRng, state.Rules, field.FieldSize);
        TypeCupTeamInvariants.ValidateCompletedLeg(ranked, totals, rankGroup, state.Rules, field.FieldSize);
        return (ranked, legRng.Snapshot());
    }

    /// <summary>Ranks one completed tournament stage (all four rank groups).</summary>
    internal static (IReadOnlyList<TeamEvent.TeamLegRanked> Legs, IReadOnlyList<TeamEvent.TeamRanked> Teams, Pcg32State RngAfter) RankStage(
        TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        IReadOnlyList<TypeCupTeamRoundPayloadDocument> stagePayloads)
    {
        if (!state.Fields.TryGetValue(stage, out TypeCupStageField? field))
        {
            throw new InvalidOperationException($"Type Cup tournament stage phase {stage.Phase} qual {stage.QualificationGroup} has no field.");
        }

        List<TeamEvent.TeamLegRanked> allLegs = new(field.FieldSize * state.Rules.TypeCupMinTeamSize);
        Pcg32State current = default;
        for (int group = 1; group <= state.Rules.TypeCupMinTeamSize; group++)
        {
            List<TypeCupTeamRoundPayloadDocument> groupPayloads = stagePayloads.Where(p => p.GroupNumber == group).ToList();
            (IReadOnlyList<TeamEvent.TeamLegRanked> legs, Pcg32State after) = RankStageGroupLeg(state, stage, group, groupPayloads);
            allLegs.AddRange(legs);
            if (group < state.Rules.TypeCupMinTeamSize)
            {
                TypeCupTeamRoundPayloadDocument nextStart = stagePayloads.Single(p => p.GroupNumber == group + 1 && p.RoundNumber == 1);
                if (after != new Pcg32State(nextStart.RngBeforeState, nextStart.RngBeforeStream))
                {
                    throw new InvalidOperationException($"Type Cup tournament RNG chain is broken at the start of group {group + 1} in phase {stage.Phase} qual {stage.QualificationGroup}.");
                }
            }

            current = after;
        }

        IReadOnlyList<TeamEvent.TeamScoreInput> teamInputs = TeamEvent.BuildTeamInputs(allLegs, field.FieldSize);
        Pcg32V1 teamRng = Pcg32V1.Restore(current);
        IReadOnlyList<TeamEvent.TeamRanked> ranked = TeamEvent.RankTeams(teamInputs, teamRng);
        TypeCupTeamInvariants.ValidateTeams(ranked, allLegs, state.Rules, field.FieldSize);
        return (allLegs, ranked, teamRng.Snapshot());
    }

    /// <summary>
    /// Ranks a stage's teams from its payloads, threading RNG from its final
    /// rank-group leg tie-break. Used for RNG-chain validation at stage
    /// boundaries (previous stage's team ranking after starts the next stage).
    /// </summary>
    internal static (IReadOnlyList<TeamEvent.TeamRanked> Teams, Pcg32State RngAfter) RankStageTeams(
        TournamentState state,
        TypeCupTournamentPlan.StageKey stage)
    {
        List<TypeCupTeamRoundPayloadDocument> stagePayloads = state.PlayedOrdered
            .Where(o => o.Key.Phase == stage.Phase && o.Key.QualificationGroup == stage.QualificationGroup)
            .Select(o => o.Payload)
            .ToList();
        return RankStageTeams(state, stage, stagePayloads);
    }

    internal static (IReadOnlyList<TeamEvent.TeamRanked> Teams, Pcg32State RngAfter) RankStageTeams(
        TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        IReadOnlyList<TypeCupTeamRoundPayloadDocument> stagePayloads)
    {
        (IReadOnlyList<TeamEvent.TeamLegRanked> legs, IReadOnlyList<TeamEvent.TeamRanked> teams, Pcg32State after) =
            RankStage(state, stage, stagePayloads);
        _ = legs;
        return (teams, after);
    }

    /// <summary>Ranks one completed group leg from its stored payloads (legacy).</summary>
    internal static (IReadOnlyList<TeamEvent.TeamLegRanked> Legs, Pcg32State RngAfter) RankGroupLeg(
        TeamState state,
        int group,
        IReadOnlyList<TypeCupTeamRoundPayloadDocument> groupPayloads)
    {
        List<TypeCupSelectionEntity> members = state.Groups[group];
        List<TypeCupTeamRoundPayloadDocument> ordered = groupPayloads.OrderBy(p => p.RoundNumber).ToList();
        List<List<TeamEvent.TeamGroupRoundEntry>> groupRounds = ordered
            .Select(p => ToGroupEntries(p, members, state.TeamIds))
            .ToList();
        IReadOnlyList<TeamEvent.TeamLegTotals> totals = TeamEvent.AccumulateLeg(
            groupRounds, state.TeamCount, state.Rules.TypeCupGroupRounds, state.Rules);
        TypeCupTeamRoundPayloadDocument last = ordered[^1];
        Pcg32V1 legRng = Pcg32V1.Restore(new Pcg32State(last.RngAfterState, last.RngAfterStream));
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked = TeamEvent.RankLeg(totals, legRng, state.Rules, state.TeamCount);
        TypeCupTeamInvariants.ValidateCompletedLeg(ranked, totals, group, state.Rules, state.TeamCount);
        return (ranked, legRng.Snapshot());
    }

    /// <summary>
    /// Completes the tournament from all ordered payloads (the source of truth).
    /// Each stage is ranked independently; qualification scores never carry to
    /// the Final (each stage accumulates only its own 32 rounds). The Final
    /// field for a tournament is derived from completed qualification standings
    /// (v1 fixed quotas, v2 guaranteed plus performance wildcards) when not
    /// already known from persisted standings.
    /// </summary>
    internal static TournamentSimulation FinishTournament(
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ordered);
        if (ordered.Count != state.Plan.TotalRounds)
        {
            throw new InvalidOperationException(
                $"Type Cup tournament requires exactly {state.Plan.TotalRounds} rounds, was {ordered.Count}.");
        }

        EnsureFinalField(state, ordered);
        List<StageSimulation> stages = RankAllStages(state, ordered);
        StageSimulation final = stages[^1];
        Pcg32State rngAfter = final.RngAfter;
        return new TournamentSimulation(stages, final, rngAfter, final.Checksum, ordered);
    }

    private static List<StageSimulation> RankAllStages(
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered)
    {
        List<StageSimulation> stages = new();
        Pcg32State current = default;
        bool first = true;
        TypeCupTournamentPlan.StageKey? previous = null;
        foreach (TypeCupTournamentPlan.StageKey stage in TypeCupTournamentPlan.StageSequence(state.Plan))
        {
            List<TypeCupTeamRoundPayloadDocument> stagePayloads = StagePayloads(ordered, stage);
            ValidateStagePayloadCount(state, stage, stagePayloads);
            (IReadOnlyList<TeamEvent.TeamLegRanked> legs, IReadOnlyList<TeamEvent.TeamRanked> teams, Pcg32State after) =
                RankStage(state, stage, stagePayloads);
            ValidateStageContinuity(state, ordered, stage, previous, stagePayloads, current, first);
            first = false;
            previous = stage;
            current = after;
            stages.Add(new StageSimulation(stage, state.Fields[stage].FieldSize, legs, teams, after, ComputeChecksum(teams)));
        }

        return stages;
    }

    private static List<TypeCupTeamRoundPayloadDocument> StagePayloads(
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        TypeCupTournamentPlan.StageKey stage) =>
        ordered
            .Where(o => o.Key.Phase == stage.Phase && o.Key.QualificationGroup == stage.QualificationGroup)
            .Select(o => o.Payload)
            .ToList();

    private static void ValidateStagePayloadCount(
        TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        List<TypeCupTeamRoundPayloadDocument> stagePayloads)
    {
        int perStage = state.Rules.TypeCupMinTeamSize * state.Rules.TypeCupGroupRounds;
        if (stagePayloads.Count != perStage)
        {
            throw new InvalidOperationException(
                $"Type Cup tournament stage phase {stage.Phase} qual {stage.QualificationGroup} must hold exactly {perStage} rounds.");
        }
    }

    private static void ValidateStageContinuity(
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        TypeCupTournamentPlan.StageKey stage,
        TypeCupTournamentPlan.StageKey? previous,
        List<TypeCupTeamRoundPayloadDocument> stagePayloads,
        Pcg32State current,
        bool first)
    {
        if (first)
        {
            return;
        }

        TypeCupTeamRoundPayloadDocument firstPayload = stagePayloads
            .OrderBy(p => p.GroupNumber).ThenBy(p => p.RoundNumber).First();
        Pcg32State expectedBefore = current;
        if (previous is not null && IsWildcardFinalStart(state, previous, stage))
        {
            expectedBefore = WildcardAfterFromOrderedPayloads(state, ordered, current);
        }

        if (expectedBefore != new Pcg32State(firstPayload.RngBeforeState, firstPayload.RngBeforeStream))
        {
            throw new InvalidOperationException(
                $"Type Cup tournament RNG chain is broken at the start of phase {stage.Phase} qual {stage.QualificationGroup}.");
        }
    }

    internal static Pcg32State WildcardAfterFromOrderedPayloads(
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        Pcg32State rngBeforeWildcard)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ordered);
        if (!state.Plan.IsTournament || !state.Plan.IsWildcardPolicy || state.Plan.WildcardCount == 0)
        {
            return rngBeforeWildcard;
        }

        Dictionary<int, TypeCupTournamentPlan.WildcardGroupStandings> byGroup = new();
        foreach (TypeCupTournamentPlan.QualificationStage stage in state.Plan.QualificationStages)
        {
            TypeCupTournamentPlan.StageKey key = TypeCupTournamentPlan.QualificationKey(stage.QualificationGroup);
            List<TypeCupTeamRoundPayloadDocument> stagePayloads = ordered
                .Where(o => o.Key.Phase == key.Phase && o.Key.QualificationGroup == key.QualificationGroup)
                .Select(o => o.Payload)
                .ToList();
            (IReadOnlyList<TeamEvent.TeamLegRanked> _, IReadOnlyList<TeamEvent.TeamRanked> teams, Pcg32State _) =
                RankStage(state, key, stagePayloads);
            List<(string Team, int Rank, int TeamScoreThousandths, int TeamBaseThousandths)> list =
                teams.Select(t => (t.TeamName, t.TeamRank, t.TeamScoreThousandths, t.TeamBaseThousandths)).ToList();
            byGroup[stage.QualificationGroup] = new TypeCupTournamentPlan.WildcardGroupStandings(
                stage.QualificationGroup, stage.GroupSize, list);
        }

        Pcg32V1 rng = Pcg32V1.Restore(rngBeforeWildcard);
        (_, Pcg32State after) =
            TypeCupTournamentPlan.SelectWildcardFinalists(state.Plan, byGroup, rng, state.Rules);
        return after;
    }

    internal static void EnsureFinalField(
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered)
    {
        if (!state.Plan.IsTournament)
        {
            return;
        }

        TypeCupTournamentPlan.StageKey finalKey = TypeCupTournamentPlan.FinalKey();
        if (state.Fields.ContainsKey(finalKey))
        {
            return;
        }

        if (!state.Plan.IsWildcardPolicy)
        {
            EnsureLegacyFinalField(state, ordered, finalKey);
            return;
        }

        EnsureWildcardFinalField(state, ordered, finalKey);
    }

    private static void EnsureLegacyFinalField(
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        TypeCupTournamentPlan.StageKey finalKey)
    {
        Dictionary<int, IReadOnlyList<(string Team, int Rank)>> qualStandings = new();
        foreach (TypeCupTournamentPlan.QualificationStage stage in state.Plan.QualificationStages)
        {
            TypeCupTournamentPlan.StageKey key = TypeCupTournamentPlan.QualificationKey(stage.QualificationGroup);
            List<TypeCupTeamRoundPayloadDocument> stagePayloads = ordered
                .Where(o => o.Key.Phase == key.Phase && o.Key.QualificationGroup == key.QualificationGroup)
                .Select(o => o.Payload)
                .ToList();
            (IReadOnlyList<TeamEvent.TeamLegRanked> _, IReadOnlyList<TeamEvent.TeamRanked> teams, Pcg32State _) =
                RankStage(state, key, stagePayloads);
            qualStandings[stage.QualificationGroup] = teams.Select(t => (t.TeamName, t.TeamRank)).ToList();
        }

        IReadOnlyList<string> legacyFinalists = TypeCupTournamentPlan.SelectFinalists(state.Plan, qualStandings);
        state.Fields[finalKey] = BuildFinalField(state, legacyFinalists);
    }

    private static void EnsureWildcardFinalField(
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        TypeCupTournamentPlan.StageKey finalKey)
    {
        Dictionary<int, TypeCupTournamentPlan.WildcardGroupStandings> wildcardByGroup = BuildWildcardGroups(state, ordered);
        Pcg32State before = WildcardRngBeforeFromOrdered(state, ordered);
        Pcg32V1 rng = Pcg32V1.Restore(before);
        (TypeCupTournamentPlan.WildcardSelection selection, Pcg32State _) =
            TypeCupTournamentPlan.SelectWildcardFinalists(state.Plan, wildcardByGroup, rng, state.Rules);
        state.Fields[finalKey] = BuildFinalField(state, selection.Finalists);
    }

    private static Dictionary<int, TypeCupTournamentPlan.WildcardGroupStandings> BuildWildcardGroups(
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered)
    {
        Dictionary<int, TypeCupTournamentPlan.WildcardGroupStandings> wildcardByGroup = new();
        foreach (TypeCupTournamentPlan.QualificationStage stage in state.Plan.QualificationStages)
        {
            TypeCupTournamentPlan.StageKey key = TypeCupTournamentPlan.QualificationKey(stage.QualificationGroup);
            List<TypeCupTeamRoundPayloadDocument> stagePayloads = ordered
                .Where(o => o.Key.Phase == key.Phase && o.Key.QualificationGroup == key.QualificationGroup)
                .Select(o => o.Payload)
                .ToList();
            (IReadOnlyList<TeamEvent.TeamLegRanked> _, IReadOnlyList<TeamEvent.TeamRanked> teams, Pcg32State _) =
                RankStage(state, key, stagePayloads);
            List<(string Team, int Rank, int TeamScoreThousandths, int TeamBaseThousandths)> list =
                teams.Select(t => (t.TeamName, t.TeamRank, t.TeamScoreThousandths, t.TeamBaseThousandths)).ToList();
            wildcardByGroup[stage.QualificationGroup] = new TypeCupTournamentPlan.WildcardGroupStandings(
                stage.QualificationGroup, stage.GroupSize, list);
        }

        return wildcardByGroup;
    }

    internal static Pcg32State WildcardRngBeforeFromOrdered(
        TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered)
    {
        // Best-effort reconstruction of the RNG state after the last qual ranking
        // from ordered payloads: rank each qual stage to its team-ranking after.
        // Used only for field building without threading; simulation paths thread
        // the authoritative current RNG instead.
        Pcg32State? lastAfter = null;
        foreach (TypeCupTournamentPlan.StageKey stage in TypeCupTournamentPlan.StageSequence(state.Plan))
        {
            if (stage.Phase != (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
            {
                continue;
            }

            List<TypeCupTeamRoundPayloadDocument> stagePayloads = ordered
                .Where(o => o.Key.Phase == stage.Phase && o.Key.QualificationGroup == stage.QualificationGroup)
                .Select(o => o.Payload)
                .ToList();
            if (stagePayloads.Count != state.Rules.TypeCupMinTeamSize * state.Rules.TypeCupGroupRounds)
            {
                continue;
            }

            (_, _, Pcg32State after) = RankStage(state, stage, stagePayloads);
            lastAfter = after;
        }

        return lastAfter ?? state.Rng;
    }

    internal static TypeCupStageField BuildFinalField(TournamentState state, IReadOnlyList<string> finalists)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(finalists);
        if (finalists.Count != state.Rules.TypeCupFinalTeamCount)
        {
            throw new InvalidOperationException(
                $"Type Cup Final must hold exactly {state.Rules.TypeCupFinalTeamCount} teams, was {finalists.Count}.");
        }

        HashSet<string> finalSet = new(finalists, StringComparer.Ordinal);
        List<TypeCupSelectionEntity> finalSelection = state.Selection
            .Where(e => finalSet.Contains(e.CreatureType))
            .ToList();
        if (finalSelection.Count != finalists.Count * state.Rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException("Type Cup Final selection does not cover exactly four athletes per finalist.");
        }

        Dictionary<int, List<TypeCupSelectionEntity>> groups = new(state.Rules.TypeCupMinTeamSize);
        foreach (int rank in Enumerable.Range(1, state.Rules.TypeCupMinTeamSize))
        {
            groups[rank] = finalSelection.Where(e => e.SelectionRank == rank).ToList();
            if (groups[rank].Count != finalists.Count)
            {
                throw new InvalidOperationException($"Type Cup Final rank group {rank} must hold exactly {finalists.Count} athletes.");
            }
        }

        TypeCupTeamInvariants.ValidateGroups(groups, state.Rules, finalists.Count);
        // Rosters need athlete names; reuse the global selection lookup via a
        // temporary groups map built from the full selection athletes.
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = BuildFinalRosters(state, groups);
        return new TypeCupStageField(
            TypeCupTournamentPlan.FinalKey(),
            finalists.Count,
            groups,
            rosters,
            BuildTeamIds(finalSelection),
            finalists.OrderBy(t => t, StringComparer.Ordinal).ToList());
    }

    internal static Dictionary<int, List<AdvanceRoundHandler.MemberRow>> BuildFinalRosters(
        TournamentState state,
        Dictionary<int, List<TypeCupSelectionEntity>> groups)
    {
        // Active bonuses already cover every selected athlete; rosters need only
        // names, resolved here from the stage selection ids via a fresh query.
        // Callers hold an open context; reuse the shared roster builder through
        // a minimal in-memory path (names come from the global rosters).
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters = new(groups.Count);
        foreach ((int rank, List<TypeCupSelectionEntity> members) in groups)
        {
            List<AdvanceRoundHandler.MemberRow> roster = new(members.Count);
            foreach (TypeCupSelectionEntity row in members)
            {
                string name = ResolveAthleteName(state, row.SaveAthleteId);
                roster.Add(new AdvanceRoundHandler.MemberRow(row.SaveAthleteId, name, 0));
            }

            roster.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            rosters[rank] = roster;
        }

        return rosters;
    }

    internal static string ResolveAthleteName(TournamentState state, int athleteId)
    {
        // Names are stable per athlete; find the member row already built for
        // qualification stages (every finalist appeared in exactly one qual).
        foreach (TypeCupStageField field in state.Fields.Values)
        {
            foreach (List<AdvanceRoundHandler.MemberRow> roster in field.Rosters.Values)
            {
                AdvanceRoundHandler.MemberRow? found = roster.FirstOrDefault(r => r.AthleteId == athleteId);
                if (found is not null)
                {
                    return found.Name;
                }
            }
        }

        throw new InvalidOperationException($"Type Cup Final references unknown athlete {athleteId}.");
    }

    /// <summary>Completes the team event from all stored payloads (legacy).</summary>
    internal static TeamSimulation FinishTeam(TournamentState state, List<TypeCupTeamRoundPayloadDocument> payloads)
    {
        // Legacy path kept for single-stage callers: wrap the single stage.
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered =
            payloads.Select((p, i) => (TypeCupTournamentPlan.StageSequence(state.Plan)[0], p)).ToList();
        TournamentSimulation tournament = FinishTournament(state, ordered);
        StageSimulation single = tournament.Final;
        return new TeamSimulation(payloads, single.Legs, single.Teams, tournament.RngAfter, tournament.Checksum);
    }

    internal static TeamSimulation FinishTeam(TeamState state, List<TypeCupTeamRoundPayloadDocument> payloads)
    {
        PostseasonEvents.EventShape shape = state.Shape;
        List<TeamEvent.TeamLegRanked> allLegs = new(state.TeamCount * shape.GroupCount);
        Pcg32State current = default;
        for (int group = 1; group <= shape.GroupCount; group++)
        {
            List<TypeCupTeamRoundPayloadDocument> groupPayloads = payloads.Where(p => p.GroupNumber == group).ToList();
            (IReadOnlyList<TeamEvent.TeamLegRanked> legs, Pcg32State after) = RankGroupLeg(state, group, groupPayloads);
            allLegs.AddRange(legs);
            if (group < shape.GroupCount)
            {
                TypeCupTeamRoundPayloadDocument nextStart = payloads.Single(p => p.GroupNumber == group + 1 && p.RoundNumber == 1);
                if (after != new Pcg32State(nextStart.RngBeforeState, nextStart.RngBeforeStream))
                {
                    throw new InvalidOperationException($"Type Cup team RNG chain is broken at the start of group {group + 1}.");
                }
            }

            current = after;
        }

        IReadOnlyList<TeamEvent.TeamScoreInput> teamInputs = TeamEvent.BuildTeamInputs(allLegs, state.TeamCount);
        Pcg32V1 teamRng = Pcg32V1.Restore(current);
        IReadOnlyList<TeamEvent.TeamRanked> ranked = TeamEvent.RankTeams(teamInputs, teamRng);
        TypeCupTeamInvariants.ValidateTeams(ranked, allLegs, state.Rules, state.TeamCount);
        Pcg32State rngAfter = teamRng.Snapshot();
        string checksum = ComputeChecksum(ranked);
        return new TeamSimulation(payloads, allLegs, ranked, rngAfter, checksum);
    }

    internal static RoundSimulationResult SimulateGroupRound(
        List<AdvanceRoundHandler.MemberRow> roster,
        Dictionary<int, Points> cumulative,
        Dictionary<int, Bonus> activeBonuses,
        Pcg32State rngBefore,
        RulesV1 rules)
    {
        List<RoundAthleteInput> inputs = new(roster.Count);
        foreach (AdvanceRoundHandler.MemberRow row in roster)
        {
            Points before = cumulative.TryGetValue(row.AthleteId, out Points value) ? value : Points.Zero;
            if (!activeBonuses.TryGetValue(row.AthleteId, out Bonus bonus))
            {
                throw new InvalidOperationException($"Missing stage-start bonus for athlete '{row.Name}'.");
            }

            inputs.Add(new RoundAthleteInput(row.AthleteId, row.Name, bonus, before));
        }

        Pcg32V1 rng = Pcg32V1.Restore(rngBefore);
        return TeamEvent.SimulateGroupRound(inputs, rng, rules, roster.Count);
    }

    internal static TypeCupTeamRoundPayloadDocument BuildGroupPayload(
        List<TypeCupSelectionEntity> members,
        SeasonEntity source,
        RulesV1 rules,
        int groupNumber,
        int roundNumber,
        Pcg32State rngBefore,
        RoundSimulationResult simulation)
    {
        List<RoundPayloadEntry> entries = new(simulation.Placements.Count);
        foreach (RoundPlacement placement in simulation.Placements)
        {
            entries.Add(new RoundPayloadEntry(
                placement.AthleteId,
                placement.Name,
                placement.Position,
                placement.BasePoints.Thousandths,
                placement.ActiveBonus.Thousandths,
                placement.FinalPoints.Thousandths,
                placement.CumulativeBefore.Thousandths,
                placement.CumulativeAfter.Thousandths,
                placement.RankBefore,
                placement.RankAfter,
                placement.RankMovement));
        }

        return new TypeCupTeamRoundPayloadDocument(
            TypeCupTeamRoundPayloadDocument.PayloadVersion,
            rules.Version,
            source.SeasonNumber,
            groupNumber,
            roundNumber,
            rngBefore.State,
            rngBefore.Stream,
            simulation.RngAfter.State,
            simulation.RngAfter.Stream,
            simulation.Checksum,
            entries);
    }

    internal static List<TeamEvent.TeamGroupRoundEntry> ToGroupEntries(
        TypeCupTeamRoundPayloadDocument payload,
        List<TypeCupSelectionEntity> members,
        Dictionary<string, int> teamIds)
    {
        Dictionary<int, TypeCupSelectionEntity> byAthlete = members.ToDictionary(m => m.SaveAthleteId);
        List<TeamEvent.TeamGroupRoundEntry> entries = new(payload.Placements.Count);
        foreach (RoundPayloadEntry placement in payload.Placements)
        {
            if (!byAthlete.TryGetValue(placement.AthleteId, out TypeCupSelectionEntity? selection))
            {
                throw new InvalidOperationException($"Type Cup team group {payload.GroupNumber} round contains athlete {placement.AthleteId} from the wrong rank group.");
            }

            if (!teamIds.TryGetValue(selection.CreatureType, out int teamId))
            {
                throw new InvalidOperationException($"Type Cup team '{selection.CreatureType}' has no deterministic team id.");
            }

            entries.Add(new TeamEvent.TeamGroupRoundEntry(
                placement.AthleteId,
                placement.Name,
                teamId,
                selection.CreatureType,
                placement.Position,
                placement.BaseThousandths,
                placement.FinalThousandths));
        }

        return entries;
    }

    /// <summary>
    /// Fingerprints team standings: lowercase hex SHA-256 over lines of
    /// <c>Rank:CreatureType:Score:Base</c> in rank order. Creature types sort
    /// ordinally for deterministic team ids, so the numeric team id is omitted:
    /// the type name alone identifies the team. SHA-256 is a content fingerprint
    /// here, not sporting randomness.
    /// </summary>
    public static string ComputeChecksum(IReadOnlyList<TeamEvent.TeamRanked> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (TeamEvent.TeamRanked entry in ranked.OrderBy(e => e.TeamRank))
        {
            builder.Append(entry.TeamRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.TeamName);
            builder.Append(':');
            builder.Append(entry.TeamScoreThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.TeamBaseThousandths.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
