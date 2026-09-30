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
    internal sealed record TeamSimulation(
        List<TypeCupTeamRoundPayloadDocument> Payloads,
        IReadOnlyList<TeamEvent.TeamLegRanked> Legs,
        IReadOnlyList<TeamEvent.TeamRanked> Teams,
        Pcg32State RngAfter,
        string Checksum);

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
    /// Plays exactly one group round. When it is the last round of a group other
    /// than the final group, the group's leg tie-break is drawn immediately so
    /// the returned RNG is where the next group starts in a one-shot run.
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

    /// <summary>Ranks one completed group leg from its stored payloads with the group's tie-break RNG.</summary>
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

    /// <summary>Completes the team event from all stored payloads (the source of truth).</summary>
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
