using System.Text.Json;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Structural invariants for the Type Cup team event (MSS-027). Fundamental
/// failures throw and abort the mutation; corrupted sporting state is never
/// silently repaired. Four rank groups each hold N athletes (one per participating
/// creature type at the same selection rank, where N is the dynamically varying
/// team count) over eight rounds; the team score is the sum of the type's four
/// legs' group scores. Only the Color Cup has a fixed eight-team field; this slice
/// never assumes exactly eight teams. Active bonus applies with normal
/// fixed-point scoring, but the event never creates league
/// <c>StageStanding</c>, <c>SeasonStanding</c> or <c>Round</c> rows and never
/// writes earned bonus, so normal league championship totals and career bonus
/// are untouched. Permanent nationality is validated separately during persistence:
/// capped athletes must already match their allocated creature type.
/// </summary>
public static class TypeCupTeamInvariants
{
    /// <summary>
    /// Validates the allocated selection before simulation: whole teams of four with
    /// selection ranks 1..4 per creature type, all athletes distinct, at least two
    /// participating teams. Returns the dynamically varying team count. There is
    /// no artificial upper bound (Game Rules §15): 35 valid teams means 35 teams.
    /// </summary>
    public static int ValidateField(
        IReadOnlyList<TypeCupSelectionEntity> selection,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(rules);
        if (selection.Count == 0 || selection.Count % rules.TypeCupMinTeamSize != 0)
        {
            throw new InvalidOperationException(
                $"Type Cup team field must hold whole teams of {rules.TypeCupMinTeamSize}, was {selection.Count} selected athletes.");
        }

        int teamCount = selection.Count / rules.TypeCupMinTeamSize;
        if (teamCount < 2)
        {
            throw new InvalidOperationException(
                $"Type Cup team event requires at least two participating creature-type teams, was {teamCount}.");
        }

        ValidateFieldGroups(selection, rules);
        return teamCount;
    }

    /// <summary>
    /// Validates the rank-group partition: group N holds exactly the team-count
    /// athletes selected at rank N (one per creature type). An athlete in the wrong
    /// rank group aborts the mutation.
    /// </summary>
    public static void ValidateGroups(
        IReadOnlyDictionary<int, List<TypeCupSelectionEntity>> groups,
        RulesV1 rules,
        int teamCount)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(rules);
        if (teamCount < 2)
        {
            throw new InvalidOperationException($"Type Cup team count {teamCount} is out of range.");
        }

        if (groups.Count != rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup team event must hold exactly {rules.TypeCupMinTeamSize} groups, was {groups.Count}.");
        }

