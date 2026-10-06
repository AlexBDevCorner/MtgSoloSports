using System.Text.Json;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Structural invariants for 16-athlete feeder qualifiers (MSS-058).
/// Fundamental failures throw and abort the mutation; corrupted sporting state
/// is never silently repaired. Mirrors the Superleague qualifier invariants
/// but with field size 16, winners 8, rounds 16, and per-color boundary checks.
/// No new bonus or championship points are generated; active bonus applies.
/// </summary>
public static class FeederQualifierInvariants
{
    public static void ValidateField(
        FeederQualifierFieldSelection.FeederField field,
        IReadOnlyList<SeasonStandingEntity> upperStandings,
        IReadOnlyList<SeasonStandingEntity> lowerStandings,
        LeagueEntity upperLeague,
        LeagueEntity lowerLeague,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(upperStandings);
        ArgumentNullException.ThrowIfNull(lowerStandings);
        ArgumentNullException.ThrowIfNull(upperLeague);
        ArgumentNullException.ThrowIfNull(lowerLeague);
        ArgumentNullException.ThrowIfNull(rules);

        CheckFieldCounts(field, rules);
        CheckFieldUniqueness(field);
        CheckIncumbentBands(field, upperLeague);
        CheckChallengerBands(field, lowerLeague);
        CheckFieldRanksMatch(field, upperStandings, lowerStandings);
    }

    private static void CheckFieldCounts(
        FeederQualifierFieldSelection.FeederField field,
        RulesV1 rules)
    {
        if (field.Incumbents.Count != rules.FeederQualifierIncumbentPerColor)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier must hold exactly {rules.FeederQualifierIncumbentPerColor} incumbents, was {field.Incumbents.Count}.");
        }

