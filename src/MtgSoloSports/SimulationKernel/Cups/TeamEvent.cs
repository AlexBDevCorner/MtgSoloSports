using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.TieBreaking;

namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Pure generic team-event scoring shared by the Color Cup team event
/// (Game Rules §14) and the Type Cup team event (Game Rules §15).
/// Teams are identified only by <c>TeamId</c>/<c>TeamName</c>; this kernel
/// knows nothing about sporting colors, creature types, selection ranks or
/// permanent nationality. Callers pre-partition athletes into groups (for
/// example selection ranks #1..#4) with N athletes per group, where N is the
/// dynamically varying team count. Scoring positions 1..32 use the snapshot
/// scoring table exactly; Type Cup positions beyond 32 score the table minimum
/// (1 point) via <see cref="ScoringCalculator.TypeCupBasePointsForPosition"/>,
/// so Color Cup (N=8) and 2–32-team Type Cup outputs are unchanged.
/// Group-size upper bounds are intentionally absent here: the Color Cup slice
/// enforces its genuine fixed 8-team field and the Type Cup slice enforces at
/// least two whole four-athlete teams, with no artificial maximum.
/// All arithmetic is checked fixed-point integers; the only randomness is the
/// caller-supplied <see cref="Pcg32V1"/>, consumed only for exactly tied
/// groups. No clock, GUID ordering, database ordering or ambient randomness.
/// </summary>
public static class TeamEvent
{
    /// <summary>
    /// One athlete's finishing position within one team-group round, including
    /// team identity so accumulation can verify an athlete never changes teams.
    /// </summary>
    public sealed record TeamGroupRoundEntry(
        int AthleteId,
        string Name,
        int TeamId,
        string TeamName,
        int Position,
        int BaseThousandths,
        int FinalThousandths);

    /// <summary>
    /// Accumulated leg totals for one athlete across all rounds of its group.
    /// <c>RoundPlaceCounts</c> has one entry per group position (index 0 counts
    /// round 1st places); entries sum to the group round count.
    /// </summary>
    public sealed record TeamLegTotals(
        int AthleteId,
        string Name,
        int TeamId,
        string TeamName,
        int LegScoreThousandths,
        int LegBaseThousandths,
        IReadOnlyList<int> RoundPlaceCounts)
    {
        public int RoundWins => RoundPlaceCounts.Count > 0 ? RoundPlaceCounts[0] : 0;
    }

    /// <summary>
    /// One athlete's final standing within its group. Cups generate no new
    /// career bonus and award no championship points, so neither value is
    /// reported here.
    /// </summary>
    public sealed record TeamLegRanked(
        int AthleteId,
        string Name,
        int TeamId,
        string TeamName,
        int LegRank,
        int LegScoreThousandths,
        int LegBaseThousandths,
        int RoundWins,
        IReadOnlyList<int> RoundPlaceCounts);

    /// <summary>
    /// One team's aggregated input for championship ranking. The team score is
    /// the sum of its legs' group scores. <c>GroupPlaceCounts</c> counts leg
    /// ranks best-downward (index 0 counts group wins) across the team's legs;
    /// <c>RoundPlaceCounts</c> sums round placements best-downward across all
    /// of the team's round appearances.
    /// </summary>
    public sealed record TeamScoreInput(
        int TeamId,
        string TeamName,
        int TotalScoreThousandths,
        int TotalBaseThousandths,
        IReadOnlyList<int> GroupPlaceCounts,
        IReadOnlyList<int> RoundPlaceCounts);

    /// <summary>
    /// One team's final championship standing.
    /// </summary>
    public sealed record TeamRanked(
        int TeamId,
        string TeamName,
        int TeamRank,
        int TeamScoreThousandths,
        int TeamBaseThousandths,
        int GroupWins,
        int RoundWins,
        IReadOnlyList<int> GroupPlaceCounts,
        IReadOnlyList<int> RoundPlaceCounts);

