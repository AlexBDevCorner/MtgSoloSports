using System.Text.Json;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Superleague.RunQualifier;

/// <summary>
/// Structural invariants for the 32-athlete Superleague qualifier (MSS-016).
/// Fundamental failures throw and abort the mutation; corrupted sporting state
/// is never silently repaired. The qualifier reuses pure round/stage scoring
/// primitives but never creates league <c>StageStanding</c>, <c>SeasonStanding</c>
/// or <c>Round</c> rows, so normal league championship totals are untouched.
/// </summary>
public static class QualifierInvariants
{
    /// <summary>
    /// Validates the 32-athlete field before simulation: exactly 8 incumbents
    /// from Superleague ranks 17-24 plus 24 challengers (3 per feeder from ranks
    /// 2-4), all distinct athletes, correct rank bands and standings linkage.
    /// </summary>
    public static void ValidateField(
        QualifierFieldSelection.QualifierField field,
        IReadOnlyList<SeasonStandingEntity> superleagueStandings,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandingsByLeague,
        IReadOnlyList<LeagueEntity> feederLeagues,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(superleagueStandings);
        ArgumentNullException.ThrowIfNull(feederStandingsByLeague);
        ArgumentNullException.ThrowIfNull(feederLeagues);
        ArgumentNullException.ThrowIfNull(superleague);
        ArgumentNullException.ThrowIfNull(rules);
        CheckFieldCounts(field, rules);
        CheckFieldUniqueness(field);
        CheckIncumbentBands(field, superleague, rules);
        CheckChallengerBands(field, feederLeagues, rules);
        CheckRanksMatchStandings(field, superleagueStandings, feederStandingsByLeague);
    }

    /// <summary>
    /// Validates one freshly simulated qualifier round before commit: exact 32
    /// placements, positions 1..32 exactly once, unique athletes, base points
    /// matching the snapshot scoring table, final points matching base plus
    /// stage-start active bonus, cumulative totals chaining and RNG advancement.
    /// </summary>
    public static void ValidateRound(
        QualifierRoundPayloadDocument payload,
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
    /// Validates a freshly completed qualifier stage before commit: exactly 32
    /// ranked athletes, ranks 1..32 exactly once, scores chaining from round
    /// payloads and round-place counts summing to 16 per athlete.
    /// Championship/earned values from the pure kernel are intentionally not
    /// validated here: the qualifier persists no championship points and no
    /// new career bonus.
    /// </summary>
    public static void ValidateCompletedQualifier(
        IReadOnlyList<StageRankedAthlete> ranked,
        IReadOnlyList<StageAthleteTotals> totals,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(rules);
        CheckCompletedCounts(ranked, totals, rules);
        CheckCompletedStandings(ranked, totals, rules);
    }

    /// <summary>
    /// Validates persisted qualifier state: 16 round rows plus 32 standing rows
    /// for the transition, ranks 1..32 exactly once, exactly 8 qualified
    /// athletes, no duplicate athletes and no league history rewritten.
    /// </summary>
    public static void ValidatePersisted(
        SeasonEntity source,
        SeasonEntity next,
        IReadOnlyList<QualifierRoundEntity> rounds,
        IReadOnlyList<QualifierStandingEntity> standings,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(rules);
        CheckPersistedSeasons(source, next);
        CheckPersistedCounts(rounds, standings, rules);
        CheckPersistedRounds(rounds, source, next, rules);
        CheckPersistedStandings(standings, source, next, rules);
    }

    private static void CheckFieldCounts(QualifierFieldSelection.QualifierField field, RulesV1 rules)
    {
        if (field.Incumbents.Count != rules.SuperleagueQualifierIncumbentCount)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly {rules.SuperleagueQualifierIncumbentCount} incumbents, was {field.Incumbents.Count}.");
        }

        if (field.Challengers.Count != rules.FeederQualifierCount)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly {rules.FeederQualifierCount} challengers, was {field.Challengers.Count}.");
        }

