using System.Text.Json;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Structural invariants for the 32-athlete Color Cup individual event
/// (MSS-024). Fundamental failures throw and abort the mutation; corrupted
/// sporting state is never silently repaired. The Cup reuses pure round/stage
/// scoring primitives but never creates league <c>StageStanding</c>,
/// <c>SeasonStanding</c> or <c>Round</c> rows and never writes
/// <c>StageStandings.EarnedBonusThousandths</c>, so normal league championship
/// totals and career bonus are untouched.
/// </summary>
public static class ColorCupIndividualInvariants
{
    /// <summary>
    /// Validates the 32-athlete field before simulation: exactly the selected
    /// athletes (8 colors x 4), all distinct, with selection ranks 1..4 per
    /// color and standings linkage intact.
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
                $"Color Cup individual field must hold exactly {rules.ColorCupColorCount * rules.ColorCupTeamSize} selected athletes, was {selection.Count}.");
        }

        HashSet<int> athleteIds = new();
        foreach (ColorCupSelectionEntity row in selection)
        {
            CheckFieldRow(row, athleteIds, rules);
        }
    }

    /// <summary>
    /// Validates one freshly simulated Cup round before commit: exact 32
    /// placements, positions 1..32 exactly once, unique athletes, base points
    /// matching the snapshot scoring table, final points matching base plus
    /// active bonus, cumulative totals chaining and RNG advancement.
    /// </summary>
    public static void ValidateRound(
        ColorCupIndividualRoundPayloadDocument payload,
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
    /// Validates a freshly completed Cup stage before commit: exactly 32
    /// ranked athletes, ranks 1..32 exactly once, scores chaining from round
    /// payloads and round-place counts summing to 16 per athlete.
    /// Championship/earned values from the pure kernel are intentionally not
    /// validated here: the Cup persists no championship points and no new
    /// career bonus.
    /// </summary>
    public static void ValidateCompletedCup(
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
    /// Validates persisted Cup state: 16 round rows plus 32 standing rows for
    /// the source season, ranks 1..32 exactly once, medals exactly on ranks
    /// 1..3, no duplicate athletes and three podium honours (ranks 1/2/3).
    /// </summary>
    public static void ValidatePersisted(
        SeasonEntity source,
        IReadOnlyList<ColorCupIndividualRoundEntity> rounds,
        IReadOnlyList<ColorCupIndividualStandingEntity> standings,
        IReadOnlyList<HonourEntity> honours,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(honours);
        ArgumentNullException.ThrowIfNull(rules);
        CheckPersistedSeasons(source);
        CheckPersistedCounts(rounds, standings, rules);
        CheckPersistedRounds(rounds, source, rules);
        CheckPersistedStandings(standings, source, rules);
        CheckPersistedHonour(source, standings, honours);
    }

    private static void CheckFieldRow(ColorCupSelectionEntity row, HashSet<int> athleteIds, RulesV1 rules)
    {
        if (row.SaveAthleteId <= 0)
        {
            throw new InvalidOperationException($"Color Cup individual field contains invalid athlete id {row.SaveAthleteId}.");
        }

        if (row.SelectionRank < 1 || row.SelectionRank > rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup individual selection rank {row.SelectionRank} is out of range.");
        }

        if (!athleteIds.Add(row.SaveAthleteId))
        {
            throw new InvalidOperationException($"Color Cup individual field contains duplicate athlete id {row.SaveAthleteId}.");
        }
    }

    private static void CheckCompletedCounts(
        IReadOnlyList<StageRankedAthlete> ranked,
        IReadOnlyList<StageAthleteTotals> totals,
        RulesV1 rules)
    {
        if (ranked.Count != rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException(
                $"Completed Color Cup must rank exactly {rules.ColorCupColorCount * rules.ColorCupTeamSize} athletes, was {ranked.Count}.");
        }

        if (totals.Count != rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException(
                $"Completed Color Cup must total exactly {rules.ColorCupColorCount * rules.ColorCupTeamSize} athletes, was {totals.Count}.");
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

        if (!accumulator.Ranks.SetEquals(Enumerable.Range(1, rules.ColorCupColorCount * rules.ColorCupTeamSize)))
        {
            throw new InvalidOperationException("Completed Color Cup must cover ranks 1..32 exactly once.");
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
            throw new InvalidOperationException($"Completed Color Cup contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Completed Color Cup contains an athlete with an empty name.");
        }

        if (entry.StageRank < 1 || entry.StageRank > rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Completed Color Cup rank {entry.StageRank} is out of range.");
        }

        if (!accumulator.Ranks.Add(entry.StageRank))
        {
            throw new InvalidOperationException($"Completed Color Cup contains duplicate rank {entry.StageRank}.");
        }

        if (!accumulator.AthleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Completed Color Cup contains duplicate athlete id {entry.AthleteId}.");
        }

        if (!accumulator.Names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Completed Color Cup contains duplicate athlete '{entry.Name}'.");
        }
    }

    private static void CheckCompletedTotals(
        StageRankedAthlete entry,
        Dictionary<int, StageAthleteTotals> totalsById,
        RulesV1 rules)
    {
        if (!totalsById.TryGetValue(entry.AthleteId, out StageAthleteTotals? accumulated))
        {
            throw new InvalidOperationException($"Completed Color Cup standing for '{entry.Name}' has no accumulated totals.");
        }

        if (!string.Equals(accumulated.Name, entry.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Completed Color Cup standing for id {entry.AthleteId} has inconsistent names.");
        }

        if (entry.StageScoreThousandths != accumulated.StageScoreThousandths)
        {
            throw new InvalidOperationException($"Completed Color Cup score for '{entry.Name}' does not match accumulated round finals.");
        }

        if (entry.BaseScoreThousandths != accumulated.BaseScoreThousandths)
        {
            throw new InvalidOperationException($"Completed Color Cup base score for '{entry.Name}' does not match accumulated round base points.");
        }

        CheckCompletedCountsRow(entry, rules);
    }

    private static void CheckCompletedCountsRow(StageRankedAthlete entry, RulesV1 rules)
    {
        if (entry.RoundPlaceCounts.Count != rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Completed Color Cup round-place counts for '{entry.Name}' must cover 32 positions.");
        }

        int sum = 0;
        foreach (int count in entry.RoundPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Completed Color Cup round-place counts for '{entry.Name}' cannot be negative.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum != rules.ColorCupIndividualRounds)
        {
            throw new InvalidOperationException(
                $"Completed Color Cup totals for '{entry.Name}' sum to {sum} round appearances, expected {rules.ColorCupIndividualRounds}.");
        }
    }

    private static void CheckPersistedSeasons(SeasonEntity source)
    {
        if (!source.IsComplete)
        {
            throw new InvalidOperationException($"Color Cup source Season {source.SeasonNumber} must be complete.");
        }

        if (source.SeasonNumber % 2 != 1)
        {
            throw new InvalidOperationException($"Color Cup source Season {source.SeasonNumber} must be odd.");
        }
    }

    private static void CheckPersistedCounts(
        IReadOnlyList<ColorCupIndividualRoundEntity> rounds,
        IReadOnlyList<ColorCupIndividualStandingEntity> standings,
        RulesV1 rules)
    {
        if (rounds.Count != rules.ColorCupIndividualRounds)
        {
            throw new InvalidOperationException(
                $"Color Cup must persist exactly {rules.ColorCupIndividualRounds} rounds, was {rounds.Count}.");
        }

        if (standings.Count != rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException(
                $"Color Cup must persist exactly {rules.ColorCupColorCount * rules.ColorCupTeamSize} standings, was {standings.Count}.");
        }
    }

    private static void CheckPersistedRounds(
        IReadOnlyList<ColorCupIndividualRoundEntity> rounds,
        SeasonEntity source,
        RulesV1 rules)
    {
        HashSet<int> seenRounds = new();
        foreach (ColorCupIndividualRoundEntity round in rounds)
        {
            CheckSinglePersistedRound(round, source, rules, seenRounds);
        }

        if (!seenRounds.SetEquals(Enumerable.Range(1, rules.ColorCupIndividualRounds)))
        {
            throw new InvalidOperationException("Color Cup must cover rounds 1..16 exactly once.");
        }
    }

    private static void CheckSinglePersistedRound(
        ColorCupIndividualRoundEntity round,
        SeasonEntity source,
        RulesV1 rules,
        HashSet<int> seenRounds)
    {
        if (round.SourceSeasonId != source.Id || round.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Color Cup round {round.Id} has corrupt source linkage.");
        }

        if (round.RoundNumber < 1 || round.RoundNumber > rules.ColorCupIndividualRounds)
        {
            throw new InvalidOperationException($"Color Cup round number {round.RoundNumber} is out of range.");
        }

        if (!seenRounds.Add(round.RoundNumber))
        {
            throw new InvalidOperationException($"Color Cup contains duplicate round {round.RoundNumber}.");
        }

        if (round.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Color Cup round {round.RoundNumber} has corrupt rules version.");
        }

        if (string.IsNullOrWhiteSpace(round.PayloadJson) || string.IsNullOrWhiteSpace(round.PayloadChecksum))
        {
            throw new InvalidOperationException($"Color Cup round {round.RoundNumber} has an empty payload.");
        }

        ColorCupIndividualRoundPayloadDocument document = ColorCupIndividualRoundPayloadDocument.FromStored(round.PayloadJson);
        if (!string.Equals(document.Checksum, round.PayloadChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Color Cup round {round.RoundNumber} checksum does not match its payload.");
        }

        if (document.RoundNumber != round.RoundNumber || document.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Color Cup round {round.RoundNumber} payload identity is corrupt.");
        }
    }

    private static void CheckPersistedStandings(
        IReadOnlyList<ColorCupIndividualStandingEntity> standings,
        SeasonEntity source,
        RulesV1 rules)
    {
        HashSet<int> ranks = new();
        HashSet<int> athleteIds = new();
        foreach (ColorCupIndividualStandingEntity standing in standings)
        {
            CheckSinglePersistedStanding(standing, source, rules, ranks, athleteIds);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, rules.ColorCupColorCount * rules.ColorCupTeamSize)))
        {
            throw new InvalidOperationException("Color Cup must cover ranks 1..32 exactly once.");
        }
    }

    private static void CheckSinglePersistedStanding(
        ColorCupIndividualStandingEntity standing,
        SeasonEntity source,
        RulesV1 rules,
        HashSet<int> ranks,
        HashSet<int> athleteIds)
    {
        if (standing.SourceSeasonId != source.Id || standing.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Color Cup standing {standing.Id} has corrupt source linkage.");
        }

        if (standing.CupRank < 1 || standing.CupRank > rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup rank {standing.CupRank} is out of range.");
        }

        if (!ranks.Add(standing.CupRank))
        {
            throw new InvalidOperationException($"Color Cup contains duplicate rank {standing.CupRank}.");
        }

        if (!athleteIds.Add(standing.SaveAthleteId))
        {
            throw new InvalidOperationException($"Color Cup contains duplicate athlete id {standing.SaveAthleteId}.");
        }

        CheckStandingMedal(standing);
        CheckStandingPlaceCounts(standing, rules);
    }

    private static void CheckStandingMedal(ColorCupIndividualStandingEntity standing)
    {
        int expectedMedal = standing.CupRank switch
        {
            1 => (int)ColorCupMedal.Gold,
            2 => (int)ColorCupMedal.Silver,
            3 => (int)ColorCupMedal.Bronze,
            _ => (int)ColorCupMedal.None,
        };
        if (standing.Medal != expectedMedal)
        {
            throw new InvalidOperationException($"Color Cup rank {standing.CupRank} has corrupt medal {standing.Medal}.");
        }

        if (standing.SelectionRank < 1 || standing.SelectionRank > 4)
        {
            throw new InvalidOperationException($"Color Cup standing {standing.Id} has corrupt selection rank.");
        }
    }

    private static void CheckStandingPlaceCounts(ColorCupIndividualStandingEntity standing, RulesV1 rules)
    {
        List<int>? counts = JsonSerializer.Deserialize<List<int>>(standing.RoundPlaceCountsJson);
        if (counts is null || counts.Count != rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup standing {standing.Id} has corrupt round-place counts.");
        }

        int sum = 0;
        foreach (int count in counts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Color Cup standing {standing.Id} has negative placement counts.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum != rules.ColorCupIndividualRounds)
        {
            throw new InvalidOperationException($"Color Cup standing {standing.Id} sums to {sum} rounds, expected {rules.ColorCupIndividualRounds}.");
        }
    }

    private static void CheckPersistedHonour(
        SeasonEntity source,
        IReadOnlyList<ColorCupIndividualStandingEntity> standings,
        IReadOnlyList<HonourEntity> honours)
    {
        List<HonourEntity> cupHonours = honours
            .Where(h => h.SeasonId == source.Id && (h.Kind == (int)Features.Records.HonourKind.ColorCupIndividualChampion
                || h.Kind == (int)Features.Records.HonourKind.ColorCupIndividualRunnerUp
                || h.Kind == (int)Features.Records.HonourKind.ColorCupIndividualThirdPlace))
            .ToList();
        if (cupHonours.Count != 3)
        {
            throw new InvalidOperationException(
                $"Color Cup for Season {source.SeasonNumber} must persist exactly three individual podium honours, was {cupHonours.Count}.");
        }

        foreach (int rank in new[] { 1, 2, 3 })
        {
            ColorCupIndividualStandingEntity standing = standings.Single(s => s.CupRank == rank);
            Features.Records.HonourKind expectedKind = Features.Records.HonourKindMapper.FromColorCupIndividualRank(rank);
            HonourEntity honour = cupHonours.SingleOrDefault(h => h.Kind == (int)expectedKind)
                ?? throw new InvalidOperationException($"Color Cup for Season {source.SeasonNumber} is missing rank-{rank} podium honour.");
            if (honour.SaveAthleteId != standing.SaveAthleteId)
            {
                throw new InvalidOperationException($"Color Cup rank-{rank} podium honour does not match the rank-{rank} athlete.");
            }
        }
    }

    private static void CheckRoundIdentity(ColorCupIndividualRoundPayloadDocument payload, RulesV1 rules)
    {
        if (payload.Version != ColorCupIndividualRoundPayloadDocument.PayloadVersion)
        {
            throw new InvalidOperationException($"Color Cup payload version must be {ColorCupIndividualRoundPayloadDocument.PayloadVersion}, was {payload.Version}.");
        }

        if (payload.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Color Cup payload rules version must be {rules.Version}, was {payload.RulesVersion}.");
        }

        if (payload.SourceSeasonNumber < 1 || payload.SourceSeasonNumber % 2 != 1)
        {
            throw new InvalidOperationException($"Color Cup source season must be a positive odd season, was {payload.SourceSeasonNumber}.");
        }

        if (payload.RoundNumber < 1 || payload.RoundNumber > rules.ColorCupIndividualRounds)
        {
            throw new InvalidOperationException($"Color Cup round must be 1..{rules.ColorCupIndividualRounds}, was {payload.RoundNumber}.");
        }

        if (payload.Placements.Count != rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup round must contain exactly {rules.ColorCupColorCount * rules.ColorCupTeamSize} placements, was {payload.Placements.Count}.");
        }
    }

    private static void CheckRoundRng(ColorCupIndividualRoundPayloadDocument payload, ulong rngBeforeState, ulong rngBeforeStream)
    {
        if (payload.RngBeforeState != rngBeforeState || payload.RngBeforeStream != rngBeforeStream)
        {
            throw new InvalidOperationException("Color Cup payload RNG-before does not match the persisted save RNG chain.");
        }

        if (payload.RngAfterState == rngBeforeState && payload.RngAfterStream == rngBeforeStream)
        {
            throw new InvalidOperationException("Color Cup round simulation must advance the save RNG.");
        }
    }

    private static void EnsurePlacements(ColorCupIndividualRoundPayloadDocument payload, RulesV1 rules)
    {
        PlacementAccumulator accumulator = new();
        foreach (RoundPayloadEntry entry in payload.Placements)
        {
            CheckPlacementIdentity(entry, accumulator);
            CheckPlacementPoints(entry, rules);
            CheckPlacementRanks(entry, rules);
        }

        if (!accumulator.Positions.SetEquals(Enumerable.Range(1, rules.ColorCupColorCount * rules.ColorCupTeamSize)))
        {
            throw new InvalidOperationException("Color Cup payload must cover positions 1..32 exactly once.");
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
            throw new InvalidOperationException($"Color Cup payload contains invalid athlete id {entry.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Color Cup payload contains an athlete with an empty name.");
        }

        if (!accumulator.Positions.Add(entry.Position))
        {
            throw new InvalidOperationException($"Color Cup payload contains duplicate position {entry.Position}.");
        }

        if (!accumulator.AthleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Color Cup payload contains duplicate athlete id {entry.AthleteId}.");
        }

        if (!accumulator.Names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Color Cup payload contains duplicate athlete '{entry.Name}'.");
        }
    }

    private static void CheckPlacementPoints(RoundPayloadEntry entry, RulesV1 rules)
    {
        if (entry.Position < 1 || entry.Position > rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup payload position {entry.Position} is out of range.");
        }

        int expectedBase = rules.ScoringTable[entry.Position - 1] * RulesV1.FixedScale;
        if (entry.BaseThousandths != expectedBase)
        {
            throw new InvalidOperationException($"Color Cup payload base points for position {entry.Position} must be {expectedBase}, was {entry.BaseThousandths}.");
        }

        if (entry.ActiveBonusThousandths < 0)
        {
            throw new InvalidOperationException($"Color Cup payload active bonus for '{entry.Name}' cannot be negative.");
        }

        long expectedFinal = (long)entry.BaseThousandths * (RulesV1.BonusPercentScale + entry.ActiveBonusThousandths) / RulesV1.BonusPercentScale;
        if (entry.FinalThousandths != expectedFinal)
        {
            throw new InvalidOperationException($"Color Cup payload final points for '{entry.Name}' must be {expectedFinal}, was {entry.FinalThousandths}.");
        }

        long expectedAfter = (long)entry.CumulativeBeforeThousandths + entry.FinalThousandths;
        if (entry.CumulativeAfterThousandths != expectedAfter)
        {
            throw new InvalidOperationException($"Color Cup payload cumulative total for '{entry.Name}' is corrupt.");
        }
    }

    private static void CheckPlacementRanks(RoundPayloadEntry entry, RulesV1 rules)
    {
        if (entry.RankBefore < 1 || entry.RankBefore > rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup payload rank-before for '{entry.Name}' is out of range.");
        }

        if (entry.RankAfter < 1 || entry.RankAfter > rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup payload rank-after for '{entry.Name}' is out of range.");
        }

        if (entry.RankMovement != entry.RankBefore - entry.RankAfter)
        {
            throw new InvalidOperationException($"Color Cup payload rank movement for '{entry.Name}' is corrupt.");
        }
    }

    private static void EnsureRankConsistency(ColorCupIndividualRoundPayloadDocument payload)
    {
        List<RoundPayloadEntry> byBefore = payload.Placements.OrderBy(e => e.RankBefore).ToList();
        List<RoundPayloadEntry> byAfter = payload.Placements.OrderBy(e => e.RankAfter).ToList();
        CheckRankSequences(byBefore, byAfter);
    }

    private static void CheckRankSequences(List<RoundPayloadEntry> byBefore, List<RoundPayloadEntry> byAfter)
    {
        for (int i = 0; i < byBefore.Count; i++)
        {
            if (byBefore[i].RankBefore != i + 1)
            {
                throw new InvalidOperationException("Color Cup payload rank-before sequence is corrupt.");
            }
        }

        for (int i = 0; i < byAfter.Count; i++)
        {
            if (byAfter[i].RankAfter != i + 1)
            {
                throw new InvalidOperationException("Color Cup payload rank-after sequence is corrupt.");
            }
        }
    }
}
