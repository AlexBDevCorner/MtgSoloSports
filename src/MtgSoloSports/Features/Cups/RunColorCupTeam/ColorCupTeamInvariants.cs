using System.Text.Json;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunColorCupTeam;

/// <summary>
/// Structural invariants for the Color Cup team event (MSS-025). Fundamental
/// failures throw and abort the mutation; corrupted sporting state is never
/// silently repaired. Four rank groups each hold eight athletes (one per color
/// at the same selection rank) over eight rounds; the team score is the sum of
/// the color's four legs' group scores. Active bonus applies with normal
/// fixed-point scoring, but the event never creates league
/// <c>StageStanding</c>, <c>SeasonStanding</c> or <c>Round</c> rows and never
/// writes earned bonus, so normal league championship totals and career bonus
/// are untouched.
/// </summary>
public static class ColorCupTeamInvariants
{
    /// <summary>
    /// Validates the 32-athlete selection before simulation: exactly four rows
    /// per sporting color with selection ranks 1..4, all athletes distinct.
    /// </summary>
    public static void ValidateField(
        IReadOnlyList<ColorCupSelectionEntity> selection,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(rules);
        if (selection.Count != rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException(
                $"Color Cup team field must hold exactly {rules.ColorCupColorCount * rules.ColorCupTeamSize} selected athletes, was {selection.Count}.");
        }

        ValidateFieldGroups(selection, rules);
    }

    /// <summary>
    /// Validates the rank-group partition: group N holds exactly the eight
    /// athletes selected at rank N (one per color). An athlete in the wrong
    /// rank group aborts the mutation.
    /// </summary>
    public static void ValidateGroups(
        IReadOnlyDictionary<int, List<ColorCupSelectionEntity>> groups,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(rules);
        if (groups.Count != rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup team event must hold exactly {rules.ColorCupTeamSize} groups, was {groups.Count}.");
        }

        foreach (int groupNumber in Enumerable.Range(1, rules.ColorCupTeamSize))
        {
            ValidateSingleGroup(groups, groupNumber, rules);
        }
    }