        if (field.All.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly {rules.QualifierSize} athletes, was {field.All.Count}.");
        }
    }

    private static void CheckFieldUniqueness(QualifierFieldSelection.QualifierField field)
    {
        HashSet<int> athleteIds = new();
        foreach (QualifierFieldSelection.QualifierPick pick in field.All)
        {
            if (pick.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Qualifier contains invalid athlete id {pick.SaveAthleteId}.");
            }

            if (string.IsNullOrWhiteSpace(pick.Name))
            {
                throw new InvalidOperationException($"Qualifier athlete {pick.SaveAthleteId} has an empty name.");
            }

            if (!athleteIds.Add(pick.SaveAthleteId))
            {
                throw new InvalidOperationException($"Qualifier contains duplicate athlete id {pick.SaveAthleteId}.");
            }
        }
    }

    private static void CheckRoundIdentity(QualifierRoundPayloadDocument payload, RulesV1 rules)
    {
        if (payload.Version != QualifierRoundPayloadDocument.PayloadVersion)
        {
            throw new InvalidOperationException($"Qualifier payload version must be {QualifierRoundPayloadDocument.PayloadVersion}, was {payload.Version}.");
        }

        if (payload.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Qualifier payload rules version must be {rules.Version}, was {payload.RulesVersion}.");
        }

        if (payload.FromSeasonNumber < 2)
        {
            throw new InvalidOperationException($"Qualifier from-season must be >= 2, was {payload.FromSeasonNumber}.");
        }

        if (payload.ToSeasonNumber != payload.FromSeasonNumber + 1)
        {
            throw new InvalidOperationException("Qualifier seasons must be consecutive.");
        }

        if (payload.RoundNumber < 1 || payload.RoundNumber > rules.QualifierRounds)
        {
            throw new InvalidOperationException($"Qualifier round must be 1..{rules.QualifierRounds}, was {payload.RoundNumber}.");
        }

        if (payload.Placements.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException($"Qualifier round must contain exactly {rules.QualifierSize} placements, was {payload.Placements.Count}.");
        }
    }

    private static void CheckRoundRng(QualifierRoundPayloadDocument payload, ulong rngBeforeState, ulong rngBeforeStream)
    {
        if (payload.RngBeforeState != rngBeforeState || payload.RngBeforeStream != rngBeforeStream)
        {
            throw new InvalidOperationException("Qualifier payload RNG-before does not match the persisted save RNG chain.");
        }

        if (payload.RngAfterState == rngBeforeState && payload.RngAfterStream == rngBeforeStream)
        {
            throw new InvalidOperationException("Qualifier round simulation must advance the save RNG.");
        }
    }

    private static void CheckCompletedCounts(
        IReadOnlyList<StageRankedAthlete> ranked,
        IReadOnlyList<StageAthleteTotals> totals,
        RulesV1 rules)
    {
        if (ranked.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException(
                $"Completed qualifier must rank exactly {rules.QualifierSize} athletes, was {ranked.Count}.");
        }

        if (totals.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException(
                $"Completed qualifier must total exactly {rules.QualifierSize} athletes, was {totals.Count}.");
        }
    }

    private static void CheckCompletedStandings(
        IReadOnlyList<StageRankedAthlete> ranked,
        IReadOnlyList<StageAthleteTotals> totals,
        RulesV1 rules)
    {
        Dictionary<int, StageAthleteTotals> totalsById = totals.ToDictionary(t => t.AthleteId);
        CompletedAccumulator accumulator = new();
        foreach (StageRankedAthlete entry in ranked)
        {
            CheckCompletedIdentity(entry, accumulator, rules);
            CheckCompletedTotals(entry, totalsById, rules);
        }

        if (!accumulator.Ranks.SetEquals(Enumerable.Range(1, rules.QualifierSize)))
        {
            throw new InvalidOperationException("Completed qualifier must cover ranks 1..32 exactly once.");
        }
    }

    private sealed class CompletedAccumulator
    {
        public HashSet<int> Ranks { get; } = new();

        public HashSet<int> AthleteIds { get; } = new();

        public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
    }

    private static void CheckCompletedIdentity(StageRankedAthlete entry, CompletedAccumulator accumulator, RulesV1 rules)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Completed qualifier contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Completed qualifier contains an athlete with an empty name.");
        }

        if (entry.StageRank < 1 || entry.StageRank > rules.QualifierSize)
        {
            throw new InvalidOperationException($"Completed qualifier rank {entry.StageRank} is out of range.");
        }

        if (!accumulator.Ranks.Add(entry.StageRank))
        {
            throw new InvalidOperationException($"Completed qualifier contains duplicate rank {entry.StageRank}.");
        }

        if (!accumulator.AthleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Completed qualifier contains duplicate athlete id {entry.AthleteId}.");
        }

        if (!accumulator.Names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Completed qualifier contains duplicate athlete '{entry.Name}'.");
        }
    }

    private static void CheckCompletedTotals(
        StageRankedAthlete entry,
        Dictionary<int, StageAthleteTotals> totalsById,
        RulesV1 rules)
    {
        if (!totalsById.TryGetValue(entry.AthleteId, out StageAthleteTotals? accumulated))
        {
            throw new InvalidOperationException($"Completed qualifier standing for '{entry.Name}' has no accumulated totals.");
        }

        if (!string.Equals(accumulated.Name, entry.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Completed qualifier standing for id {entry.AthleteId} has inconsistent names.");
        }

        if (entry.StageScoreThousandths != accumulated.StageScoreThousandths)
        {
            throw new InvalidOperationException($"Completed qualifier score for '{entry.Name}' does not match accumulated round finals.");
        }

        if (entry.BaseScoreThousandths != accumulated.BaseScoreThousandths)
        {
            throw new InvalidOperationException($"Completed qualifier base score for '{entry.Name}' does not match accumulated round base points.");
        }

        CheckCompletedCountsRow(entry, rules);
    }

    private static void CheckCompletedCountsRow(StageRankedAthlete entry, RulesV1 rules)
    {
        if (entry.RoundPlaceCounts.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException($"Completed qualifier round-place counts for '{entry.Name}' must cover 32 positions.");
        }

        int sum = 0;
        foreach (int count in entry.RoundPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Completed qualifier round-place counts for '{entry.Name}' cannot be negative.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum != rules.QualifierRounds)
        {
            throw new InvalidOperationException(
                $"Completed qualifier totals for '{entry.Name}' sum to {sum} round appearances, expected {rules.QualifierRounds}.");
        }
    }

    private static void CheckPersistedSeasons(SeasonEntity source, SeasonEntity next)
    {
        if (!source.HasSuperleague || !source.IsComplete)
        {
            throw new InvalidOperationException("Qualifier requires a completed source season with a Superleague.");
        }

        if (next.SeasonNumber != source.SeasonNumber + 1 || !next.HasSuperleague || next.IsComplete)
        {
            throw new InvalidOperationException("Qualifier next season must be the consecutive incomplete Superleague season.");
        }
    }

    private static void CheckPersistedCounts(
        IReadOnlyList<QualifierRoundEntity> rounds,
        IReadOnlyList<QualifierStandingEntity> standings,
        RulesV1 rules)
    {
        if (rounds.Count != rules.QualifierRounds)
        {
            throw new InvalidOperationException(
                $"Qualifier must persist exactly {rules.QualifierRounds} rounds, was {rounds.Count}.");
        }

        if (standings.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException(
                $"Qualifier must persist exactly {rules.QualifierSize} standings, was {standings.Count}.");
        }
    }

    private static void CheckPersistedRounds(
        IReadOnlyList<QualifierRoundEntity> rounds,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules)
    {
        HashSet<int> seenRounds = new();
        foreach (QualifierRoundEntity round in rounds)
        {
            CheckSinglePersistedRound(round, source, next, rules, seenRounds);
        }

        if (!seenRounds.SetEquals(Enumerable.Range(1, rules.QualifierRounds)))
        {
            throw new InvalidOperationException("Qualifier must cover rounds 1..16 exactly once.");
        }
    }

    private static void CheckSinglePersistedRound(
        QualifierRoundEntity round,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        HashSet<int> seenRounds)
    {
        if (round.FromSeasonId != source.Id || round.ToSeasonId != next.Id)
        {
            throw new InvalidOperationException($"Qualifier round {round.Id} has corrupt season linkage.");
        }

        if (round.RoundNumber < 1 || round.RoundNumber > rules.QualifierRounds)
        {
            throw new InvalidOperationException($"Qualifier round number {round.RoundNumber} is out of range.");
        }

        if (!seenRounds.Add(round.RoundNumber))
        {
            throw new InvalidOperationException($"Qualifier contains duplicate round {round.RoundNumber}.");
        }

        if (round.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Qualifier round {round.RoundNumber} has corrupt rules version.");
        }

        if (string.IsNullOrWhiteSpace(round.PayloadJson) || string.IsNullOrWhiteSpace(round.PayloadChecksum))
        {
            throw new InvalidOperationException($"Qualifier round {round.RoundNumber} has an empty payload.");
        }

        QualifierRoundPayloadDocument document = QualifierRoundPayloadDocument.FromJson(round.PayloadJson);
        if (!string.Equals(document.Checksum, round.PayloadChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Qualifier round {round.RoundNumber} checksum does not match its payload.");
        }

        if (document.RoundNumber != round.RoundNumber)
        {
            throw new InvalidOperationException($"Qualifier round {round.RoundNumber} payload identity is corrupt.");
        }
    }

    private static void CheckPersistedStandings(
        IReadOnlyList<QualifierStandingEntity> standings,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules)
    {
        HashSet<int> ranks = new();
        HashSet<int> athleteIds = new();
        int qualified = 0;
        foreach (QualifierStandingEntity standing in standings)
        {
            qualified += CheckSinglePersistedStanding(standing, source, next, rules, ranks, athleteIds) ? 1 : 0;
        }

        if (!ranks.SetEquals(Enumerable.Range(1, rules.QualifierSize)))
        {
            throw new InvalidOperationException("Qualifier must cover ranks 1..32 exactly once.");
        }

        if (qualified != rules.QualifierWinners)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly {rules.QualifierWinners} successful qualifiers, was {qualified}.");
        }

        CheckPersistedRoles(standings, rules);
    }

    private static bool CheckSinglePersistedStanding(
        QualifierStandingEntity standing,
        SeasonEntity source,
        SeasonEntity next,
        RulesV1 rules,
        HashSet<int> ranks,
        HashSet<int> athleteIds)
    {
        if (standing.FromSeasonId != source.Id || standing.ToSeasonId != next.Id)
        {
            throw new InvalidOperationException($"Qualifier standing {standing.Id} has corrupt season linkage.");
        }

        if (standing.QualifierRank < 1 || standing.QualifierRank > rules.QualifierSize)
        {
            throw new InvalidOperationException($"Qualifier rank {standing.QualifierRank} is out of range.");
        }

        if (!ranks.Add(standing.QualifierRank))
        {
            throw new InvalidOperationException($"Qualifier contains duplicate rank {standing.QualifierRank}.");
        }

        if (!athleteIds.Add(standing.SaveAthleteId))
        {
            throw new InvalidOperationException($"Qualifier contains duplicate athlete id {standing.SaveAthleteId}.");
        }

        bool expectedQualified = standing.QualifierRank <= rules.QualifierWinners;
        if (standing.IsQualified != expectedQualified)
        {
            throw new InvalidOperationException($"Qualifier rank {standing.QualifierRank} has corrupt qualified flag.");
        }

        if (!Enum.IsDefined(typeof(QualifierRole), standing.Role))
        {
            throw new InvalidOperationException($"Qualifier standing {standing.Id} has unexpected role {standing.Role}.");
        }

        if (standing.FromLeagueId <= 0 || standing.FromSeasonRank < 1 || standing.FromSeasonRank > rules.LeagueSize)
        {
            throw new InvalidOperationException($"Qualifier standing {standing.Id} has corrupt source provenance.");
        }

        CheckPersistedPlaceCounts(standing, rules);
        return standing.IsQualified;
    }

    private static void CheckPersistedPlaceCounts(QualifierStandingEntity standing, RulesV1 rules)
    {
        List<int>? counts = JsonSerializer.Deserialize<List<int>>(standing.RoundPlaceCountsJson);
        if (counts is null || counts.Count != rules.QualifierSize)
        {
            throw new InvalidOperationException($"Qualifier standing {standing.Id} has corrupt round-place counts.");
        }

        int sum = 0;
        foreach (int count in counts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Qualifier standing {standing.Id} has negative placement counts.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum != rules.QualifierRounds)
        {
            throw new InvalidOperationException($"Qualifier standing {standing.Id} sums to {sum} rounds, expected {rules.QualifierRounds}.");
        }
    }

    private static void CheckPersistedRoles(IReadOnlyList<QualifierStandingEntity> standings, RulesV1 rules)
    {
        int incumbents = standings.Count(s => s.Role == (int)QualifierRole.Incumbent);
        int challengers = standings.Count(s => s.Role == (int)QualifierRole.Challenger);
        if (incumbents != rules.SuperleagueQualifierIncumbentCount)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly {rules.SuperleagueQualifierIncumbentCount} incumbents, was {incumbents}.");
        }

        if (challengers != rules.FeederQualifierCount)
        {
            throw new InvalidOperationException(
                $"Qualifier must hold exactly {rules.FeederQualifierCount} challengers, was {challengers}.");
        }
    }

    private static void CheckIncumbentBands(
        QualifierFieldSelection.QualifierField field,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        int first = rules.SuperleagueSafeCount + 1;
        int last = rules.SuperleagueSafeCount + rules.SuperleagueQualifierIncumbentCount;
        foreach (QualifierFieldSelection.QualifierPick pick in field.Incumbents)
        {
            CheckSingleIncumbent(pick, superleague, first, last);
        }
    }

    private static void CheckSingleIncumbent(
        QualifierFieldSelection.QualifierPick pick,
        LeagueEntity superleague,
        int first,
        int last)
    {
        if (pick.FromLeagueId != superleague.Id)
        {
            throw new InvalidOperationException($"Qualifier incumbent {pick.SaveAthleteId} must come from the Superleague.");
        }

        if (pick.FromSeasonRank < first || pick.FromSeasonRank > last)
        {
            throw new InvalidOperationException(
                $"Qualifier incumbent rank {pick.FromSeasonRank} is outside places {first}-{last}.");
        }

        if (pick.Role != QualifierRole.Incumbent)
        {
            throw new InvalidOperationException($"Qualifier incumbent {pick.SaveAthleteId} has corrupt role {pick.Role}.");
        }
    }

    private static void CheckChallengerBands(
        QualifierFieldSelection.QualifierField field,
        IReadOnlyList<LeagueEntity> feederLeagues,
        RulesV1 rules)
    {
        HashSet<int> feederIds = feederLeagues.Select(l => l.Id).ToHashSet();
        Dictionary<int, int> challengersPerLeague = field.Challengers
            .GroupBy(p => p.FromLeagueId)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (LeagueEntity league in feederLeagues)
        {
            if (!challengersPerLeague.TryGetValue(league.Id, out int count) || count != 3)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must contribute exactly three qualifier challengers, was {count}.");
            }
        }

        foreach (QualifierFieldSelection.QualifierPick pick in field.Challengers)
        {
            CheckSingleChallenger(pick, feederIds);
        }
    }

    private static void CheckSingleChallenger(
        QualifierFieldSelection.QualifierPick pick,
        HashSet<int> feederIds)
    {
        if (!feederIds.Contains(pick.FromLeagueId))
        {
            throw new InvalidOperationException($"Qualifier challenger {pick.SaveAthleteId} references unknown league {pick.FromLeagueId}.");
        }

        if (pick.FromSeasonRank is < 2 or > 4)
        {
            throw new InvalidOperationException(
                $"Qualifier challenger rank {pick.FromSeasonRank} is outside places 2-4.");
        }

        if (pick.Role != QualifierRole.Challenger)
        {
            throw new InvalidOperationException($"Qualifier challenger {pick.SaveAthleteId} has corrupt role {pick.Role}.");
        }
    }

    private static void CheckRanksMatchStandings(
        QualifierFieldSelection.QualifierField field,
        IReadOnlyList<SeasonStandingEntity> superleagueStandings,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandingsByLeague)
    {
        Dictionary<int, int> superRankByAthlete = superleagueStandings.ToDictionary(r => r.SaveAthleteId, r => r.SeasonRank);
        Dictionary<int, int> feederRankByAthlete = BuildFeederRankMap(feederStandingsByLeague);
        foreach (QualifierFieldSelection.QualifierPick pick in field.All)
        {
            CheckSingleRank(pick, superRankByAthlete, feederRankByAthlete);
        }
    }

    private static Dictionary<int, int> BuildFeederRankMap(
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandingsByLeague)
    {
        Dictionary<int, int> feederRankByAthlete = new();
        foreach ((int _, IReadOnlyList<SeasonStandingEntity> rows) in feederStandingsByLeague)
        {
            foreach (SeasonStandingEntity row in rows)
            {
                feederRankByAthlete[row.SaveAthleteId] = row.SeasonRank;
            }
        }

        return feederRankByAthlete;
    }

    private static void CheckSingleRank(
        QualifierFieldSelection.QualifierPick pick,
        Dictionary<int, int> superRankByAthlete,
        Dictionary<int, int> feederRankByAthlete)
    {
        bool inSuper = superRankByAthlete.TryGetValue(pick.SaveAthleteId, out int superRank);
        bool inFeeder = feederRankByAthlete.TryGetValue(pick.SaveAthleteId, out int feederRank);
        if (inSuper == inFeeder)
        {
            throw new InvalidOperationException(
                $"Athlete {pick.SaveAthleteId} must appear in exactly one source table.");
        }

        int actual = inSuper ? superRank : feederRank;
        if (actual != pick.FromSeasonRank)
        {
            throw new InvalidOperationException(
                $"Athlete {pick.SaveAthleteId} rank {pick.FromSeasonRank} does not match final standing rank {actual}.");
        }
    }

    private static void EnsurePlacements(QualifierRoundPayloadDocument payload, RulesV1 rules)
    {
        PlacementAccumulator accumulator = new();
        foreach (RoundPayloadEntry entry in payload.Placements)
        {
            CheckPlacementIdentity(entry, accumulator);
            CheckPlacementPoints(entry, rules);
            CheckPlacementRanks(entry, rules);
        }

        if (!accumulator.Positions.SetEquals(Enumerable.Range(1, rules.QualifierSize)))
        {
            throw new InvalidOperationException("Qualifier payload must cover positions 1..32 exactly once.");
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
            throw new InvalidOperationException($"Qualifier payload contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Qualifier payload contains an athlete with an empty name.");
        }

        if (!accumulator.Positions.Add(entry.Position))
        {
            throw new InvalidOperationException($"Qualifier payload contains duplicate position {entry.Position}.");
        }

        if (!accumulator.AthleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Qualifier payload contains duplicate athlete id {entry.AthleteId}.");
        }

        if (!accumulator.Names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Qualifier payload contains duplicate athlete '{entry.Name}'.");
        }
    }

    private static void CheckPlacementPoints(RoundPayloadEntry entry, RulesV1 rules)
    {
        if (entry.Position < 1 || entry.Position > rules.QualifierSize)
        {
            throw new InvalidOperationException($"Qualifier payload position {entry.Position} is out of range.");
        }

        int expectedBase = rules.ScoringTable[entry.Position - 1] * RulesV1.FixedScale;
        if (entry.BaseThousandths != expectedBase)
        {
            throw new InvalidOperationException($"Qualifier payload base points for position {entry.Position} must be {expectedBase}, was {entry.BaseThousandths}.");
        }

        if (entry.ActiveBonusThousandths < 0)
        {
            throw new InvalidOperationException($"Qualifier payload active bonus for '{entry.Name}' cannot be negative.");
        }

        long expectedFinal = (long)entry.BaseThousandths * (RulesV1.BonusPercentScale + entry.ActiveBonusThousandths) / RulesV1.BonusPercentScale;
        if (entry.FinalThousandths != expectedFinal)
        {
            throw new InvalidOperationException($"Qualifier payload final points for '{entry.Name}' must be {expectedFinal}, was {entry.FinalThousandths}.");
        }

        long expectedAfter = (long)entry.CumulativeBeforeThousandths + entry.FinalThousandths;
        if (entry.CumulativeAfterThousandths != expectedAfter)
        {
            throw new InvalidOperationException($"Qualifier payload cumulative total for '{entry.Name}' is corrupt.");
        }
    }

    private static void CheckPlacementRanks(RoundPayloadEntry entry, RulesV1 rules)
    {
        if (entry.RankBefore < 1 || entry.RankBefore > rules.QualifierSize)
        {
            throw new InvalidOperationException($"Qualifier payload rank-before for '{entry.Name}' is out of range.");
        }

        if (entry.RankAfter < 1 || entry.RankAfter > rules.QualifierSize)
        {
            throw new InvalidOperationException($"Qualifier payload rank-after for '{entry.Name}' is out of range.");
        }

        if (entry.RankMovement != entry.RankBefore - entry.RankAfter)
        {
            throw new InvalidOperationException($"Qualifier payload rank movement for '{entry.Name}' is corrupt.");
        }
    }

    private static void EnsureRankConsistency(QualifierRoundPayloadDocument payload)
    {
        List<RoundPayloadEntry> byBefore = payload.Placements.OrderBy(e => e.RankBefore).ToList();
        List<RoundPayloadEntry> byAfter = payload.Placements.OrderBy(e => e.RankAfter).ToList();
        CheckRankSequences(byBefore, byAfter);
        CheckRankOrders(byBefore, byAfter);
    }

    private static void CheckRankSequences(List<RoundPayloadEntry> byBefore, List<RoundPayloadEntry> byAfter)
    {
        for (int i = 0; i < byBefore.Count; i++)
        {
            if (byBefore[i].RankBefore != i + 1)
            {
                throw new InvalidOperationException("Qualifier payload rank-before sequence is corrupt.");
            }
        }

        for (int i = 0; i < byAfter.Count; i++)
        {
            if (byAfter[i].RankAfter != i + 1)
            {
                throw new InvalidOperationException("Qualifier payload rank-after sequence is corrupt.");
            }
        }
    }

    private static void CheckRankOrders(List<RoundPayloadEntry> byBefore, List<RoundPayloadEntry> byAfter)
    {
        for (int i = 1; i < byBefore.Count; i++)
        {
            bool ordered = byBefore[i - 1].CumulativeBeforeThousandths > byBefore[i].CumulativeBeforeThousandths ||
                (byBefore[i - 1].CumulativeBeforeThousandths == byBefore[i].CumulativeBeforeThousandths &&
                 string.Compare(byBefore[i - 1].Name, byBefore[i].Name, StringComparison.Ordinal) < 0);
            if (!ordered)
            {
                throw new InvalidOperationException("Qualifier payload rank-before order contradicts cumulative-before totals.");
            }
        }

        for (int i = 1; i < byAfter.Count; i++)
        {
            bool ordered = byAfter[i - 1].CumulativeAfterThousandths > byAfter[i].CumulativeAfterThousandths ||
                (byAfter[i - 1].CumulativeAfterThousandths == byAfter[i].CumulativeAfterThousandths &&
                 string.Compare(byAfter[i - 1].Name, byAfter[i].Name, StringComparison.Ordinal) < 0);
            if (!ordered)
            {
                throw new InvalidOperationException("Qualifier payload rank-after order contradicts cumulative-after totals.");
            }
        }
    }
}