        if (field.Challengers.Count != rules.FeederQualifierChallengerPerColor)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier must hold exactly {rules.FeederQualifierChallengerPerColor} challengers, was {field.Challengers.Count}.");
        }

        if (field.All.Count != rules.FeederQualifierSize)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier must hold exactly {rules.FeederQualifierSize} athletes, was {field.All.Count}.");
        }
    }

    private static void CheckFieldUniqueness(FeederQualifierFieldSelection.FeederField field)
    {
        HashSet<int> ids = new();
        foreach (FeederQualifierFieldSelection.FeederPick pick in field.All)
        {
            if (pick.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Feeder qualifier contains invalid athlete id {pick.SaveAthleteId}.");
            }

            if (string.IsNullOrWhiteSpace(pick.Name))
            {
                throw new InvalidOperationException($"Feeder qualifier athlete {pick.SaveAthleteId} has an empty name.");
            }

            if (!ids.Add(pick.SaveAthleteId))
            {
                throw new InvalidOperationException($"Feeder qualifier contains duplicate athlete id {pick.SaveAthleteId}.");
            }

            if (pick.SportingColor != field.SportingColor)
            {
                throw new InvalidOperationException(
                    $"Feeder qualifier athlete {pick.SaveAthleteId} color {pick.SportingColor} does not match boundary color {field.SportingColor}; no cross-color movement.");
            }
        }
    }

    private static void CheckIncumbentBands(
        FeederQualifierFieldSelection.FeederField field,
        LeagueEntity upperLeague)
    {
        foreach (FeederQualifierFieldSelection.FeederPick pick in field.Incumbents)
        {
            if (pick.FromLeagueId != upperLeague.Id)
            {
                throw new InvalidOperationException($"Feeder qualifier incumbent {pick.SaveAthleteId} must come from the upper tier.");
            }

            if (pick.FromSeasonRank < 17 || pick.FromSeasonRank > 24)
            {
                throw new InvalidOperationException(
                    $"Feeder qualifier incumbent rank {pick.FromSeasonRank} is outside places 17-24.");
            }

            if (pick.Role != QualifierRole.Incumbent)
            {
                throw new InvalidOperationException($"Feeder qualifier incumbent {pick.SaveAthleteId} has corrupt role {pick.Role}.");
            }
        }
    }

    private static void CheckChallengerBands(
        FeederQualifierFieldSelection.FeederField field,
        LeagueEntity lowerLeague)
    {
        foreach (FeederQualifierFieldSelection.FeederPick pick in field.Challengers)
        {
            if (pick.FromLeagueId != lowerLeague.Id)
            {
                throw new InvalidOperationException($"Feeder qualifier challenger {pick.SaveAthleteId} must come from the lower tier.");
            }

            if (pick.FromSeasonRank < 9 || pick.FromSeasonRank > 16)
            {
                throw new InvalidOperationException(
                    $"Feeder qualifier challenger rank {pick.FromSeasonRank} is outside places 9-16.");
            }

            if (pick.Role != QualifierRole.Challenger)
            {
                throw new InvalidOperationException($"Feeder qualifier challenger {pick.SaveAthleteId} has corrupt role {pick.Role}.");
            }
        }
    }

    private static void CheckFieldRanksMatch(
        FeederQualifierFieldSelection.FeederField field,
        IReadOnlyList<SeasonStandingEntity> upperStandings,
        IReadOnlyList<SeasonStandingEntity> lowerStandings)
    {
        Dictionary<int, int> upperRanks = upperStandings.ToDictionary(r => r.SaveAthleteId, r => r.SeasonRank);
        Dictionary<int, int> lowerRanks = lowerStandings.ToDictionary(r => r.SaveAthleteId, r => r.SeasonRank);
        foreach (FeederQualifierFieldSelection.FeederPick pick in field.All)
        {
            bool inUpper = upperRanks.TryGetValue(pick.SaveAthleteId, out int upperRank);
            bool inLower = lowerRanks.TryGetValue(pick.SaveAthleteId, out int lowerRank);
            if (inUpper == inLower)
            {
                throw new InvalidOperationException($"Athlete {pick.SaveAthleteId} must appear in exactly one source table.");
            }

            int actual = inUpper ? upperRank : lowerRank;
            if (actual != pick.FromSeasonRank)
            {
                throw new InvalidOperationException(
                    $"Athlete {pick.SaveAthleteId} rank {pick.FromSeasonRank} does not match final standing rank {actual}.");
            }
        }
    }

    public static void ValidateRound(
        RoundPayloadEntry entry,
        QualifierBoundary boundary,
        RulesV1 rules)
    {
        _ = boundary;
        ArgumentNullException.ThrowIfNull(rules);
        if (entry.Position < 1 || entry.Position > rules.FeederQualifierSize)
        {
            throw new InvalidOperationException($"Feeder qualifier payload position {entry.Position} is out of range.");
        }

        int expectedBase = rules.ScoringTable[entry.Position - 1] * RulesV1.FixedScale;
        if (entry.BaseThousandths != expectedBase)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier payload base points for position {entry.Position} must be {expectedBase}, was {entry.BaseThousandths}.");
        }
    }

    public static void ValidateCompleted(
        IReadOnlyList<StageRankedAthlete> ranked,
        IReadOnlyList<StageAthleteTotals> totals,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(rules);

        if (ranked.Count != rules.FeederQualifierSize)
        {
            throw new InvalidOperationException(
                $"Completed feeder qualifier must rank exactly {rules.FeederQualifierSize} athletes, was {ranked.Count}.");
        }

        if (totals.Count != rules.FeederQualifierSize)
        {
            throw new InvalidOperationException(
                $"Completed feeder qualifier must total exactly {rules.FeederQualifierSize} athletes, was {totals.Count}.");
        }

        CheckCompletedRanks(ranked, rules);
        CheckCompletedRoundTotals(ranked, rules);
    }

    private static void CheckCompletedRanks(
        IReadOnlyList<StageRankedAthlete> ranked,
        RulesV1 rules)
    {
        HashSet<int> ranks = new();
        HashSet<int> ids = new();
        foreach (StageRankedAthlete entry in ranked)
        {
            if (entry.StageRank < 1 || entry.StageRank > rules.FeederQualifierSize)
            {
                throw new InvalidOperationException($"Completed feeder qualifier rank {entry.StageRank} is out of range.");
            }

            if (!ranks.Add(entry.StageRank))
            {
                throw new InvalidOperationException($"Completed feeder qualifier contains duplicate rank {entry.StageRank}.");
            }

            if (!ids.Add(entry.AthleteId))
            {
                throw new InvalidOperationException($"Completed feeder qualifier contains duplicate athlete id {entry.AthleteId}.");
            }
        }

        if (!ranks.SetEquals(Enumerable.Range(1, rules.FeederQualifierSize)))
        {
            throw new InvalidOperationException("Completed feeder qualifier must cover ranks 1..16 exactly once.");
        }
    }

    private static void CheckCompletedRoundTotals(
        IReadOnlyList<StageRankedAthlete> ranked,
        RulesV1 rules)
    {
        foreach (StageRankedAthlete entry in ranked)
        {
            if (entry.RoundPlaceCounts.Count != rules.FeederQualifierSize)
            {
                throw new InvalidOperationException(
                    $"Completed feeder qualifier round-place counts for '{entry.Name}' must cover 16 positions.");
            }

            CheckRoundPlaceSum(entry, rules);
        }
    }

    private static void CheckRoundPlaceSum(StageRankedAthlete entry, RulesV1 rules)
    {
        int sum = 0;
        foreach (int count in entry.RoundPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Completed feeder qualifier counts for '{entry.Name}' cannot be negative.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum != rules.FeederQualifierRounds)
        {
            throw new InvalidOperationException(
                $"Completed feeder qualifier totals for '{entry.Name}' sum to {sum}, expected {rules.FeederQualifierRounds}.");
        }
    }

    public static void ValidatePersisted(
        SeasonEntity source,
        SeasonEntity next,
        QualifierBoundary boundary,
        int color,
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
        CheckPersistedRounds(rounds, source, next, boundary, color, rules);
        CheckPersistedStandingRows(standings, source, next, boundary, color, rules);
        CheckPersistedStandingTotals(standings, rules);
    }

    private static void CheckPersistedSeasons(SeasonEntity source, SeasonEntity next)
    {
        if (!source.HasSuperleague || !source.IsComplete)
        {
            throw new InvalidOperationException("Feeder qualifier requires a completed source season with a Superleague.");
        }

        if (next.SeasonNumber != source.SeasonNumber + 1 || !next.HasSuperleague || next.IsComplete)
        {
            throw new InvalidOperationException("Feeder qualifier next season must be the consecutive incomplete Superleague season.");
        }
    }

    private static void CheckPersistedCounts(
        IReadOnlyList<QualifierRoundEntity> rounds,
        IReadOnlyList<QualifierStandingEntity> standings,
        RulesV1 rules)
    {
        if (rounds.Count != rules.FeederQualifierRounds)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier must persist exactly {rules.FeederQualifierRounds} rounds, was {rounds.Count}.");
        }

        if (standings.Count != rules.FeederQualifierSize)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier must persist exactly {rules.FeederQualifierSize} standings, was {standings.Count}.");
        }
    }

    private static void CheckPersistedRounds(
        IReadOnlyList<QualifierRoundEntity> rounds,
        SeasonEntity source,
        SeasonEntity next,
        QualifierBoundary boundary,
        int color,
        RulesV1 rules)
    {
        HashSet<int> roundNumbers = new();
        foreach (QualifierRoundEntity round in rounds)
        {
            if (round.FromSeasonId != source.Id || round.ToSeasonId != next.Id)
            {
                throw new InvalidOperationException($"Feeder qualifier round {round.Id} has corrupt season linkage.");
            }

            if (round.QualifierBoundary != (int)boundary || round.QualifierSportingColor != color)
            {
                throw new InvalidOperationException($"Feeder qualifier round {round.Id} has corrupt event identity.");
            }

            if (!roundNumbers.Add(round.RoundNumber))
            {
                throw new InvalidOperationException($"Feeder qualifier contains duplicate round {round.RoundNumber}.");
            }
        }

        if (!roundNumbers.SetEquals(Enumerable.Range(1, rules.FeederQualifierRounds)))
        {
            throw new InvalidOperationException("Feeder qualifier must cover rounds 1..16 exactly once.");
        }
    }

    private static void CheckPersistedStandingRows(
        IReadOnlyList<QualifierStandingEntity> standings,
        SeasonEntity source,
        SeasonEntity next,
        QualifierBoundary boundary,
        int color,
        RulesV1 rules)
    {
        HashSet<int> ranks = new();
        HashSet<int> athleteIds = new();
        foreach (QualifierStandingEntity standing in standings)
        {
            if (standing.FromSeasonId != source.Id || standing.ToSeasonId != next.Id)
            {
                throw new InvalidOperationException($"Feeder qualifier standing {standing.Id} has corrupt season linkage.");
            }

            if (standing.QualifierBoundary != (int)boundary || standing.QualifierSportingColor != color)
            {
                throw new InvalidOperationException($"Feeder qualifier standing {standing.Id} has corrupt event identity.");
            }

            if (!ranks.Add(standing.QualifierRank))
            {
                throw new InvalidOperationException($"Feeder qualifier contains duplicate rank {standing.QualifierRank}.");
            }

            if (!athleteIds.Add(standing.SaveAthleteId))
            {
                throw new InvalidOperationException($"Feeder qualifier contains duplicate athlete id {standing.SaveAthleteId}.");
            }

            CheckStandingPlaceCounts(standing, rules);
        }
    }

    private static void CheckStandingPlaceCounts(QualifierStandingEntity standing, RulesV1 rules)
    {
        List<int>? counts = JsonSerializer.Deserialize<List<int>>(standing.RoundPlaceCountsJson);
        if (counts is null || counts.Count != rules.FeederQualifierSize)
        {
            throw new InvalidOperationException($"Feeder qualifier standing {standing.Id} has corrupt round-place counts.");
        }
    }

    private static void CheckPersistedStandingTotals(
        IReadOnlyList<QualifierStandingEntity> standings,
        RulesV1 rules)
    {
        HashSet<int> ranks = standings.Select(s => s.QualifierRank).ToHashSet();
        if (!ranks.SetEquals(Enumerable.Range(1, rules.FeederQualifierSize)))
        {
            throw new InvalidOperationException("Feeder qualifier must cover ranks 1..16 exactly once.");
        }

        int qualified = standings.Count(s => s.IsQualified);
        if (qualified != rules.FeederQualifierWinners)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier must hold exactly {rules.FeederQualifierWinners} successful qualifiers, was {qualified}.");
        }

        int incumbents = standings.Count(s => s.Role == (int)QualifierRole.Incumbent);
        int challengers = standings.Count(s => s.Role == (int)QualifierRole.Challenger);
        if (incumbents != rules.FeederQualifierIncumbentPerColor || challengers != rules.FeederQualifierChallengerPerColor)
        {
            throw new InvalidOperationException(
                $"Feeder qualifier must hold exactly 8 incumbents and 8 challengers, was {incumbents}/{challengers}.");
        }
    }
}