    /// <summary>
    /// Validates one freshly simulated team round before commit: exact eight
    /// placements, positions 1..8 exactly once, unique athletes, base points
    /// matching the snapshot scoring table, final points matching base plus
    /// active bonus, cumulative totals chaining and RNG advancement.
    /// </summary>
    public static void ValidateRound(
        ColorCupTeamRoundPayloadDocument payload,
        RulesV1 rules,
        ulong rngBeforeState,
        ulong rngBeforeStream)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(rules);
        CheckRoundIdentity(payload, rules);
        CheckRoundRng(payload, rngBeforeState, rngBeforeStream);
        EnsurePlacements(payload, rules);
    }

    /// <summary>
    /// Validates one freshly completed group leg before commit: exactly eight
    /// ranked athletes, ranks 1..8 exactly once, scores chaining from round
    /// payloads and round-place counts summing to eight per athlete.
    /// </summary>
    public static void ValidateCompletedLeg(
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegTotals> totals,
        int groupNumber,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(rules);
        if (groupNumber < 1 || groupNumber > rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup team group {groupNumber} is out of range.");
        }

        CheckLegCounts(ranked, totals, rules);
        CheckLegStandings(ranked, totals, rules);
    }

    /// <summary>
    /// Validates freshly computed team championship standings before commit:
    /// exactly eight teams, ranks 1..8 exactly once, medals exactly on ranks
    /// 1..3 and team scores equal to the sum of the team's four leg scores.
    /// </summary>
    public static void ValidateTeams(
        IReadOnlyList<TeamEvent.TeamRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegRanked> legs,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(rules);
        CheckTeamCounts(ranked, legs, rules);
        CheckTeamSums(ranked, legs, rules);
        CheckTeamRanks(ranked, rules);
    }

    /// <summary>
    /// Validates persisted team state: 32 round rows (4 groups x 8 rounds),
    /// 32 leg rows, 8 team rows with medals on ranks 1..3, and twelve
    /// podium honours (four members each for ranks 1/2/3).
    /// </summary>
    public static void ValidatePersisted(
        SeasonEntity source,
        IReadOnlyList<ColorCupTeamRoundEntity> rounds,
        IReadOnlyList<ColorCupTeamGroupStandingEntity> legs,
        IReadOnlyList<ColorCupTeamStandingEntity> teams,
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
        CheckPersistedRounds(rounds, source, rules);
        CheckPersistedLegs(legs, source, rules);
        CheckPersistedTeams(teams, source, rules);
        CheckPersistedHonour(source, legs, teams, honours);
    }

    private static void ValidateFieldGroups(
        IReadOnlyList<ColorCupSelectionEntity> selection,
        RulesV1 rules)
    {
        HashSet<int> athleteIds = new();
        Dictionary<int, HashSet<int>> ranksByColor = new();
        foreach (ColorCupSelectionEntity row in selection)
        {
            CheckFieldRow(row, athleteIds, rules);
            TrackFieldRank(ranksByColor, row);
        }

        if (ranksByColor.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team field must cover exactly {rules.ColorCupColorCount} colors, was {ranksByColor.Count}.");
        }

        foreach ((int color, HashSet<int> ranks) in ranksByColor)
        {
            if (!ranks.SetEquals(Enumerable.Range(1, rules.ColorCupTeamSize)))
            {
                throw new InvalidOperationException($"Color Cup team color {color} must cover selection ranks 1..{rules.ColorCupTeamSize} exactly once.");
            }
        }
    }

    private static void CheckFieldRow(ColorCupSelectionEntity row, HashSet<int> athleteIds, RulesV1 rules)
    {
        if (row.SaveAthleteId <= 0)
        {
            throw new InvalidOperationException($"Color Cup team field contains invalid athlete id {row.SaveAthleteId}.");
        }

        if (row.SelectionRank < 1 || row.SelectionRank > rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup team selection rank {row.SelectionRank} is out of range.");
        }

        if (!athleteIds.Add(row.SaveAthleteId))
        {
            throw new InvalidOperationException($"Color Cup team field contains duplicate athlete id {row.SaveAthleteId}.");
        }
    }

    private static void TrackFieldRank(Dictionary<int, HashSet<int>> ranksByColor, ColorCupSelectionEntity row)
    {
        if (!ranksByColor.TryGetValue(row.SportingColor, out HashSet<int>? ranks))
        {
            ranks = new HashSet<int>();
            ranksByColor[row.SportingColor] = ranks;
        }

        if (!ranks.Add(row.SelectionRank))
        {
            throw new InvalidOperationException($"Color Cup team color {row.SportingColor} contains duplicate selection rank {row.SelectionRank}.");
        }
    }

    private static void ValidateSingleGroup(
        IReadOnlyDictionary<int, List<ColorCupSelectionEntity>> groups,
        int groupNumber,
        RulesV1 rules)
    {
        if (!groups.TryGetValue(groupNumber, out List<ColorCupSelectionEntity>? members))
        {
            throw new InvalidOperationException($"Color Cup team event is missing group {groupNumber}.");
        }

        if (members.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team group {groupNumber} must hold exactly {rules.ColorCupColorCount} athletes, was {members.Count}.");
        }

        HashSet<int> colors = new();
        HashSet<int> athletes = new();
        foreach (ColorCupSelectionEntity row in members)
        {
            if (row.SelectionRank != groupNumber)
            {
                throw new InvalidOperationException(
                    $"Athlete {row.SaveAthleteId} with selection rank #{row.SelectionRank} competes in wrong rank group {groupNumber}.");
            }

            if (!colors.Add(row.SportingColor))
            {
                throw new InvalidOperationException($"Color Cup team group {groupNumber} contains duplicate sporting color {row.SportingColor}.");
            }

            if (!athletes.Add(row.SaveAthleteId))
            {
                throw new InvalidOperationException($"Color Cup team group {groupNumber} contains duplicate athlete id {row.SaveAthleteId}.");
            }
        }
    }

    private static void CheckRoundIdentity(ColorCupTeamRoundPayloadDocument payload, RulesV1 rules)
    {
        if (payload.Version != ColorCupTeamRoundPayloadDocument.PayloadVersion)
        {
            throw new InvalidOperationException($"Color Cup team payload version must be {ColorCupTeamRoundPayloadDocument.PayloadVersion}, was {payload.Version}.");
        }

        if (payload.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Color Cup team payload rules version must be {rules.Version}, was {payload.RulesVersion}.");
        }

        if (payload.SourceSeasonNumber < 1 || payload.SourceSeasonNumber % 2 != 1)
        {
            throw new InvalidOperationException($"Color Cup team source season must be a positive odd season, was {payload.SourceSeasonNumber}.");
        }

        if (payload.GroupNumber < 1 || payload.GroupNumber > rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup team group must be 1..{rules.ColorCupTeamSize}, was {payload.GroupNumber}.");
        }

        if (payload.RoundNumber < 1 || payload.RoundNumber > rules.ColorCupTeamGroupRounds)
        {
            throw new InvalidOperationException($"Color Cup team round must be 1..{rules.ColorCupTeamGroupRounds}, was {payload.RoundNumber}.");
        }

        if (payload.Placements.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team round must contain exactly {rules.ColorCupColorCount} placements, was {payload.Placements.Count}.");
        }
    }

    private static void CheckRoundRng(ColorCupTeamRoundPayloadDocument payload, ulong rngBeforeState, ulong rngBeforeStream)
    {
        if (payload.RngBeforeState != rngBeforeState || payload.RngBeforeStream != rngBeforeStream)
        {
            throw new InvalidOperationException("Color Cup team payload RNG-before does not match the persisted save RNG chain.");
        }

        if (payload.RngAfterState == rngBeforeState && payload.RngAfterStream == rngBeforeStream)
        {
            throw new InvalidOperationException("Color Cup team round simulation must advance the save RNG.");
        }
    }

    private static void EnsurePlacements(ColorCupTeamRoundPayloadDocument payload, RulesV1 rules)
    {
        PlacementAccumulator accumulator = new();
        foreach (RoundPayloadEntry entry in payload.Placements)
        {
            CheckPlacementIdentity(entry, accumulator);
            CheckPlacementPoints(entry, rules);
            CheckPlacementRanks(entry, rules);
        }

        if (!accumulator.Positions.SetEquals(Enumerable.Range(1, rules.ColorCupColorCount)))
        {
            throw new InvalidOperationException($"Color Cup team payload must cover positions 1..{rules.ColorCupColorCount} exactly once.");
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
            throw new InvalidOperationException($"Color Cup team payload contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Color Cup team payload contains an athlete with an empty name.");
        }

        if (!accumulator.Positions.Add(entry.Position))
        {
            throw new InvalidOperationException($"Color Cup team payload contains duplicate position {entry.Position}.");
        }

        if (!accumulator.AthleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Color Cup team payload contains duplicate athlete id {entry.AthleteId}.");
        }

        if (!accumulator.Names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Color Cup team payload contains duplicate athlete '{entry.Name}'.");
        }
    }

    private static void CheckPlacementPoints(RoundPayloadEntry entry, RulesV1 rules)
    {
        if (entry.Position < 1 || entry.Position > rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team payload position {entry.Position} is out of range.");
        }

        int expectedBase = rules.ScoringTable[entry.Position - 1] * RulesV1.FixedScale;
        if (entry.BaseThousandths != expectedBase)
        {
            throw new InvalidOperationException($"Color Cup team payload base points for position {entry.Position} must be {expectedBase}, was {entry.BaseThousandths}.");
        }

        if (entry.ActiveBonusThousandths < 0)
        {
            throw new InvalidOperationException($"Color Cup team payload active bonus for '{entry.Name}' cannot be negative.");
        }

        long expectedFinal = (long)entry.BaseThousandths * (RulesV1.BonusPercentScale + entry.ActiveBonusThousandths) / RulesV1.BonusPercentScale;
        if (entry.FinalThousandths != expectedFinal)
        {
            throw new InvalidOperationException($"Color Cup team payload final points for '{entry.Name}' must be {expectedFinal}, was {entry.FinalThousandths}.");
        }

        long expectedAfter = (long)entry.CumulativeBeforeThousandths + entry.FinalThousandths;
        if (entry.CumulativeAfterThousandths != expectedAfter)
        {
            throw new InvalidOperationException($"Color Cup team payload cumulative total for '{entry.Name}' is corrupt.");
        }
    }

    private static void CheckPlacementRanks(RoundPayloadEntry entry, RulesV1 rules)
    {
        if (entry.RankBefore < 1 || entry.RankBefore > rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team payload rank-before for '{entry.Name}' is out of range.");
        }

        if (entry.RankAfter < 1 || entry.RankAfter > rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team payload rank-after for '{entry.Name}' is out of range.");
        }

        if (entry.RankMovement != entry.RankBefore - entry.RankAfter)
        {
            throw new InvalidOperationException($"Color Cup team payload rank movement for '{entry.Name}' is corrupt.");
        }
    }

    private static void EnsureRankConsistency(ColorCupTeamRoundPayloadDocument payload)
    {
        List<RoundPayloadEntry> byBefore = payload.Placements.OrderBy(e => e.RankBefore).ToList();
        List<RoundPayloadEntry> byAfter = payload.Placements.OrderBy(e => e.RankAfter).ToList();
        for (int i = 0; i < byBefore.Count; i++)
        {
            if (byBefore[i].RankBefore != i + 1)
            {
                throw new InvalidOperationException("Color Cup team payload rank-before sequence is corrupt.");
            }
        }

        for (int i = 0; i < byAfter.Count; i++)
        {
            if (byAfter[i].RankAfter != i + 1)
            {
                throw new InvalidOperationException("Color Cup team payload rank-after sequence is corrupt.");
            }
        }
    }

    private static void CheckLegCounts(
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegTotals> totals,
        RulesV1 rules)
    {
        if (ranked.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Completed Color Cup team group must rank exactly {rules.ColorCupColorCount} athletes, was {ranked.Count}.");
        }

        if (totals.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Completed Color Cup team group must total exactly {rules.ColorCupColorCount} athletes, was {totals.Count}.");
        }
    }

    private static void CheckLegStandings(
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegTotals> totals,
        RulesV1 rules)
    {
        Dictionary<int, TeamEvent.TeamLegTotals> totalsById = totals.ToDictionary(t => t.AthleteId);
        HashSet<int> ranks = new();
        HashSet<int> athleteIds = new();
        foreach (TeamEvent.TeamLegRanked entry in ranked)
        {
            CheckLegIdentity(entry, ranks, athleteIds, rules);
            CheckLegTotals(entry, totalsById, rules);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, rules.ColorCupColorCount)))
        {
            throw new InvalidOperationException("Completed Color Cup team group must cover ranks 1..8 exactly once.");
        }
    }

    private static void CheckLegIdentity(TeamEvent.TeamLegRanked entry, HashSet<int> ranks, HashSet<int> athleteIds, RulesV1 rules)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Completed Color Cup team group contains invalid athlete id {entry.AthleteId}.");
        }

        if (entry.LegRank < 1 || entry.LegRank > rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Completed Color Cup team group rank {entry.LegRank} is out of range.");
        }

        if (!ranks.Add(entry.LegRank))
        {
            throw new InvalidOperationException($"Completed Color Cup team group contains duplicate rank {entry.LegRank}.");
        }

        if (!athleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Completed Color Cup team group contains duplicate athlete id {entry.AthleteId}.");
        }
    }

    private static void CheckLegTotals(
        TeamEvent.TeamLegRanked entry,
        Dictionary<int, TeamEvent.TeamLegTotals> totalsById,
        RulesV1 rules)
    {
        if (!totalsById.TryGetValue(entry.AthleteId, out TeamEvent.TeamLegTotals? accumulated))
        {
            throw new InvalidOperationException($"Completed Color Cup team standing for '{entry.Name}' has no accumulated totals.");
        }

        if (entry.LegScoreThousandths != accumulated.LegScoreThousandths)
        {
            throw new InvalidOperationException($"Completed Color Cup team score for '{entry.Name}' does not match accumulated round finals.");
        }

        if (entry.LegBaseThousandths != accumulated.LegBaseThousandths)
        {
            throw new InvalidOperationException($"Completed Color Cup team base score for '{entry.Name}' does not match accumulated round base points.");
        }

        if (entry.RoundPlaceCounts.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Completed Color Cup team counts for '{entry.Name}' must cover 8 positions.");
        }

        int sum = 0;
        foreach (int count in entry.RoundPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Completed Color Cup team counts for '{entry.Name}' cannot be negative.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum != rules.ColorCupTeamGroupRounds)
        {
            throw new InvalidOperationException($"Completed Color Cup team totals for '{entry.Name}' sum to {sum} round appearances, expected {rules.ColorCupTeamGroupRounds}.");
        }
    }

    private static void CheckTeamCounts(
        IReadOnlyList<TeamEvent.TeamRanked> ranked,
        IReadOnlyList<TeamEvent.TeamLegRanked> legs,
        RulesV1 rules)
    {
        if (ranked.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Completed Color Cup team championship must rank exactly {rules.ColorCupColorCount} teams, was {ranked.Count}.");
        }

        if (legs.Count != rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Completed Color Cup team championship must hold exactly {rules.ColorCupColorCount * rules.ColorCupTeamSize} legs, was {legs.Count}.");
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
            throw new InvalidOperationException($"Color Cup team '{team.TeamName}' has no legs.");
        }

        if (members.Count != rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup team '{team.TeamName}' must field exactly {rules.ColorCupTeamSize} legs, was {members.Count}.");
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
            throw new InvalidOperationException($"Color Cup team '{team.TeamName}' score does not equal the sum of its four legs.");
        }

        if (team.TeamBaseThousandths != expectedBase)
        {
            throw new InvalidOperationException($"Color Cup team '{team.TeamName}' base score does not equal the sum of its four legs.");
        }
    }

    private static void CheckTeamRanks(IReadOnlyList<TeamEvent.TeamRanked> ranked, RulesV1 rules)
    {
        HashSet<int> ranks = new();
        HashSet<int> teamIds = new();
        foreach (TeamEvent.TeamRanked team in ranked)
        {
            if (team.TeamRank < 1 || team.TeamRank > rules.ColorCupColorCount)
            {
                throw new InvalidOperationException($"Color Cup team rank {team.TeamRank} is out of range.");
            }

            if (!ranks.Add(team.TeamRank))
            {
                throw new InvalidOperationException($"Color Cup team championship contains duplicate rank {team.TeamRank}.");
            }

            if (!teamIds.Add(team.TeamId))
            {
                throw new InvalidOperationException($"Color Cup team championship contains duplicate team id {team.TeamId}.");
            }

            CheckTeamVectors(team, rules);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, rules.ColorCupColorCount)))
        {
            throw new InvalidOperationException("Color Cup team championship must cover ranks 1..8 exactly once.");
        }
    }

    private static void CheckTeamVectors(TeamEvent.TeamRanked team, RulesV1 rules)
    {
        if (team.GroupPlaceCounts.Count != rules.ColorCupColorCount || team.RoundPlaceCounts.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team '{team.TeamName}' has corrupt tie-break vectors.");
        }

        int groupSum = 0;
        foreach (int count in team.GroupPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Color Cup team '{team.TeamName}' has negative group counts.");
            }

            checked
            {
                groupSum += count;
            }
        }

        if (groupSum != rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup team '{team.TeamName}' group counts sum to {groupSum}, expected {rules.ColorCupTeamSize}.");
        }

        int roundSum = 0;
        foreach (int count in team.RoundPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Color Cup team '{team.TeamName}' has negative round counts.");
            }

            checked
            {
                roundSum += count;
            }
        }

        int expectedRounds = rules.ColorCupTeamSize * rules.ColorCupTeamGroupRounds;
        if (roundSum != expectedRounds)
        {
            throw new InvalidOperationException($"Color Cup team '{team.TeamName}' round counts sum to {roundSum}, expected {expectedRounds}.");
        }
    }

    private static void CheckPersistedSeasons(SeasonEntity source)
    {
        if (!source.IsComplete)
        {
            throw new InvalidOperationException($"Color Cup team source Season {source.SeasonNumber} must be complete.");
        }

        if (source.SeasonNumber % 2 != 1)
        {
            throw new InvalidOperationException($"Color Cup team source Season {source.SeasonNumber} must be odd.");
        }
    }

    private static void CheckPersistedCounts(
        IReadOnlyList<ColorCupTeamRoundEntity> rounds,
        IReadOnlyList<ColorCupTeamGroupStandingEntity> legs,
        IReadOnlyList<ColorCupTeamStandingEntity> teams,
        RulesV1 rules)
    {
        int expectedRounds = rules.ColorCupTeamSize * rules.ColorCupTeamGroupRounds;
        if (rounds.Count != expectedRounds)
        {
            throw new InvalidOperationException($"Color Cup team must persist exactly {expectedRounds} rounds, was {rounds.Count}.");
        }

        if (legs.Count != rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup team must persist exactly {rules.ColorCupColorCount * rules.ColorCupTeamSize} legs, was {legs.Count}.");
        }

        if (teams.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team must persist exactly {rules.ColorCupColorCount} teams, was {teams.Count}.");
        }
    }

    private static void CheckPersistedRounds(
        IReadOnlyList<ColorCupTeamRoundEntity> rounds,
        SeasonEntity source,
        RulesV1 rules)
    {
        HashSet<(int Group, int Round)> seen = new();
        foreach (ColorCupTeamRoundEntity round in rounds)
        {
            CheckSinglePersistedRound(round, source, rules, seen);
        }

        for (int group = 1; group <= rules.ColorCupTeamSize; group++)
        {
            for (int number = 1; number <= rules.ColorCupTeamGroupRounds; number++)
            {
                if (!seen.Contains((group, number)))
                {
                    throw new InvalidOperationException($"Color Cup team is missing group {group} round {number}.");
                }
            }
        }
    }

    private static void CheckSinglePersistedRound(
        ColorCupTeamRoundEntity round,
        SeasonEntity source,
        RulesV1 rules,
        HashSet<(int Group, int Round)> seen)
    {
        if (round.SourceSeasonId != source.Id || round.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Color Cup team round {round.Id} has corrupt source linkage.");
        }

        if (round.GroupNumber < 1 || round.GroupNumber > rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup team round group {round.GroupNumber} is out of range.");
        }

        if (round.RoundNumber < 1 || round.RoundNumber > rules.ColorCupTeamGroupRounds)
        {
            throw new InvalidOperationException($"Color Cup team round number {round.RoundNumber} is out of range.");
        }

        if (!seen.Add((round.GroupNumber, round.RoundNumber)))
        {
            throw new InvalidOperationException($"Color Cup team contains duplicate group {round.GroupNumber} round {round.RoundNumber}.");
        }

        if (round.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Color Cup team group {round.GroupNumber} round {round.RoundNumber} has corrupt rules version.");
        }

        if (string.IsNullOrWhiteSpace(round.PayloadJson) || string.IsNullOrWhiteSpace(round.PayloadChecksum))
        {
            throw new InvalidOperationException($"Color Cup team group {round.GroupNumber} round {round.RoundNumber} has an empty payload.");
        }

        ColorCupTeamRoundPayloadDocument document = ColorCupTeamRoundPayloadDocument.FromStored(round.PayloadJson);
        if (!string.Equals(document.Checksum, round.PayloadChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Color Cup team group {round.GroupNumber} round {round.RoundNumber} checksum does not match its payload.");
        }

        if (document.RoundNumber != round.RoundNumber || document.GroupNumber != round.GroupNumber || document.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Color Cup team group {round.GroupNumber} round {round.RoundNumber} payload identity is corrupt.");
        }
    }

    private static void CheckPersistedLegs(
        IReadOnlyList<ColorCupTeamGroupStandingEntity> legs,
        SeasonEntity source,
        RulesV1 rules)
    {
        HashSet<int> athletes = new();
        Dictionary<int, HashSet<int>> ranksByGroup = new();
        foreach (ColorCupTeamGroupStandingEntity leg in legs)
        {
            CheckSinglePersistedLeg(leg, source, rules, athletes, ranksByGroup);
        }

        foreach (int group in Enumerable.Range(1, rules.ColorCupTeamSize))
        {
            if (!ranksByGroup.TryGetValue(group, out HashSet<int>? ranks) ||
                !ranks.SetEquals(Enumerable.Range(1, rules.ColorCupColorCount)))
            {
                throw new InvalidOperationException($"Color Cup team group {group} must cover ranks 1..{rules.ColorCupColorCount} exactly once.");
            }
        }
    }

    private static void CheckSinglePersistedLeg(
        ColorCupTeamGroupStandingEntity leg,
        SeasonEntity source,
        RulesV1 rules,
        HashSet<int> athletes,
        Dictionary<int, HashSet<int>> ranksByGroup)
    {
        if (leg.SourceSeasonId != source.Id || leg.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Color Cup team leg {leg.Id} has corrupt source linkage.");
        }

        if (leg.GroupNumber < 1 || leg.GroupNumber > rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup team leg group {leg.GroupNumber} is out of range.");
        }

        if (leg.SelectionRank != leg.GroupNumber)
        {
            throw new InvalidOperationException($"Color Cup team leg {leg.Id} selection rank #{leg.SelectionRank} does not match group {leg.GroupNumber}.");
        }

        if (leg.GroupRank < 1 || leg.GroupRank > rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team leg group rank {leg.GroupRank} is out of range.");
        }

        if (!athletes.Add(leg.SaveAthleteId))
        {
            throw new InvalidOperationException($"Color Cup team contains duplicate athlete id {leg.SaveAthleteId}.");
        }

        if (!ranksByGroup.TryGetValue(leg.GroupNumber, out HashSet<int>? ranks))
        {
            ranks = new HashSet<int>();
            ranksByGroup[leg.GroupNumber] = ranks;
        }

        if (!ranks.Add(leg.GroupRank))
        {
            throw new InvalidOperationException($"Color Cup team group {leg.GroupNumber} contains duplicate rank {leg.GroupRank}.");
        }

        CheckLegPlaceCounts(leg, rules);
    }

    private static void CheckLegPlaceCounts(ColorCupTeamGroupStandingEntity leg, RulesV1 rules)
    {
        List<int>? counts = JsonSerializer.Deserialize<List<int>>(leg.RoundPlaceCountsJson);
        if (counts is null || counts.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team leg {leg.Id} has corrupt round-place counts.");
        }

        int sum = 0;
        foreach (int count in counts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Color Cup team leg {leg.Id} has negative placement counts.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum != rules.ColorCupTeamGroupRounds)
        {
            throw new InvalidOperationException($"Color Cup team leg {leg.Id} sums to {sum} rounds, expected {rules.ColorCupTeamGroupRounds}.");
        }
    }

    private static void CheckPersistedTeams(
        IReadOnlyList<ColorCupTeamStandingEntity> teams,
        SeasonEntity source,
        RulesV1 rules)
    {
        HashSet<int> ranks = new();
        HashSet<int> colors = new();
        foreach (ColorCupTeamStandingEntity team in teams)
        {
            CheckSinglePersistedTeam(team, source, rules, ranks, colors);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, rules.ColorCupColorCount)))
        {
            throw new InvalidOperationException("Color Cup team championship must cover ranks 1..8 exactly once.");
        }
    }

    private static void CheckSinglePersistedTeam(
        ColorCupTeamStandingEntity team,
        SeasonEntity source,
        RulesV1 rules,
        HashSet<int> ranks,
        HashSet<int> colors)
    {
        if (team.SourceSeasonId != source.Id || team.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Color Cup team {team.Id} has corrupt source linkage.");
        }

        if (team.TeamRank < 1 || team.TeamRank > rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team rank {team.TeamRank} is out of range.");
        }

        if (!ranks.Add(team.TeamRank))
        {
            throw new InvalidOperationException($"Color Cup team championship contains duplicate rank {team.TeamRank}.");
        }

        if (!colors.Add(team.SportingColor))
        {
            throw new InvalidOperationException($"Color Cup team championship contains duplicate color {team.SportingColor}.");
        }

        int expectedMedal = team.TeamRank switch
        {
            1 => (int)ColorCupMedal.Gold,
            2 => (int)ColorCupMedal.Silver,
            3 => (int)ColorCupMedal.Bronze,
            _ => (int)ColorCupMedal.None,
        };
        if (team.Medal != expectedMedal)
        {
            throw new InvalidOperationException($"Color Cup team rank {team.TeamRank} has corrupt medal {team.Medal}.");
        }

        CheckTeamCountJson(team, rules);
    }

    private static void CheckTeamCountJson(ColorCupTeamStandingEntity team, RulesV1 rules)
    {
        List<int>? groupCounts = JsonSerializer.Deserialize<List<int>>(team.GroupPlaceCountsJson);
        List<int>? roundCounts = JsonSerializer.Deserialize<List<int>>(team.RoundPlaceCountsJson);
        if (groupCounts is null || groupCounts.Count != rules.ColorCupColorCount ||
            roundCounts is null || roundCounts.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException($"Color Cup team {team.Id} has corrupt tie-break counts.");
        }

        int groupSum = 0;
        foreach (int count in groupCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Color Cup team {team.Id} has negative group counts.");
            }

            checked
            {
                groupSum += count;
            }
        }

        if (groupSum != rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup team {team.Id} group counts sum to {groupSum}, expected {rules.ColorCupTeamSize}.");
        }

        int roundSum = 0;
        foreach (int count in roundCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Color Cup team {team.Id} has negative round counts.");
            }

            checked
            {
                roundSum += count;
            }
        }

        int expectedRounds = rules.ColorCupTeamSize * rules.ColorCupTeamGroupRounds;
        if (roundSum != expectedRounds)
        {
            throw new InvalidOperationException($"Color Cup team {team.Id} round counts sum to {roundSum}, expected {expectedRounds}.");
        }
    }

    private static void CheckPersistedHonour(
        SeasonEntity source,
        IReadOnlyList<ColorCupTeamGroupStandingEntity> legs,
        IReadOnlyList<ColorCupTeamStandingEntity> teams,
        IReadOnlyList<HonourEntity> honours)
    {
        List<HonourEntity> teamHonours = honours
            .Where(h => h.SeasonId == source.Id && (h.Kind == (int)Features.Records.HonourKind.ColorCupTeamChampion
                || h.Kind == (int)Features.Records.HonourKind.ColorCupTeamRunnerUp
                || h.Kind == (int)Features.Records.HonourKind.ColorCupTeamThirdPlace))
            .ToList();
        if (teamHonours.Count != 12)
        {
            throw new InvalidOperationException(
                $"Color Cup team for Season {source.SeasonNumber} must persist exactly twelve team podium honours, was {teamHonours.Count}.");
        }

        foreach (int rank in new[] { 1, 2, 3 })
        {
            Features.Records.HonourKind expectedKind = Features.Records.HonourKindMapper.FromColorCupTeamRank(rank);
            ColorCupTeamStandingEntity team = teams.Single(s => s.TeamRank == rank);
            HashSet<int> teamAthletes = legs
                .Where(l => l.SportingColor == team.SportingColor)
                .Select(l => l.SaveAthleteId)
                .ToHashSet();
            if (teamAthletes.Count != 4)
            {
                throw new InvalidOperationException($"Color Cup team rank {rank} must field exactly four legs.");
            }

            List<HonourEntity> rankHonours = teamHonours.Where(h => h.Kind == (int)expectedKind).ToList();
            if (rankHonours.Count != 4)
            {
                throw new InvalidOperationException($"Color Cup team rank {rank} must persist exactly four podium honours, was {rankHonours.Count}.");
            }

            HashSet<int> honourAthletes = new();
            foreach (HonourEntity honour in rankHonours)
            {
                if (honour.SeasonNumber != source.SeasonNumber)
                {
                    throw new InvalidOperationException("Color Cup team podium honour has corrupt season linkage.");
                }

                if (!teamAthletes.Contains(honour.SaveAthleteId))
                {
                    throw new InvalidOperationException($"Color Cup team rank-{rank} podium honour does not match a rank-{rank} team leg.");
                }

                if (!honourAthletes.Add(honour.SaveAthleteId))
                {
                    throw new InvalidOperationException($"Color Cup team rank-{rank} podium honours must cover four distinct athletes.");
                }
            }
        }
    }
}