        foreach (int groupNumber in Enumerable.Range(1, rules.TypeCupMinTeamSize))
        {
            ValidateSingleGroup(groups, groupNumber, rules, teamCount);
        }
    }

    /// <summary>
    /// Validates one freshly simulated team round before commit: exact N
    /// placements, positions 1..N exactly once, unique athletes, base points
    /// matching the first N snapshot scoring-table entries, final points matching
    /// base plus active bonus, cumulative totals chaining and RNG advancement.
    /// </summary>
    public static void ValidateRound(
        TypeCupTeamRoundPayloadDocument payload,
        RulesV1 rules,
        int teamCount,
        ulong rngBeforeState,
        ulong rngBeforeStream)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(rules);
        CheckRoundIdentity(payload, rules, teamCount);
        CheckRoundRng(payload, rngBeforeState, rngBeforeStream);
        EnsurePlacements(payload, rules, teamCount);
    }

    /// <summary>
    /// Validates one freshly completed group leg before commit: exactly N
    /// ranked athletes, ranks 1..N exactly once, scores chaining from round
    /// payloads and round-place counts summing to eight per athlete.
    /// </summary>
    public static void ValidateCompletedLeg(
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegTotals> totals,
        int groupNumber,
        RulesV1 rules,
        int teamCount)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(rules);
        if (groupNumber < 1 || groupNumber > rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup team group {groupNumber} is out of range.");
        }

        CheckLegCounts(ranked, totals, rules, teamCount);
        CheckLegStandings(ranked, totals, rules, teamCount);
    }

    /// <summary>
    /// Validates freshly computed team championship standings before commit:
    /// exactly N teams, ranks 1..N exactly once, medals exactly on ranks
    /// 1..3 and team scores equal to the sum of the team's four leg scores.
    /// </summary>
    public static void ValidateTeams(
        IReadOnlyList<TeamEvent.TeamRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegRanked> legs,
        RulesV1 rules,
        int teamCount)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(rules);
        CheckTeamCounts(ranked, legs, rules, teamCount);
        CheckTeamSums(ranked, legs, rules);
        CheckTeamRanks(ranked, rules, teamCount);
    }

    /// <summary>
    /// Validates persisted team state: 32 round rows (4 groups x 8 rounds),
    /// teamCount x 4 leg rows, teamCount team rows with medals on ranks 1..3,
    /// and exactly four championship honours (one per winning-team member).
    /// </summary>
    public static void ValidatePersisted(
        SeasonEntity source,
        IReadOnlyList<TypeCupTeamRoundEntity> rounds,
        IReadOnlyList<TypeCupTeamGroupStandingEntity> legs,
        IReadOnlyList<TypeCupTeamStandingEntity> teams,
        IReadOnlyList<HonourEntity> honours,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(honours);
        ArgumentNullException.ThrowIfNull(rules);
        CheckPersistedSeasons(source);
        CheckPersistedCounts(rounds, legs, teams, rules);
        int teamCount = teams.Count;
        CheckPersistedRounds(rounds, source, rules, teamCount);
        CheckPersistedLegs(legs, source, rules, teamCount);
        CheckPersistedTeams(teams, source, rules, teamCount);
        CheckPersistedHonour(source, legs, teams, honours);
    }

    private static void ValidateFieldGroups(
        IReadOnlyList<TypeCupSelectionEntity> selection,
        RulesV1 rules)
    {
        HashSet<int> athleteIds = new();
        Dictionary<string, HashSet<int>> ranksByType = new(StringComparer.Ordinal);
        foreach (TypeCupSelectionEntity row in selection)
        {
            CheckFieldRow(row, athleteIds, rules);
            TrackFieldRank(ranksByType, row);
        }

        foreach ((string type, HashSet<int> ranks) in ranksByType)
        {
            if (!ranks.SetEquals(Enumerable.Range(1, rules.TypeCupMinTeamSize)))
            {
                throw new InvalidOperationException($"Type Cup team '{type}' must cover selection ranks 1..{rules.TypeCupMinTeamSize} exactly once.");
            }
        }
    }

    private static void CheckFieldRow(TypeCupSelectionEntity row, HashSet<int> athleteIds, RulesV1 rules)
    {
        if (string.IsNullOrWhiteSpace(row.CreatureType))
        {
            throw new InvalidOperationException("Type Cup team field contains an empty creature type.");
        }

        if (row.SaveAthleteId <= 0)
        {
            throw new InvalidOperationException($"Type Cup team field contains invalid athlete id {row.SaveAthleteId}.");
        }

        if (row.SelectionRank < 1 || row.SelectionRank > rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup team selection rank {row.SelectionRank} is out of range.");
        }

        if (!athleteIds.Add(row.SaveAthleteId))
        {
            throw new InvalidOperationException($"Type Cup team field contains duplicate athlete id {row.SaveAthleteId}.");
        }
    }

    private static void TrackFieldRank(Dictionary<string, HashSet<int>> ranksByType, TypeCupSelectionEntity row)
    {
        if (!ranksByType.TryGetValue(row.CreatureType, out HashSet<int>? ranks))
        {
            ranks = new HashSet<int>();
            ranksByType[row.CreatureType] = ranks;
        }

        if (!ranks.Add(row.SelectionRank))
        {
            throw new InvalidOperationException($"Type Cup team '{row.CreatureType}' contains duplicate selection rank {row.SelectionRank}.");
        }
    }

    private static void ValidateSingleGroup(
        IReadOnlyDictionary<int, List<TypeCupSelectionEntity>> groups,
        int groupNumber,
        RulesV1 rules,
        int teamCount)
    {
        if (!groups.TryGetValue(groupNumber, out List<TypeCupSelectionEntity>? members))
        {
            throw new InvalidOperationException($"Type Cup team event is missing group {groupNumber}.");
        }

        if (members.Count != teamCount)
        {
            throw new InvalidOperationException($"Type Cup team group {groupNumber} must hold exactly {teamCount} athletes, was {members.Count}.");
        }

        HashSet<string> types = new(StringComparer.Ordinal);
        HashSet<int> athletes = new();
        foreach (TypeCupSelectionEntity row in members)
        {
            if (row.SelectionRank != groupNumber)
            {
                throw new InvalidOperationException(
                    $"Athlete {row.SaveAthleteId} with selection rank #{row.SelectionRank} competes in wrong rank group {groupNumber}.");
            }

            if (string.IsNullOrWhiteSpace(row.CreatureType))
            {
                throw new InvalidOperationException($"Type Cup team group {groupNumber} contains an empty creature type.");
            }

            if (!types.Add(row.CreatureType))
            {
                throw new InvalidOperationException($"Type Cup team group {groupNumber} contains duplicate creature type '{row.CreatureType}'.");
            }

            if (!athletes.Add(row.SaveAthleteId))
            {
                throw new InvalidOperationException($"Type Cup team group {groupNumber} contains duplicate athlete id {row.SaveAthleteId}.");
            }
        }
    }

    private static void CheckRoundIdentity(TypeCupTeamRoundPayloadDocument payload, RulesV1 rules, int teamCount)
    {
        if (payload.Version != TypeCupTeamRoundPayloadDocument.PayloadVersion)
        {
            throw new InvalidOperationException($"Type Cup team payload version must be {TypeCupTeamRoundPayloadDocument.PayloadVersion}, was {payload.Version}.");
        }

        if (payload.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Type Cup team payload rules version must be {rules.Version}, was {payload.RulesVersion}.");
        }

        if (payload.SourceSeasonNumber < 1 || payload.SourceSeasonNumber % 2 != 0)
        {
            throw new InvalidOperationException($"Type Cup team source season must be a positive even season, was {payload.SourceSeasonNumber}.");
        }

        if (payload.GroupNumber < 1 || payload.GroupNumber > rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup team group must be 1..{rules.TypeCupMinTeamSize}, was {payload.GroupNumber}.");
        }

        if (payload.RoundNumber < 1 || payload.RoundNumber > rules.TypeCupGroupRounds)
        {
            throw new InvalidOperationException($"Type Cup team round must be 1..{rules.TypeCupGroupRounds}, was {payload.RoundNumber}.");
        }

        if (payload.Placements.Count != teamCount)
        {
            throw new InvalidOperationException($"Type Cup team round must contain exactly {teamCount} placements, was {payload.Placements.Count}.");
        }
    }

    private static void CheckRoundRng(TypeCupTeamRoundPayloadDocument payload, ulong rngBeforeState, ulong rngBeforeStream)
    {
        if (payload.RngBeforeState != rngBeforeState || payload.RngBeforeStream != rngBeforeStream)
        {
            throw new InvalidOperationException("Type Cup team payload RNG-before does not match the persisted save RNG chain.");
        }

        if (payload.RngAfterState == rngBeforeState && payload.RngAfterStream == rngBeforeStream)
        {
            throw new InvalidOperationException("Type Cup team round simulation must advance the save RNG.");
        }
    }

    private static void EnsurePlacements(TypeCupTeamRoundPayloadDocument payload, RulesV1 rules, int teamCount)
    {
        PlacementAccumulator accumulator = new();
        foreach (RoundPayloadEntry entry in payload.Placements)
        {
            CheckPlacementIdentity(entry, accumulator);
            CheckPlacementPoints(entry, rules, teamCount);
            CheckPlacementRanks(entry, teamCount);
        }

        if (!accumulator.Positions.SetEquals(Enumerable.Range(1, teamCount)))
        {
            throw new InvalidOperationException($"Type Cup team payload must cover positions 1..{teamCount} exactly once.");
        }

        EnsureRankConsistency(payload);
    }

    private sealed class PlacementAccumulator
    {
        public HashSet<int> Positions { get; } = new();

        public HashSet<int> AthleteIds { get; } = new();

        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
    }

    private static void CheckPlacementIdentity(RoundPayloadEntry entry, PlacementAccumulator accumulator)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Type Cup team payload contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Type Cup team payload contains an athlete with an empty name.");
        }

        if (!accumulator.Positions.Add(entry.Position))
        {
            throw new InvalidOperationException($"Type Cup team payload contains duplicate position {entry.Position}.");
        }

        if (!accumulator.AthleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Type Cup team payload contains duplicate athlete id {entry.AthleteId}.");
        }

        if (!accumulator.Names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Type Cup team payload contains duplicate athlete '{entry.Name}'.");
        }
    }

    private static void CheckPlacementPoints(RoundPayloadEntry entry, RulesV1 rules, int teamCount)
    {
        if (entry.Position < 1 || entry.Position > teamCount)
        {
            throw new InvalidOperationException($"Type Cup team payload position {entry.Position} is out of range.");
        }

        int expectedBase = SimulationKernel.Scoring.ScoringCalculator.TypeCupBasePointsForPosition(entry.Position, rules).Thousandths;
        if (entry.BaseThousandths != expectedBase)
        {
            throw new InvalidOperationException($"Type Cup team payload base points for position {entry.Position} must be {expectedBase}, was {entry.BaseThousandths}.");
        }

        if (entry.ActiveBonusThousandths < 0)
        {
            throw new InvalidOperationException($"Type Cup team payload active bonus for '{entry.Name}' cannot be negative.");
        }

        long expectedFinal = (long)entry.BaseThousandths * (RulesV1.BonusPercentScale + entry.ActiveBonusThousandths) / RulesV1.BonusPercentScale;
        if (entry.FinalThousandths != expectedFinal)
        {
            throw new InvalidOperationException($"Type Cup team payload final points for '{entry.Name}' must be {expectedFinal}, was {entry.FinalThousandths}.");
        }

        long expectedAfter = (long)entry.CumulativeBeforeThousandths + entry.FinalThousandths;
        if (entry.CumulativeAfterThousandths != expectedAfter)
        {
            throw new InvalidOperationException($"Type Cup team payload cumulative total for '{entry.Name}' is corrupt.");
        }
    }

    private static void CheckPlacementRanks(RoundPayloadEntry entry, int teamCount)
    {
        if (entry.RankBefore < 1 || entry.RankBefore > teamCount)
        {
            throw new InvalidOperationException($"Type Cup team payload rank-before for '{entry.Name}' is out of range.");
        }

        if (entry.RankAfter < 1 || entry.RankAfter > teamCount)
        {
            throw new InvalidOperationException($"Type Cup team payload rank-after for '{entry.Name}' is out of range.");
        }

        if (entry.RankMovement != entry.RankBefore - entry.RankAfter)
        {
            throw new InvalidOperationException($"Type Cup team payload rank movement for '{entry.Name}' is corrupt.");
        }
    }

    private static void EnsureRankConsistency(TypeCupTeamRoundPayloadDocument payload)
    {
        List<RoundPayloadEntry> byBefore = payload.Placements.OrderBy(e => e.RankBefore).ToList();
        List<RoundPayloadEntry> byAfter = payload.Placements.OrderBy(e => e.RankAfter).ToList();
        for (int i = 0; i < byBefore.Count; i++)
        {
            if (byBefore[i].RankBefore != i + 1)
            {
                throw new InvalidOperationException("Type Cup team payload rank-before sequence is corrupt.");
            }
        }

        for (int i = 0; i < byAfter.Count; i++)
        {
            if (byAfter[i].RankAfter != i + 1)
            {
                throw new InvalidOperationException("Type Cup team payload rank-after sequence is corrupt.");
            }
        }
    }

    private static void CheckLegCounts(
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegTotals> totals,
        RulesV1 rules,
        int teamCount)
    {
        if (ranked.Count != teamCount)
        {
            throw new InvalidOperationException($"Completed Type Cup team group must rank exactly {teamCount} athletes, was {ranked.Count}.");
        }

        if (totals.Count != teamCount)
        {
            throw new InvalidOperationException($"Completed Type Cup team group must total exactly {teamCount} athletes, was {totals.Count}.");
        }
    }

    private static void CheckLegStandings(
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegTotals> totals,
        RulesV1 rules,
        int teamCount)
    {
        Dictionary<int, TeamEvent.TeamLegTotals> totalsById = totals.ToDictionary(t => t.AthleteId);
        HashSet<int> ranks = new();
        HashSet<int> athleteIds = new();
        foreach (TeamEvent.TeamLegRanked entry in ranked)
        {
            CheckLegIdentity(entry, ranks, athleteIds, teamCount);
            CheckLegTotals(entry, totalsById, rules, teamCount);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, teamCount)))
        {
            throw new InvalidOperationException($"Completed Type Cup team group must cover ranks 1..{teamCount} exactly once.");
        }
    }

    private static void CheckLegIdentity(TeamEvent.TeamLegRanked entry, HashSet<int> ranks, HashSet<int> athleteIds, int teamCount)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Completed Type Cup team group contains invalid athlete id {entry.AthleteId}.");
        }

        if (entry.LegRank < 1 || entry.LegRank > teamCount)
        {
            throw new InvalidOperationException($"Completed Type Cup team group rank {entry.LegRank} is out of range.");
        }

        if (!ranks.Add(entry.LegRank))
        {
            throw new InvalidOperationException($"Completed Type Cup team group contains duplicate rank {entry.LegRank}.");
        }

        if (!athleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Completed Type Cup team group contains duplicate athlete id {entry.AthleteId}.");
        }
    }

    private static void CheckLegTotals(
        TeamEvent.TeamLegRanked entry,
        Dictionary<int, TeamEvent.TeamLegTotals> totalsById,
        RulesV1 rules,
        int teamCount)
    {
        if (!totalsById.TryGetValue(entry.AthleteId, out TeamEvent.TeamLegTotals? accumulated))
        {
            throw new InvalidOperationException($"Completed Type Cup team standing for '{entry.Name}' has no accumulated totals.");
        }

        if (entry.LegScoreThousandths != accumulated.LegScoreThousandths)
        {
            throw new InvalidOperationException($"Completed Type Cup team score for '{entry.Name}' does not match accumulated round finals.");
        }

        if (entry.LegBaseThousandths != accumulated.LegBaseThousandths)
        {
            throw new InvalidOperationException($"Completed Type Cup team base score for '{entry.Name}' does not match accumulated round base points.");
        }

        if (entry.RoundPlaceCounts.Count != teamCount)
        {
            throw new InvalidOperationException($"Completed Type Cup team counts for '{entry.Name}' must cover {teamCount} positions.");
        }

        int sum = 0;
        foreach (int count in entry.RoundPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Completed Type Cup team counts for '{entry.Name}' cannot be negative.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum != rules.TypeCupGroupRounds)
        {
            throw new InvalidOperationException($"Completed Type Cup team totals for '{entry.Name}' sum to {sum} round appearances, expected {rules.TypeCupGroupRounds}.");
        }
    }

    private static void CheckTeamCounts(
        IReadOnlyList<TeamEvent.TeamRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegRanked> legs,
        RulesV1 rules,
        int teamCount)
    {
        if (ranked.Count != teamCount)
        {
            throw new InvalidOperationException($"Completed Type Cup team championship must rank exactly {teamCount} teams, was {ranked.Count}.");
        }

        if (legs.Count != teamCount * rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Completed Type Cup team championship must hold exactly {teamCount * rules.TypeCupMinTeamSize} legs, was {legs.Count}.");
        }
    }

    private static void CheckTeamSums(
        IReadOnlyList<TeamEvent.TeamRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegRanked> legs,
        RulesV1 rules)
    {
        Dictionary<int, List<TeamEvent.TeamLegRanked>> legsByTeam = new();
        foreach (TeamEvent.TeamLegRanked leg in legs)
        {
            if (!legsByTeam.TryGetValue(leg.TeamId, out List<TeamEvent.TeamLegRanked>? list))
            {
                list = new List<TeamEvent.TeamLegRanked>();
                legsByTeam[leg.TeamId] = list;
            }

            list.Add(leg);
        }

        foreach (TeamEvent.TeamRanked team in ranked)
        {
            CheckSingleTeamSum(team, legsByTeam, rules);
        }
    }

    private static void CheckSingleTeamSum(
        TeamEvent.TeamRanked team,
        Dictionary<int, List<TeamEvent.TeamLegRanked>> legsByTeam,
        RulesV1 rules)
    {
        if (!legsByTeam.TryGetValue(team.TeamId, out List<TeamEvent.TeamLegRanked>? members))
        {
            throw new InvalidOperationException($"Type Cup team '{team.TeamName}' has no legs.");
        }

        if (members.Count != rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup team '{team.TeamName}' must field exactly {rules.TypeCupMinTeamSize} legs, was {members.Count}.");
        }

        int expectedScore = 0;
        int expectedBase = 0;
        checked
        {
            foreach (TeamEvent.TeamLegRanked leg in members)
            {
                expectedScore += leg.LegScoreThousandths;
                expectedBase += leg.LegBaseThousandths;
            }
        }

        if (team.TeamScoreThousandths != expectedScore)
        {
            throw new InvalidOperationException($"Type Cup team '{team.TeamName}' score does not equal the sum of its four legs.");
        }

        if (team.TeamBaseThousandths != expectedBase)
        {
            throw new InvalidOperationException($"Type Cup team '{team.TeamName}' base score does not equal the sum of its four legs.");
        }
    }

    private static void CheckTeamRanks(IReadOnlyList<TeamEvent.TeamRanked> ranked, RulesV1 rules, int teamCount)
    {
        HashSet<int> ranks = new();
        HashSet<int> teamIds = new();
        foreach (TeamEvent.TeamRanked team in ranked)
        {
            if (team.TeamRank < 1 || team.TeamRank > teamCount)
            {
                throw new InvalidOperationException($"Type Cup team rank {team.TeamRank} is out of range.");
            }

            if (!ranks.Add(team.TeamRank))
            {
                throw new InvalidOperationException($"Type Cup team championship contains duplicate rank {team.TeamRank}.");
            }

            if (!teamIds.Add(team.TeamId))
            {
                throw new InvalidOperationException($"Type Cup team championship contains duplicate team id {team.TeamId}.");
            }

            CheckTeamVectors(team, rules, teamCount);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, teamCount)))
        {
            throw new InvalidOperationException($"Type Cup team championship must cover ranks 1..{teamCount} exactly once.");
        }
    }

    private static void CheckTeamVectors(TeamEvent.TeamRanked team, RulesV1 rules, int teamCount)
    {
        if (team.GroupPlaceCounts.Count != teamCount || team.RoundPlaceCounts.Count != teamCount)
        {
            throw new InvalidOperationException($"Type Cup team '{team.TeamName}' has corrupt tie-break vectors.");
        }

        int groupSum = 0;
        foreach (int count in team.GroupPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Type Cup team '{team.TeamName}' has negative group counts.");
            }

            checked
            {
                groupSum += count;
            }
        }

        if (groupSum != rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup team '{team.TeamName}' group counts sum to {groupSum}, expected {rules.TypeCupMinTeamSize}.");
        }

        int roundSum = 0;
        foreach (int count in team.RoundPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Type Cup team '{team.TeamName}' has negative round counts.");
            }

            checked
            {
                roundSum += count;
            }
        }

        int expectedRounds = rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds;
        if (roundSum != expectedRounds)
        {
            throw new InvalidOperationException($"Type Cup team '{team.TeamName}' round counts sum to {roundSum}, expected {expectedRounds}.");
        }
    }

    private static void CheckPersistedSeasons(SeasonEntity source)
    {
        if (!source.IsComplete)
        {
            throw new InvalidOperationException($"Type Cup team source Season {source.SeasonNumber} must be complete.");
        }

        if (source.SeasonNumber % 2 != 0)
        {
            throw new InvalidOperationException($"Type Cup team source Season {source.SeasonNumber} must be even.");
        }
    }

    private static void CheckPersistedCounts(
        IReadOnlyList<TypeCupTeamRoundEntity> rounds,
        IReadOnlyList<TypeCupTeamGroupStandingEntity> legs,
        IReadOnlyList<TypeCupTeamStandingEntity> teams,
        RulesV1 rules)
    {
        int expectedRounds = rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds;
        if (rounds.Count != expectedRounds)
        {
            throw new InvalidOperationException($"Type Cup team must persist exactly {expectedRounds} rounds, was {rounds.Count}.");
        }

        if (teams.Count < 2)
        {
            throw new InvalidOperationException($"Type Cup team must persist at least two teams, was {teams.Count}.");
        }

        if (legs.Count != teams.Count * rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup team must persist exactly {teams.Count * rules.TypeCupMinTeamSize} legs, was {legs.Count}.");
        }
    }

    private static void CheckPersistedRounds(
        IReadOnlyList<TypeCupTeamRoundEntity> rounds,
        SeasonEntity source,
        RulesV1 rules,
        int teamCount)
    {
        HashSet<(int Group, int Round)> seen = new();
        foreach (TypeCupTeamRoundEntity round in rounds)
        {
            CheckSinglePersistedRound(round, source, rules, seen);
        }

        for (int group = 1; group <= rules.TypeCupMinTeamSize; group++)
        {
            for (int number = 1; number <= rules.TypeCupGroupRounds; number++)
            {
                if (!seen.Contains((group, number)))
                {
                    throw new InvalidOperationException($"Type Cup team is missing group {group} round {number}.");
                }
            }
        }
    }

    private static void CheckSinglePersistedRound(
        TypeCupTeamRoundEntity round,
        SeasonEntity source,
        RulesV1 rules,
        HashSet<(int Group, int Round)> seen)
    {
        if (round.SourceSeasonId != source.Id || round.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Type Cup team round {round.Id} has corrupt source linkage.");
        }

        if (round.GroupNumber < 1 || round.GroupNumber > rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup team round group {round.GroupNumber} is out of range.");
        }

        if (round.RoundNumber < 1 || round.RoundNumber > rules.TypeCupGroupRounds)
        {
            throw new InvalidOperationException($"Type Cup team round number {round.RoundNumber} is out of range.");
        }

        if (!seen.Add((round.GroupNumber, round.RoundNumber)))
        {
            throw new InvalidOperationException($"Type Cup team contains duplicate group {round.GroupNumber} round {round.RoundNumber}.");
        }

        if (round.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Type Cup team group {round.GroupNumber} round {round.RoundNumber} has corrupt rules version.");
        }

        if (string.IsNullOrWhiteSpace(round.PayloadJson) || string.IsNullOrWhiteSpace(round.PayloadChecksum))
        {
            throw new InvalidOperationException($"Type Cup team group {round.GroupNumber} round {round.RoundNumber} has an empty payload.");
        }

        TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
        if (!string.Equals(document.Checksum, round.PayloadChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Type Cup team group {round.GroupNumber} round {round.RoundNumber} checksum does not match its payload.");
        }

        if (document.RoundNumber != round.RoundNumber || document.GroupNumber != round.GroupNumber || document.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Type Cup team group {round.GroupNumber} round {round.RoundNumber} payload identity is corrupt.");
        }
    }

    private static void CheckPersistedLegs(
        IReadOnlyList<TypeCupTeamGroupStandingEntity> legs,
        SeasonEntity source,
        RulesV1 rules,
        int teamCount)
    {
        HashSet<int> athletes = new();
        Dictionary<int, HashSet<int>> ranksByGroup = new();
        foreach (TypeCupTeamGroupStandingEntity leg in legs)
        {
            CheckSinglePersistedLeg(leg, source, rules, teamCount, athletes, ranksByGroup);
        }

        foreach (int group in Enumerable.Range(1, rules.TypeCupMinTeamSize))
        {
            if (!ranksByGroup.TryGetValue(group, out HashSet<int>? ranks) ||
                !ranks.SetEquals(Enumerable.Range(1, teamCount)))
            {
                throw new InvalidOperationException($"Type Cup team group {group} must cover ranks 1..{teamCount} exactly once.");
            }
        }
    }

    private static void CheckSinglePersistedLeg(
        TypeCupTeamGroupStandingEntity leg,
        SeasonEntity source,
        RulesV1 rules,
        int teamCount,
        HashSet<int> athletes,
        Dictionary<int, HashSet<int>> ranksByGroup)
    {
        if (leg.SourceSeasonId != source.Id || leg.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Type Cup team leg {leg.Id} has corrupt source linkage.");
        }

        if (leg.GroupNumber < 1 || leg.GroupNumber > rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup team leg group {leg.GroupNumber} is out of range.");
        }

        if (leg.SelectionRank != leg.GroupNumber)
        {
            throw new InvalidOperationException($"Type Cup team leg {leg.Id} selection rank #{leg.SelectionRank} does not match group {leg.GroupNumber}.");
        }

        if (string.IsNullOrWhiteSpace(leg.CreatureType))
        {
            throw new InvalidOperationException($"Type Cup team leg {leg.Id} has an empty creature type.");
        }

        if (leg.GroupRank < 1 || leg.GroupRank > teamCount)
        {
            throw new InvalidOperationException($"Type Cup team leg group rank {leg.GroupRank} is out of range.");
        }

        if (!athletes.Add(leg.SaveAthleteId))
        {
            throw new InvalidOperationException($"Type Cup team contains duplicate athlete id {leg.SaveAthleteId}.");
        }

        if (!ranksByGroup.TryGetValue(leg.GroupNumber, out HashSet<int>? ranks))
        {
            ranks = new HashSet<int>();
            ranksByGroup[leg.GroupNumber] = ranks;
        }

        if (!ranks.Add(leg.GroupRank))
        {
            throw new InvalidOperationException($"Type Cup team group {leg.GroupNumber} contains duplicate rank {leg.GroupRank}.");
        }

        CheckLegPlaceCounts(leg, rules, teamCount);
    }

    private static void CheckLegPlaceCounts(TypeCupTeamGroupStandingEntity leg, RulesV1 rules, int teamCount)
    {
        List<int>? counts = JsonSerializer.Deserialize<List<int>>(leg.RoundPlaceCountsJson);
        if (counts is null || counts.Count != teamCount)
        {
            throw new InvalidOperationException($"Type Cup team leg {leg.Id} has corrupt round-place counts.");
        }

        int sum = 0;
        foreach (int count in counts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Type Cup team leg {leg.Id} has negative placement counts.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum != rules.TypeCupGroupRounds)
        {
            throw new InvalidOperationException($"Type Cup team leg {leg.Id} sums to {sum} rounds, expected {rules.TypeCupGroupRounds}.");
        }
    }

    private static void CheckPersistedTeams(
        IReadOnlyList<TypeCupTeamStandingEntity> teams,
        SeasonEntity source,
        RulesV1 rules,
        int teamCount)
    {
        HashSet<int> ranks = new();
        HashSet<string> types = new(StringComparer.Ordinal);
        foreach (TypeCupTeamStandingEntity team in teams)
        {
            CheckSinglePersistedTeam(team, source, rules, teamCount, ranks, types);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, teamCount)))
        {
            throw new InvalidOperationException($"Type Cup team championship must cover ranks 1..{teamCount} exactly once.");
        }
    }

    private static void CheckSinglePersistedTeam(
        TypeCupTeamStandingEntity team,
        SeasonEntity source,
        RulesV1 rules,
        int teamCount,
        HashSet<int> ranks,
        HashSet<string> types)
    {
        if (team.SourceSeasonId != source.Id || team.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Type Cup team {team.Id} has corrupt source linkage.");
        }

        if (string.IsNullOrWhiteSpace(team.CreatureType))
        {
            throw new InvalidOperationException($"Type Cup team {team.Id} has an empty creature type.");
        }

        if (team.TeamRank < 1 || team.TeamRank > teamCount)
        {
            throw new InvalidOperationException($"Type Cup team rank {team.TeamRank} is out of range.");
        }

        if (!ranks.Add(team.TeamRank))
        {
            throw new InvalidOperationException($"Type Cup team championship contains duplicate rank {team.TeamRank}.");
        }

        if (!types.Add(team.CreatureType))
        {
            throw new InvalidOperationException($"Type Cup team championship contains duplicate creature type '{team.CreatureType}'.");
        }

        int expectedMedal = team.TeamRank switch
        {
            1 => (int)TypeCupMedal.Gold,
            2 => (int)TypeCupMedal.Silver,
            3 => (int)TypeCupMedal.Bronze,
            _ => (int)TypeCupMedal.None,
        };
        if (team.Medal != expectedMedal)
        {
            throw new InvalidOperationException($"Type Cup team rank {team.TeamRank} has corrupt medal {team.Medal}.");
        }

        CheckTeamCountJson(team, rules, teamCount);
    }

    private static void CheckTeamCountJson(TypeCupTeamStandingEntity team, RulesV1 rules, int teamCount)
    {
        List<int>? groupCounts = JsonSerializer.Deserialize<List<int>>(team.GroupPlaceCountsJson);
        List<int>? roundCounts = JsonSerializer.Deserialize<List<int>>(team.RoundPlaceCountsJson);
        if (groupCounts is null || groupCounts.Count != teamCount ||
            roundCounts is null || roundCounts.Count != teamCount)
        {
            throw new InvalidOperationException($"Type Cup team {team.Id} has corrupt tie-break counts.");
        }

        int groupSum = 0;
        foreach (int count in groupCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Type Cup team {team.Id} has negative group counts.");
            }

            checked
            {
                groupSum += count;
            }
        }

        if (groupSum != rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup team {team.Id} group counts sum to {groupSum}, expected {rules.TypeCupMinTeamSize}.");
        }

        int roundSum = 0;
        foreach (int count in roundCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Type Cup team {team.Id} has negative round counts.");
            }

            checked
            {
                roundSum += count;
            }
        }

        int expectedRounds = rules.TypeCupMinTeamSize * rules.TypeCupGroupRounds;
        if (roundSum != expectedRounds)
        {
            throw new InvalidOperationException($"Type Cup team {team.Id} round counts sum to {roundSum}, expected {expectedRounds}.");
        }
    }

    private static void CheckPersistedHonour(
        SeasonEntity source,
        IReadOnlyList<TypeCupTeamGroupStandingEntity> legs,
        IReadOnlyList<TypeCupTeamStandingEntity> teams,
        IReadOnlyList<HonourEntity> honours)
    {
        List<HonourEntity> teamHonours = honours
            .Where(h => h.SeasonId == source.Id && h.Kind == (int)Features.Records.HonourKind.TypeCupTeamChampion)
            .ToList();
        if (teamHonours.Count != 4)
        {
            throw new InvalidOperationException(
                $"Type Cup team for Season {source.SeasonNumber} must persist exactly four team championship honours, was {teamHonours.Count}.");
        }

        TypeCupTeamStandingEntity champion = teams.Single(s => s.TeamRank == 1);
        HashSet<int> championAthletes = legs
            .Where(l => string.Equals(l.CreatureType, champion.CreatureType, StringComparison.Ordinal))
            .Select(l => l.SaveAthleteId)
            .ToHashSet();
        if (championAthletes.Count != 4)
        {
            throw new InvalidOperationException("Type Cup team champion must field exactly four legs.");
        }

        HashSet<int> honourAthletes = new();
        foreach (HonourEntity honour in teamHonours)
        {
            if (honour.SeasonNumber != source.SeasonNumber)
            {
                throw new InvalidOperationException("Type Cup team championship honour has corrupt season linkage.");
            }

            if (!championAthletes.Contains(honour.SaveAthleteId))
            {
                throw new InvalidOperationException("Type Cup team championship honour does not match a champion-team leg.");
            }

            if (!honourAthletes.Add(honour.SaveAthleteId))
            {
                throw new InvalidOperationException("Type Cup team championship honours must cover four distinct athletes.");
            }
        }
    }
}
