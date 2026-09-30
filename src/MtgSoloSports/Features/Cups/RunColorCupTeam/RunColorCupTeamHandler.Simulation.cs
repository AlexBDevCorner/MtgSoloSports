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

namespace MtgSoloSports.Features.Cups.RunColorCupTeam;

public sealed partial class RunColorCupTeamHandler
{
    internal sealed record TeamSimulation(
        List<ColorCupTeamRoundPayloadDocument> Payloads,
        IReadOnlyList<TeamEvent.TeamLegRanked> Legs,
        IReadOnlyList<TeamEvent.TeamRanked> Teams,
        Pcg32State RngAfter,
        string Checksum);

    internal sealed record TeamState(
        RulesV1 Rules,
        SaveMetadataEntity Metadata,
        SeasonEntity Source,
        Dictionary<int, List<ColorCupSelectionEntity>> Groups,
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> Rosters,
        Dictionary<int, Bonus> ActiveBonuses,
        List<ColorCupTeamRoundPayloadDocument> Played,
        Pcg32State Rng,
        int StageCountBefore,
        int SeasonCountBefore,
        int RoundCountBefore,
        long LifetimeBefore,
        long EffectiveBefore,
        long ChampionshipBefore)
    {
        public PostseasonEvents.EventShape Shape => PostseasonEvents.Shape(PostseasonEvents.ColorCupTeam, Rules);
    }

    /// <summary>
    /// Plays exactly one group round. When it is the last round of a group other
    /// than the final group, the group's leg tie-break is drawn immediately so
    /// the returned RNG is where the next group starts in a one-shot run.
    /// </summary>
    internal static (ColorCupTeamRoundPayloadDocument Payload, Pcg32State RngAfterStep) PlayRound(
        TeamState state,
        IReadOnlyList<ColorCupTeamRoundPayloadDocument> played,
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
        ColorCupTeamRoundPayloadDocument payload = BuildGroupPayload(
            state.Groups[group], state.Source, state.Rules, group, round, rngBefore, simulation);
        ColorCupTeamInvariants.ValidateRound(payload, state.Rules, rngBefore.State, rngBefore.Stream);

        Pcg32State after = simulation.RngAfter;
        if (round == shape.RoundsPerGroup && group < shape.GroupCount)
        {
            List<ColorCupTeamRoundPayloadDocument> groupPayloads = [.. played.Where(p => p.GroupNumber == group), payload];
            after = RankGroupLeg(state, group, groupPayloads).RngAfter;
        }

        return (payload, after);
    }

    /// <summary>Ranks one completed group leg from its stored payloads with the group's tie-break RNG.</summary>
    internal static (IReadOnlyList<TeamEvent.TeamLegRanked> Legs, Pcg32State RngAfter) RankGroupLeg(
        TeamState state,
        int group,
        IReadOnlyList<ColorCupTeamRoundPayloadDocument> groupPayloads)
    {
        List<ColorCupSelectionEntity> members = state.Groups[group];
        List<ColorCupTeamRoundPayloadDocument> ordered = groupPayloads.OrderBy(p => p.RoundNumber).ToList();
        List<List<TeamEvent.TeamGroupRoundEntry>> groupRounds = ordered
            .Select(p => ToGroupEntries(p, members))
            .ToList();
        IReadOnlyList<TeamEvent.TeamLegTotals> totals = TeamEvent.AccumulateLeg(
            groupRounds, state.Rules.ColorCupColorCount, state.Rules.ColorCupTeamGroupRounds, state.Rules);
        ColorCupTeamRoundPayloadDocument last = ordered[^1];
        Pcg32V1 legRng = Pcg32V1.Restore(new Pcg32State(last.RngAfterState, last.RngAfterStream));
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked = TeamEvent.RankLeg(totals, legRng, state.Rules, state.Rules.ColorCupColorCount);
        ColorCupTeamInvariants.ValidateCompletedLeg(ranked, totals, group, state.Rules);
        return (ranked, legRng.Snapshot());
    }

    /// <summary>Completes the team event from all stored payloads (the source of truth).</summary>
    internal static TeamSimulation FinishTeam(TeamState state, List<ColorCupTeamRoundPayloadDocument> payloads)
    {
        PostseasonEvents.EventShape shape = state.Shape;
        List<TeamEvent.TeamLegRanked> allLegs = new(state.Rules.ColorCupColorCount * shape.GroupCount);
        Pcg32State current = default;
        for (int group = 1; group <= shape.GroupCount; group++)
        {
            List<ColorCupTeamRoundPayloadDocument> groupPayloads = payloads.Where(p => p.GroupNumber == group).ToList();
            (IReadOnlyList<TeamEvent.TeamLegRanked> legs, Pcg32State after) = RankGroupLeg(state, group, groupPayloads);
            allLegs.AddRange(legs);
            if (group < shape.GroupCount)
            {
                ColorCupTeamRoundPayloadDocument nextStart = payloads.Single(p => p.GroupNumber == group + 1 && p.RoundNumber == 1);
                if (after != new Pcg32State(nextStart.RngBeforeState, nextStart.RngBeforeStream))
                {
                    throw new InvalidOperationException($"Color Cup team RNG chain is broken at the start of group {group + 1}.");
                }
            }

            current = after;
        }

        IReadOnlyList<TeamEvent.TeamScoreInput> teamInputs = TeamEvent.BuildTeamInputs(allLegs, state.Rules.ColorCupColorCount);
        Pcg32V1 teamRng = Pcg32V1.Restore(current);
        IReadOnlyList<TeamEvent.TeamRanked> ranked = TeamEvent.RankTeams(teamInputs, teamRng);
        ColorCupTeamInvariants.ValidateTeams(ranked, allLegs, state.Rules);
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

    internal static ColorCupTeamRoundPayloadDocument BuildGroupPayload(
        List<ColorCupSelectionEntity> members,
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

        return new ColorCupTeamRoundPayloadDocument(
            ColorCupTeamRoundPayloadDocument.PayloadVersion,
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
        ColorCupTeamRoundPayloadDocument payload,
        List<ColorCupSelectionEntity> members)
    {
        Dictionary<int, ColorCupSelectionEntity> byAthlete = members.ToDictionary(m => m.SaveAthleteId);
        List<TeamEvent.TeamGroupRoundEntry> entries = new(payload.Placements.Count);
        foreach (RoundPayloadEntry placement in payload.Placements)
        {
            if (!byAthlete.TryGetValue(placement.AthleteId, out ColorCupSelectionEntity? selection))
            {
                throw new InvalidOperationException($"Color Cup team group {payload.GroupNumber} round contains athlete {placement.AthleteId} from the wrong rank group.");
            }

            string teamName = ((SimulationKernel.Catalog.SportingColor)selection.SportingColor).ToString();
            entries.Add(new TeamEvent.TeamGroupRoundEntry(
                placement.AthleteId,
                placement.Name,
                selection.SportingColor,
                teamName,
                placement.Position,
                placement.BaseThousandths,
                placement.FinalThousandths));
        }

        return entries;
    }

    /// <summary>
    /// Fingerprints team standings: lowercase hex SHA-256 over lines of
    /// <c>Rank:TeamId:TeamName:Score:Base</c> in rank order.
    /// SHA-256 is a content fingerprint here, not sporting randomness.
    /// </summary>
    public static string ComputeChecksum(IReadOnlyList<TeamEvent.TeamRanked> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (TeamEvent.TeamRanked entry in ranked)
        {
            builder.Append(entry.TeamRank.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.TeamId.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