    /// <summary>
    /// Simulates one team-group round: deterministic shuffle with only the
    /// supplied RNG, base points from the snapshot scoring table by shuffled
    /// position (positions 1..32 use the table exactly; positions beyond 32
    /// score the table minimum of 1 point), final points with only the
    /// supplied active bonus. The input roster order is the shuffle input
    /// order; callers must sort deterministically (for example by name
    /// ordinal) before calling. No artificial upper bound is enforced here;
    /// slice invariants enforce genuine field rules (Color Cup exactly eight,
    /// Type Cup at least two teams).
    /// </summary>
    public static RoundSimulationResult SimulateGroupRound(
        IReadOnlyList<RoundAthleteInput> roster,
        Pcg32V1 rng,
        RulesV1 rules,
        int groupSize)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();
        if (groupSize < 2)
        {
            throw new InvalidOperationException($"Team group size must be at least 2, was {groupSize}.");
        }

        if (roster.Count != groupSize)
        {
            throw new InvalidOperationException($"Team group roster must contain exactly {groupSize} athletes, was {roster.Count}.");
        }

        ValidateGroupInputs(roster);
        Dictionary<int, int> rankBefore = ComputeGroupRanks(roster);
        List<RoundPlacement> unranked = ShuffleAndScoreGroup(roster, rng, rules, rankBefore, groupSize);
        List<RoundPlacement> ranked = ApplyGroupRanks(unranked);
        ValidateGroupResult(ranked, rules, groupSize);
        return new RoundSimulationResult(ranked, rng.Snapshot(), RoundSimulator.ComputeChecksum(ranked));
    }

    /// <summary>
    /// Accumulates per-athlete leg totals from per-round group finishing orders.
    /// Each round must cover positions 1..N exactly once with unique athletes;
    /// team identity must be stable for each athlete across rounds.
    /// </summary>
    public static IReadOnlyList<TeamLegTotals> AccumulateLeg(
        IReadOnlyList<IReadOnlyList<TeamGroupRoundEntry>> rounds,
        int expectedAthletes,
        int expectedRounds,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();
        if (expectedAthletes < 2)
        {
            throw new InvalidOperationException($"Team group must hold at least 2 athletes, was {expectedAthletes}.");
        }

        if (expectedRounds < 1)
        {
            throw new InvalidOperationException($"Team group must hold at least one round, was {expectedRounds}.");
        }

        if (rounds.Count != expectedRounds)
        {
            throw new InvalidOperationException($"Team group accumulation requires exactly {expectedRounds} rounds, was {rounds.Count}.");
        }

        Dictionary<int, LegAccumulator> accumulators = new(expectedAthletes);
        Dictionary<int, LegIdentity> identities = new(expectedAthletes);
        foreach (IReadOnlyList<TeamGroupRoundEntry> round in rounds)
        {
            ValidateGroupRound(round, rules, expectedAthletes, accumulators, identities);
        }

        if (accumulators.Count != expectedAthletes)
        {
            throw new InvalidOperationException($"Team group accumulation requires exactly {expectedAthletes} athletes, was {accumulators.Count}.");
        }

        List<TeamLegTotals> totals = new(accumulators.Count);
        foreach ((int athleteId, LegAccumulator accumulator) in accumulators)
        {
            if (accumulator.CountSum != expectedRounds)
            {
                throw new InvalidOperationException(
                    $"Athlete '{identities[athleteId].Name}' has {accumulator.CountSum} round appearances, expected {expectedRounds}.");
            }

            LegIdentity identity = identities[athleteId];
            totals.Add(new TeamLegTotals(
                athleteId,
                identity.Name,
                identity.TeamId,
                identity.TeamName,
                accumulator.LegScore,
                accumulator.LegBase,
                accumulator.Counts));
        }

        totals.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return totals;
    }

    /// <summary>
    /// Ranks accumulated leg totals best-first. Leg score is primary; ties break
    /// by round-place counts best-downward, then raw base totals, then a seeded
    /// draw that consumes <paramref name="rng"/> only for exactly tied groups.
    /// Groups are canonically ordered before shuffling so input enumeration
    /// order cannot affect the result.
    /// </summary>
    public static IReadOnlyList<TeamLegRanked> RankLeg(
        IReadOnlyList<TeamLegTotals> totals,
        Pcg32V1 rng,
        RulesV1 rules,
        int groupSize)
    {
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();
        if (groupSize < 2)
        {
            throw new InvalidOperationException($"Team group size must be at least 2, was {groupSize}.");
        }

        if (totals.Count != groupSize)
        {
            throw new InvalidOperationException($"Team leg ranking requires exactly {groupSize} athletes, was {totals.Count}.");
        }

        ValidateLegTotals(totals, rules, groupSize);
        List<TeamLegTotals> byScore = SortLegsByScore(totals);
        List<TeamLegRanked> ranked = new(byScore.Count);
        int index = 0;
        while (index < byScore.Count)
        {
            int runEnd = index + 1;
            while (runEnd < byScore.Count &&
                byScore[runEnd].LegScoreThousandths == byScore[index].LegScoreThousandths)
            {
                runEnd++;
            }

            List<TeamLegTotals> group = byScore.GetRange(index, runEnd - index);
            IReadOnlyList<TeamLegTotals> ordered = OrderTiedLegs(group, rng);
            foreach (TeamLegTotals entry in ordered)
            {
                ranked.Add(new TeamLegRanked(
                    entry.AthleteId,
                    entry.Name,
                    entry.TeamId,
                    entry.TeamName,
                    ranked.Count + 1,
                    entry.LegScoreThousandths,
                    entry.LegBaseThousandths,
                    entry.RoundWins,
                    entry.RoundPlaceCounts));
            }

            index = runEnd;
        }

        return ranked;
    }

    /// <summary>
    /// Aggregates leg standings into per-team championship inputs. Each team
    /// must field the same number of legs (four for the Color Cup); group and
    /// round place-count vectors share the group-size length so team tie-break
    /// vectors stay comparable across teams.
    /// </summary>
    public static IReadOnlyList<TeamScoreInput> BuildTeamInputs(
        IReadOnlyList<TeamLegRanked> legs,
        int groupSize)
    {
        ArgumentNullException.ThrowIfNull(legs);
        if (groupSize < 2)
        {
            throw new InvalidOperationException($"Team group size must be at least 2, was {groupSize}.");
        }

        if (legs.Count == 0 || legs.Count % groupSize != 0)
        {
            throw new InvalidOperationException($"Team legs must form whole groups of {groupSize}, was {legs.Count}.");
        }

        Dictionary<int, TeamAggregator> byTeam = new();
        Dictionary<int, string> teamNames = new();
        foreach (TeamLegRanked leg in legs)
        {
            ValidateLegForTeam(leg, groupSize);
            if (!teamNames.TryGetValue(leg.TeamId, out string? known))
            {
                teamNames[leg.TeamId] = leg.TeamName;
            }
            else if (!string.Equals(known, leg.TeamName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Team {leg.TeamId} has inconsistent names.");
            }

            if (!byTeam.TryGetValue(leg.TeamId, out TeamAggregator? aggregator))
            {
                aggregator = new TeamAggregator(groupSize);
                byTeam[leg.TeamId] = aggregator;
            }

            aggregator.Add(leg, groupSize);
        }

        int legsPerTeam = legs.Count / byTeam.Count;
        List<TeamScoreInput> inputs = new(byTeam.Count);
        foreach ((int teamId, TeamAggregator aggregator) in byTeam)
        {
            if (aggregator.LegCount != legsPerTeam)
            {
                throw new InvalidOperationException($"Team {teamId} fields {aggregator.LegCount} legs, expected {legsPerTeam}.");
            }

            inputs.Add(new TeamScoreInput(
                teamId,
                teamNames[teamId],
                aggregator.TotalScore,
                aggregator.TotalBase,
                aggregator.GroupPlaceCounts,
                aggregator.RoundPlaceCounts));
        }

        inputs.Sort(static (left, right) => string.Compare(left.TeamName, right.TeamName, StringComparison.Ordinal));
        return inputs;
    }

    /// <summary>
    /// Ranks teams best-first. The team score (sum of leg group scores) is
    /// primary; ties break by group-place counts best-downward, then aggregated
    /// round-place counts best-downward, then raw base totals, then a seeded
    /// draw that consumes <paramref name="rng"/> only for exactly tied groups.
    /// Teams are canonically ordered before shuffling so input enumeration
    /// order cannot affect the result.
    /// </summary>
    public static IReadOnlyList<TeamRanked> RankTeams(
        IReadOnlyList<TeamScoreInput> inputs,
        Pcg32V1 rng)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rng);
        if (inputs.Count == 0)
        {
            throw new InvalidOperationException("Team ranking requires at least one team.");
        }

        ValidateTeamInputs(inputs);
        List<TeamScoreInput> byScore = [.. inputs];
        byScore.Sort(static (left, right) =>
        {
            int score = right.TotalScoreThousandths.CompareTo(left.TotalScoreThousandths);
            return score != 0 ? score : string.Compare(left.TeamName, right.TeamName, StringComparison.Ordinal);
        });

        List<TeamRanked> ranked = new(byScore.Count);
        int index = 0;
        while (index < byScore.Count)
        {
            int runEnd = index + 1;
            while (runEnd < byScore.Count &&
                byScore[runEnd].TotalScoreThousandths == byScore[index].TotalScoreThousandths)
            {
                runEnd++;
            }

            List<TeamScoreInput> group = byScore.GetRange(index, runEnd - index);
            IReadOnlyList<TeamScoreInput> ordered = OrderTiedTeams(group, rng);
            foreach (TeamScoreInput entry in ordered)
            {
                ranked.Add(new TeamRanked(
                    entry.TeamId,
                    entry.TeamName,
                    ranked.Count + 1,
                    entry.TotalScoreThousandths,
                    entry.TotalBaseThousandths,
                    entry.GroupPlaceCounts.Count > 0 ? entry.GroupPlaceCounts[0] : 0,
                    entry.RoundPlaceCounts.Count > 0 ? entry.RoundPlaceCounts[0] : 0,
                    entry.GroupPlaceCounts,
                    entry.RoundPlaceCounts));
            }

            index = runEnd;
        }

        return ranked;
    }

    private sealed class LegAccumulator
    {
        public LegAccumulator(int groupSize)
        {
            Counts = new int[groupSize];
        }

        public int LegScore;

        public int LegBase;

        public int[] Counts;

        public int CountSum;
    }

    private sealed record LegIdentity(string Name, int TeamId, string TeamName);

    private sealed class TeamAggregator
    {
        public TeamAggregator(int groupSize)
        {
            GroupPlaceCounts = new int[groupSize];
            RoundPlaceCounts = new int[groupSize];
        }

        public int TotalScore;

        public int TotalBase;

        public int[] GroupPlaceCounts;

        public int[] RoundPlaceCounts;

        public int LegCount;

        public void Add(TeamLegRanked leg, int groupSize)
        {
            checked
            {
                TotalScore += leg.LegScoreThousandths;
                TotalBase += leg.LegBaseThousandths;
            }

            if (leg.LegRank < 1 || leg.LegRank > groupSize)
            {
                throw new InvalidOperationException($"Leg rank {leg.LegRank} is out of range for group size {groupSize}.");
            }

            GroupPlaceCounts[leg.LegRank - 1]++;
            for (int i = 0; i < groupSize; i++)
            {
                checked
                {
                    RoundPlaceCounts[i] += leg.RoundPlaceCounts[i];
                }
            }

            checked
            {
                LegCount++;
            }
        }
    }

    private static void ValidateGroupInputs(IReadOnlyList<RoundAthleteInput> roster)
    {
        HashSet<int> ids = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (RoundAthleteInput athlete in roster)
        {
            if (athlete.AthleteId <= 0)
            {
                throw new InvalidOperationException($"Team group roster contains invalid athlete id {athlete.AthleteId}.");
            }

            if (string.IsNullOrWhiteSpace(athlete.Name))
            {
                throw new InvalidOperationException("Team group roster contains an athlete with an empty name.");
            }

            if (!ids.Add(athlete.AthleteId))
            {
                throw new InvalidOperationException($"Team group roster contains duplicate athlete id {athlete.AthleteId}.");
            }

            if (!names.Add(athlete.Name))
            {
                throw new InvalidOperationException($"Team group roster contains duplicate athlete '{athlete.Name}'.");
            }
        }
    }

    private static Dictionary<int, int> ComputeGroupRanks(IReadOnlyList<RoundAthleteInput> roster)
    {
        List<RoundAthleteInput> ordered = new(roster);
        ordered.Sort(static (left, right) =>
        {
            int points = right.CumulativeBefore.Thousandths.CompareTo(left.CumulativeBefore.Thousandths);
            return points != 0 ? points : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });

        Dictionary<int, int> ranks = new(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            ranks[ordered[i].AthleteId] = i + 1;
        }

        return ranks;
    }

    private static List<RoundPlacement> ShuffleAndScoreGroup(
        IReadOnlyList<RoundAthleteInput> roster,
        Pcg32V1 rng,
        RulesV1 rules,
        Dictionary<int, int> rankBefore,
        int groupSize)
    {
        List<RoundAthleteInput> shuffled = new(roster);
        DeterministicShuffle.Shuffle(shuffled, rng);

        List<RoundPlacement> placements = new(shuffled.Count);
        for (int i = 0; i < shuffled.Count; i++)
        {
            int position = i + 1;
            if (position > groupSize)
            {
                throw new InvalidOperationException($"Team group position {position} exceeds group size {groupSize}.");
            }

            Points basePoints = ScoringCalculator.TypeCupBasePointsForPosition(position, rules);
            Points finalPoints = ScoringCalculator.ApplyBonus(basePoints, shuffled[i].ActiveBonus);
            Points cumulativeAfter = checked(shuffled[i].CumulativeBefore + finalPoints);
            placements.Add(new RoundPlacement(
                shuffled[i].AthleteId,
                shuffled[i].Name,
                position,
                basePoints,
                shuffled[i].ActiveBonus,
                finalPoints,
                shuffled[i].CumulativeBefore,
                cumulativeAfter,
                rankBefore[shuffled[i].AthleteId],
                0,
                0));
        }

        return placements;
    }

    private static List<RoundPlacement> ApplyGroupRanks(IReadOnlyList<RoundPlacement> placements)
    {
        List<RoundPlacement> ordered = new(placements);
        ordered.Sort(static (left, right) =>
        {
            int points = right.CumulativeAfter.Thousandths.CompareTo(left.CumulativeAfter.Thousandths);
            return points != 0 ? points : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });

        Dictionary<int, int> rankAfter = new(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            rankAfter[ordered[i].AthleteId] = i + 1;
        }

        List<RoundPlacement> ranked = new(placements.Count);
        foreach (RoundPlacement placement in placements)
        {
            int after = rankAfter[placement.AthleteId];
            ranked.Add(placement with
            {
                RankAfter = after,
                RankMovement = placement.RankBefore - after,
            });
        }

        return ranked;
    }

    private static void ValidateGroupResult(IReadOnlyList<RoundPlacement> placements, RulesV1 rules, int groupSize)
    {
        HashSet<int> positions = new();
        HashSet<int> ids = new();
        foreach (RoundPlacement placement in placements)
        {
            if (!positions.Add(placement.Position))
            {
                throw new InvalidOperationException($"Team group result contains duplicate position {placement.Position}.");
            }

            if (!ids.Add(placement.AthleteId))
            {
                throw new InvalidOperationException($"Team group result contains duplicate athlete id {placement.AthleteId}.");
            }

            Points expectedBase = ScoringCalculator.TypeCupBasePointsForPosition(placement.Position, rules);
            if (expectedBase.Thousandths != placement.BasePoints.Thousandths)
            {
                throw new InvalidOperationException($"Team group result base points for position {placement.Position} are corrupt.");
            }

            Points expectedFinal = ScoringCalculator.ApplyBonus(expectedBase, placement.ActiveBonus);
            if (expectedFinal.Thousandths != placement.FinalPoints.Thousandths)
            {
                throw new InvalidOperationException($"Team group result final points for '{placement.Name}' are corrupt.");
            }

            Points expectedAfter = checked(placement.CumulativeBefore + placement.FinalPoints);
            if (expectedAfter.Thousandths != placement.CumulativeAfter.Thousandths)
            {
                throw new InvalidOperationException($"Team group result cumulative total for '{placement.Name}' is corrupt.");
            }
        }

        if (positions.Count != groupSize || !positions.SetEquals(Enumerable.Range(1, groupSize)))
        {
            throw new InvalidOperationException($"Team group result must cover positions 1..{groupSize} exactly once.");
        }
    }

    private static void ValidateGroupRound(
        IReadOnlyList<TeamGroupRoundEntry> round,
        RulesV1 rules,
        int expectedAthletes,
        Dictionary<int, LegAccumulator> accumulators,
        Dictionary<int, LegIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Count != expectedAthletes)
        {
            throw new InvalidOperationException($"Team group round must contain exactly {expectedAthletes} entries, was {round.Count}.");
        }

        HashSet<int> positions = new();
        HashSet<int> athleteIds = new();
        foreach (TeamGroupRoundEntry entry in round)
        {
            ValidateGroupRoundEntry(entry, rules, expectedAthletes, positions, athleteIds);
            TrackGroupRoundEntry(entry, expectedAthletes, accumulators, identities);
        }

        if (!positions.SetEquals(Enumerable.Range(1, expectedAthletes)))
        {
            throw new InvalidOperationException($"Team group round must cover positions 1..{expectedAthletes} exactly once.");
        }
    }

    private static void ValidateGroupRoundEntry(
        TeamGroupRoundEntry entry,
        RulesV1 rules,
        int expectedAthletes,
        HashSet<int> positions,
        HashSet<int> athleteIds)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Team group round contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Team group round contains an athlete with an empty name.");
        }

        if (string.IsNullOrWhiteSpace(entry.TeamName))
        {
            throw new InvalidOperationException($"Team group round entry for '{entry.Name}' has an empty team name.");
        }

        if (entry.Position < 1 || entry.Position > expectedAthletes)
        {
            throw new InvalidOperationException($"Team group round position {entry.Position} is out of range.");
        }

        if (!positions.Add(entry.Position))
        {
            throw new InvalidOperationException($"Team group round contains duplicate position {entry.Position}.");
        }

        if (!athleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Team group round contains duplicate athlete id {entry.AthleteId}.");
        }

        if (entry.BaseThousandths < 0 || entry.FinalThousandths < 0)
        {
            throw new InvalidOperationException($"Team group round points for '{entry.Name}' cannot be negative.");
        }

        int expectedBase = ScoringCalculator.TypeCupBasePointsForPosition(entry.Position, rules).Thousandths;
        if (entry.BaseThousandths != expectedBase)
        {
            throw new InvalidOperationException(
                $"Team group round base points for position {entry.Position} must be {expectedBase}, was {entry.BaseThousandths}.");
        }
    }

    private static void TrackGroupRoundEntry(
        TeamGroupRoundEntry entry,
        int expectedAthletes,
        Dictionary<int, LegAccumulator> accumulators,
        Dictionary<int, LegIdentity> identities)
    {
        if (!identities.TryGetValue(entry.AthleteId, out LegIdentity? known))
        {
            identities[entry.AthleteId] = new LegIdentity(entry.Name, entry.TeamId, entry.TeamName);
        }
        else
        {
            if (!string.Equals(known.Name, entry.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Athlete id {entry.AthleteId} has inconsistent names.");
            }

            if (known.TeamId != entry.TeamId || !string.Equals(known.TeamName, entry.TeamName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Athlete '{entry.Name}' changes teams within one group.");
            }
        }

        if (!accumulators.TryGetValue(entry.AthleteId, out LegAccumulator? accumulator))
        {
            accumulator = new LegAccumulator(expectedAthletes);
            accumulators[entry.AthleteId] = accumulator;
        }

        checked
        {
            accumulator.LegScore += entry.FinalThousandths;
            accumulator.LegBase += entry.BaseThousandths;
        }

        accumulator.Counts[entry.Position - 1]++;
        accumulator.CountSum++;
    }

    private static void ValidateLegTotals(IReadOnlyList<TeamLegTotals> totals, RulesV1 rules, int groupSize)
    {
        HashSet<int> ids = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (TeamLegTotals entry in totals)
        {
            if (entry.AthleteId <= 0)
            {
                throw new InvalidOperationException($"Team leg totals contain invalid athlete id {entry.AthleteId}.");
            }

            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.TeamName))
            {
                throw new InvalidOperationException("Team leg totals contain an athlete or team with an empty name.");
            }

            if (!ids.Add(entry.AthleteId))
            {
                throw new InvalidOperationException($"Team leg totals contain duplicate athlete id {entry.AthleteId}.");
            }

            if (!names.Add(entry.Name))
            {
                throw new InvalidOperationException($"Team leg totals contain duplicate athlete '{entry.Name}'.");
            }

            if (entry.LegScoreThousandths < 0 || entry.LegBaseThousandths < 0)
            {
                throw new InvalidOperationException($"Team leg totals for '{entry.Name}' cannot be negative.");
            }

            if (entry.RoundPlaceCounts.Count != groupSize)
            {
                throw new InvalidOperationException($"Team leg totals for '{entry.Name}' must have {groupSize} round-place counts, was {entry.RoundPlaceCounts.Count}.");
            }

            ValidateLegCounts(entry, rules);
        }
    }

    private static void ValidateLegCounts(TeamLegTotals entry, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(rules);
        int sum = 0;
        foreach (int count in entry.RoundPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Team leg totals for '{entry.Name}' contain a negative placement count.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum == 0)
        {
            throw new InvalidOperationException($"Team leg totals for '{entry.Name}' sum to zero round appearances.");
        }
    }

    private static List<TeamLegTotals> SortLegsByScore(IReadOnlyList<TeamLegTotals> totals)
    {
        List<TeamLegTotals> byScore = [.. totals];
        byScore.Sort(static (left, right) =>
        {
            int score = right.LegScoreThousandths.CompareTo(left.LegScoreThousandths);
            return score != 0 ? score : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });
        return byScore;
    }

    private static IReadOnlyList<TeamLegTotals> OrderTiedLegs(List<TeamLegTotals> group, Pcg32V1 rng)
    {
        if (group.Count == 1)
        {
            return group;
        }

        IReadOnlyList<RankedEntry<TeamLegTotals>> ranked = TieBreaker.RankWithSeededDraw(
            group,
            entry => new DeterministicTieBreakKey(
                [],
                entry.RoundPlaceCounts,
                entry.LegBaseThousandths),
            entry => entry.Name,
            rng);
        return ranked.Select(r => r.Entry).ToList();
    }

    private static void ValidateLegForTeam(TeamLegRanked leg, int groupSize)
    {
        ArgumentNullException.ThrowIfNull(leg);
        if (leg.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Team leg contains invalid athlete id {leg.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(leg.Name) || string.IsNullOrWhiteSpace(leg.TeamName))
        {
            throw new InvalidOperationException("Team leg contains an empty athlete or team name.");
        }

        if (leg.LegRank < 1 || leg.LegRank > groupSize)
        {
            throw new InvalidOperationException($"Team leg rank {leg.LegRank} is out of range for group size {groupSize}.");
        }

        if (leg.LegScoreThousandths < 0 || leg.LegBaseThousandths < 0)
        {
            throw new InvalidOperationException($"Team leg score for '{leg.Name}' cannot be negative.");
        }

        if (leg.RoundPlaceCounts.Count != groupSize)
        {
            throw new InvalidOperationException($"Team leg counts for '{leg.Name}' must have {groupSize} entries.");
        }
    }

    private static void ValidateTeamInputs(IReadOnlyList<TeamScoreInput> inputs)
    {
        HashSet<int> ids = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        int groupSize = inputs[0].GroupPlaceCounts.Count;
        foreach (TeamScoreInput entry in inputs)
        {
            if (entry.TeamId < 0)
            {
                throw new InvalidOperationException($"Team input contains invalid team id {entry.TeamId}.");
            }

            if (string.IsNullOrWhiteSpace(entry.TeamName))
            {
                throw new InvalidOperationException("Team input contains an empty team name.");
            }

            if (!ids.Add(entry.TeamId))
            {
                throw new InvalidOperationException($"Team inputs contain duplicate team id {entry.TeamId}.");
            }

            if (!names.Add(entry.TeamName))
            {
                throw new InvalidOperationException($"Team inputs contain duplicate team '{entry.TeamName}'.");
            }

            if (entry.TotalScoreThousandths < 0 || entry.TotalBaseThousandths < 0)
            {
                throw new InvalidOperationException($"Team '{entry.TeamName}' has corrupt negative totals.");
            }

            if (entry.GroupPlaceCounts.Count != groupSize || entry.RoundPlaceCounts.Count != groupSize)
            {
                throw new InvalidOperationException($"Team '{entry.TeamName}' has inconsistent tie-break vector lengths.");
            }

            ValidateTeamVector(entry.GroupPlaceCounts, entry.TeamName);
            ValidateTeamVector(entry.RoundPlaceCounts, entry.TeamName);
        }
    }

    private static void ValidateTeamVector(IReadOnlyList<int> vector, string teamName)
    {
        foreach (int count in vector)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Team '{teamName}' has negative tie-break counts.");
            }
        }
    }

    private static IReadOnlyList<TeamScoreInput> OrderTiedTeams(List<TeamScoreInput> group, Pcg32V1 rng)
    {
        if (group.Count == 1)
        {
            return group;
        }

        IReadOnlyList<RankedEntry<TeamScoreInput>> ranked = TieBreaker.RankWithSeededDraw(
            group,
            entry => new DeterministicTieBreakKey(
                entry.GroupPlaceCounts,
                entry.RoundPlaceCounts,
                entry.TotalBaseThousandths),
            entry => entry.TeamName,
            rng);
        return ranked.Select(r => r.Entry).ToList();
    }
}
