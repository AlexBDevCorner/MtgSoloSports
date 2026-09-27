using System.Security.Cryptography;
using System.Text;
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

    internal static TeamSimulation SimulateTeam(
        Dictionary<int, List<TypeCupSelectionEntity>> groups,
        Dictionary<int, List<AdvanceRoundHandler.MemberRow>> rosters,
        Dictionary<int, Bonus> activeBonuses,
        Pcg32State rngBefore,
        SeasonEntity source,
        RulesV1 rules,
        int teamCount)
    {
        List<TypeCupTeamRoundPayloadDocument> payloads = new(rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds);
        List<TeamEvent.TeamLegRanked> allLegs = new(teamCount * rules.TypeCupMinTeamSize);
        Pcg32State current = rngBefore;
        Dictionary<string, int> teamIds = BuildTeamIds(groups.Values.SelectMany(g => g).ToList());

        foreach (int groupNumber in Enumerable.Range(1, rules.TypeCupMinTeamSize).ToList())
        {
            current = SimulateSingleGroup(
                groups[groupNumber],
                rosters[groupNumber],
                activeBonuses,
                current,
                source,
                rules,
                groupNumber,
                teamCount,
                teamIds,
                payloads,
                allLegs);
        }

        IReadOnlyList<TeamEvent.TeamScoreInput> teamInputs = TeamEvent.BuildTeamInputs(allLegs, teamCount);
        Pcg32V1 teamRng = Pcg32V1.Restore(current);
        IReadOnlyList<TeamEvent.TeamRanked> ranked = TeamEvent.RankTeams(teamInputs, teamRng);
        TypeCupTeamInvariants.ValidateTeams(ranked, allLegs, rules, teamCount);
        Pcg32State rngAfter = teamRng.Snapshot();
        string checksum = ComputeChecksum(ranked);
        return new TeamSimulation(payloads, allLegs, ranked, rngAfter, checksum);
    }

    internal static Pcg32State SimulateSingleGroup(
        List<TypeCupSelectionEntity> members,
        List<AdvanceRoundHandler.MemberRow> roster,
        Dictionary<int, Bonus> activeBonuses,
        Pcg32State rngBefore,
        SeasonEntity source,
        RulesV1 rules,
        int groupNumber,
        int teamCount,
        Dictionary<string, int> teamIds,
        List<TypeCupTeamRoundPayloadDocument> payloads,
        List<TeamEvent.TeamLegRanked> allLegs)
    {
        Dictionary<int, Points> cumulative = new(roster.Count);
        List<List<TeamEvent.TeamGroupRoundEntry>> groupRounds = new(rules.TypeCupGroupRounds);
        Pcg32State current = rngBefore;

        for (int roundNumber = 1; roundNumber <= rules.TypeCupGroupRounds; roundNumber++)
        {
            RoundSimulationResult simulation = SimulateGroupRound(roster, cumulative, activeBonuses, current, rules);
            TypeCupTeamRoundPayloadDocument payload = BuildGroupPayload(
                members, source, rules, groupNumber, roundNumber, current, simulation);
            TypeCupTeamInvariants.ValidateRound(payload, rules, teamCount, current.State, current.Stream);
            payloads.Add(payload);
            groupRounds.Add(ToGroupEntries(payload, members, teamIds));
            foreach (RoundPayloadEntry entry in payload.Placements)
            {
                cumulative[entry.AthleteId] = Points.FromThousandths(entry.CumulativeAfterThousandths);
            }

            current = simulation.RngAfter;
        }

        IReadOnlyList<TeamEvent.TeamLegTotals> totals = TeamEvent.AccumulateLeg(
            groupRounds, teamCount, rules.TypeCupGroupRounds, rules);
        Pcg32V1 legRng = Pcg32V1.Restore(current);
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked = TeamEvent.RankLeg(totals, legRng, rules, teamCount);
        TypeCupTeamInvariants.ValidateCompletedLeg(ranked, totals, groupNumber, rules, teamCount);
        foreach (TeamEvent.TeamLegRanked leg in ranked)
        {
            allLegs.Add(leg);
        }

        return legRng.Snapshot();
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
