using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.ChaosCup;

/// <summary>
/// Structural invariants for persisted Chaos Cup editions (MSS-099).
/// Fundamental failures throw and abort the mutation; corrupted sporting state is
/// never silently repaired. Covers editions, frozen entrants/source division,
/// staged groups and draw, group round results, group standings, advancing
/// entrants and the official Final. Supports partially played groups across
/// restart (contiguous per-group rounds, unbroken per-group RNG chain, standings
/// only for completed groups) and immutable completed round/replay facts.
/// Empty Chaos tables (historic saves without Chaos editions) are valid and need
/// no validation; these helpers only run when an edition is present.
/// </summary>
public static class ChaosCupInvariants
{
    public static void ValidateEdition(ChaosCupEditionEntity edition, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(rules);
        if (edition.SourceSeasonId <= 0)
        {
            throw new InvalidOperationException("Chaos Cup edition has corrupt source season linkage.");
        }

        if (edition.SourceSeasonNumber < 1)
        {
            throw new InvalidOperationException("Chaos Cup edition source season number is out of range.");
        }

        if (edition.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Chaos Cup edition rules version must be {rules.Version}, was {edition.RulesVersion}.");
        }

        if (edition.ChaosFormatVersion != ChaosCupFormat.ChaosFormatVersion)
        {
            throw new InvalidOperationException($"Chaos Cup edition format version must be {ChaosCupFormat.ChaosFormatVersion}, was {edition.ChaosFormatVersion}.");
        }

        if (edition.FieldSize != ChaosCupFormat.FieldSize)
        {
            throw new InvalidOperationException($"Chaos Cup edition field size must be {ChaosCupFormat.FieldSize}, was {edition.FieldSize}.");
        }

        if (string.IsNullOrWhiteSpace(edition.EditionChecksum))
        {
            throw new InvalidOperationException("Chaos Cup edition has an empty checksum.");
        }

        if (edition.RngAfterState == edition.RngBeforeState && edition.RngAfterStream == edition.RngBeforeStream)
        {
            throw new InvalidOperationException("Chaos Cup edition seed must advance the save RNG.");
        }
    }

    public static void ValidateEntrants(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        IReadOnlyList<ChaosCupEntrantEntity> entrants)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(entrants);
        if (edition.SourceSeasonId != source.Id || edition.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException("Chaos Cup edition source linkage does not match the season.");
        }

        if (entrants.Count != ChaosCupFormat.FieldSize)
        {
            throw new InvalidOperationException($"Chaos Cup must freeze exactly {ChaosCupFormat.FieldSize} entrants, was {entrants.Count}.");
        }

        HashSet<int> ids = new(entrants.Count);
        Dictionary<SportingColor, int> perColor = new();
        foreach (ChaosCupEntrantEntity row in entrants)
        {
            if (row.SourceSeasonId != source.Id || row.SourceSeasonNumber != source.SeasonNumber)
            {
                throw new InvalidOperationException($"Chaos Cup entrant {row.Id} has corrupt source linkage.");
            }

            if (row.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException("Chaos Cup entrant has corrupt athlete id.");
            }

            if (!ids.Add(row.SaveAthleteId))
            {
                throw new InvalidOperationException($"Chaos Cup field contains duplicate athlete {row.SaveAthleteId}.");
            }

            if (!Enum.IsDefined((SportingColor)row.SportingColor))
            {
                throw new InvalidOperationException($"Chaos Cup entrant {row.SaveAthleteId} has unknown sporting color {row.SportingColor}.");
            }

            if (row.IsPoolInvite != (row.SourceLeagueId is null))
            {
                throw new InvalidOperationException($"Chaos Cup entrant {row.SaveAthleteId} has inconsistent Pool invitation linkage.");
            }

            if (!row.IsPoolInvite && row.SourceLeagueId <= 0)
            {
                throw new InvalidOperationException($"Chaos Cup entrant {row.SaveAthleteId} has corrupt source league linkage.");
            }

            SportingColor color = (SportingColor)row.SportingColor;
            perColor.TryGetValue(color, out int current);
            perColor[color] = checked(current + 1);
        }

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            if (!perColor.TryGetValue(color, out int count) || count != ChaosCupFormat.EntrantsPerColor)
            {
                throw new InvalidOperationException($"Chaos Cup field must hold exactly {ChaosCupFormat.EntrantsPerColor} {color} entrants, was {count}.");
            }
        }
    }

    public static void ValidateStageDraw(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        IReadOnlyList<ChaosCupEntrantEntity> entrants,
        IReadOnlyList<ChaosCupDrawEntity> draws,
        int stageIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(draws);
        int expectedGroups = ChaosCupFormat.GroupCountForStage(stageIndex);
        int expectedEntrants = ChaosCupFormat.EntrantCountForStage(stageIndex);
        if (draws.Count != expectedEntrants)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} must draw exactly {expectedEntrants} entrants, was {draws.Count}.");
        }

        HashSet<int> entrantIds = entrants.Select(e => e.SaveAthleteId).ToHashSet();
        Dictionary<int, SportingColor> colorByAthlete = entrants.ToDictionary(e => e.SaveAthleteId, e => (SportingColor)e.SportingColor);
        CheckDrawLinkage(source, edition, draws, stageIndex, expectedGroups);
        CheckDrawCoverage(draws, stageIndex, expectedGroups, entrantIds);

        if (stageIndex == 0)
        {
            CheckFirstStageColorBalance(draws, colorByAthlete);
        }
    }

    public static void ValidateRoundPayload(
        ChaosCupRoundPayloadDocument payload,
        RulesV1 rules,
        ulong rngBeforeState,
        ulong rngBeforeStream)
    {
        CheckPayloadIdentity(payload, rules, rngBeforeState, rngBeforeStream);
        EnsurePayloadPlacements(payload, rules);
    }

    private static void CheckPayloadIdentity(
        ChaosCupRoundPayloadDocument payload,
        RulesV1 rules,
        ulong rngBeforeState,
        ulong rngBeforeStream)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(rules);
        if (payload.Version != ChaosCupRoundPayloadDocument.PayloadVersion)
        {
            throw new InvalidOperationException($"Chaos Cup payload version must be {ChaosCupRoundPayloadDocument.PayloadVersion}, was {payload.Version}.");
        }

        if (payload.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Chaos Cup payload rules version must be {rules.Version}, was {payload.RulesVersion}.");
        }

        if (payload.ChaosFormatVersion != ChaosCupFormat.ChaosFormatVersion)
        {
            throw new InvalidOperationException($"Chaos Cup payload format version must be {ChaosCupFormat.ChaosFormatVersion}, was {payload.ChaosFormatVersion}.");
        }

        if (payload.StageIndex < 0 || payload.StageIndex >= ChaosCupFormat.StageCount)
        {
            throw new InvalidOperationException($"Chaos Cup payload stage {payload.StageIndex} is out of range.");
        }

        int groupCount = ChaosCupFormat.GroupCountForStage(payload.StageIndex);
        if (payload.GroupNumber < 1 || payload.GroupNumber > groupCount)
        {
            throw new InvalidOperationException($"Chaos Cup payload group {payload.GroupNumber} is out of range for stage {payload.StageIndex}.");
        }

        if (payload.RoundNumber < 1 || payload.RoundNumber > ChaosCupFormat.RoundsPerGroup)
        {
            throw new InvalidOperationException($"Chaos Cup payload round {payload.RoundNumber} is out of range.");
        }

        if (payload.RngBeforeState != rngBeforeState || payload.RngBeforeStream != rngBeforeStream)
        {
            throw new InvalidOperationException("Chaos Cup payload RNG-before does not match the persisted group RNG chain.");
        }

        if (payload.RngAfterState == rngBeforeState && payload.RngAfterStream == rngBeforeStream)
        {
            throw new InvalidOperationException("Chaos Cup round simulation must advance the group RNG.");
        }

        if (string.IsNullOrWhiteSpace(payload.Checksum))
        {
            throw new InvalidOperationException("Chaos Cup payload has an empty checksum.");
        }

        if (payload.Placements.Count != ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException($"Chaos Cup round must contain exactly {ChaosCupFormat.GroupSize} placements, was {payload.Placements.Count}.");
        }
    }

    private static void EnsurePayloadPlacements(ChaosCupRoundPayloadDocument payload, RulesV1 rules)
    {
        HashSet<int> positions = new(ChaosCupFormat.GroupSize);
        HashSet<int> athleteIds = new(ChaosCupFormat.GroupSize);
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (RoundPayloadEntry entry in payload.Placements)
        {
            CheckSinglePlacement(entry, positions, athleteIds, names, rules);
        }

        if (!positions.SetEquals(Enumerable.Range(1, ChaosCupFormat.GroupSize)))
        {
            throw new InvalidOperationException("Chaos Cup payload must cover positions 1..32 exactly once.");
        }
    }

    private static void CheckSinglePlacement(
        RoundPayloadEntry entry,
        HashSet<int> positions,
        HashSet<int> athleteIds,
        HashSet<string> names,
        RulesV1 rules)
    {
        CheckPlacementIdentity(entry, positions, athleteIds, names);
        CheckPlacementPoints(entry, rules);
        if (entry.RankMovement != entry.RankBefore - entry.RankAfter)
        {
            throw new InvalidOperationException($"Chaos Cup payload rank movement for '{entry.Name}' is corrupt.");
        }
    }

    private static void CheckPlacementIdentity(
        RoundPayloadEntry entry,
        HashSet<int> positions,
        HashSet<int> athleteIds,
        HashSet<string> names)
    {
        if (entry.AthleteId <= 0)
        {
            throw new InvalidOperationException("Chaos Cup payload contains invalid athlete id.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Chaos Cup payload contains an athlete with an empty name.");
        }

        if (!positions.Add(entry.Position))
        {
            throw new InvalidOperationException($"Chaos Cup payload contains duplicate position {entry.Position}.");
        }

        if (!athleteIds.Add(entry.AthleteId))
        {
            throw new InvalidOperationException($"Chaos Cup payload contains duplicate athlete id {entry.AthleteId}.");
        }

        if (!names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Chaos Cup payload contains duplicate athlete '{entry.Name}'.");
        }

        if (entry.Position < 1 || entry.Position > ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException($"Chaos Cup payload position {entry.Position} is out of range.");
        }
    }

    private static void CheckPlacementPoints(RoundPayloadEntry entry, RulesV1 rules)
    {
        int expectedBase = rules.ScoringTable[entry.Position - 1] * RulesV1.FixedScale;
        if (entry.BaseThousandths != expectedBase)
        {
            throw new InvalidOperationException($"Chaos Cup payload base points for position {entry.Position} must be {expectedBase}, was {entry.BaseThousandths}.");
        }

        if (entry.ActiveBonusThousandths < 0)
        {
            throw new InvalidOperationException($"Chaos Cup payload active bonus for '{entry.Name}' cannot be negative.");
        }

        long expectedFinal = (long)entry.BaseThousandths * (RulesV1.BonusPercentScale + entry.ActiveBonusThousandths) / RulesV1.BonusPercentScale;
        if (entry.FinalThousandths != expectedFinal)
        {
            throw new InvalidOperationException($"Chaos Cup payload final points for '{entry.Name}' must be {expectedFinal}, was {entry.FinalThousandths}.");
        }

        long expectedAfter = (long)entry.CumulativeBeforeThousandths + entry.FinalThousandths;
        if (entry.CumulativeAfterThousandths != expectedAfter)
        {
            throw new InvalidOperationException($"Chaos Cup payload cumulative total for '{entry.Name}' is corrupt.");
        }

        if (entry.RankBefore < 1 || entry.RankBefore > ChaosCupFormat.GroupSize
            || entry.RankAfter < 1 || entry.RankAfter > ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException($"Chaos Cup payload ranks for '{entry.Name}' are out of range.");
        }
    }

    public static void ValidatePersistedRound(
        ChaosCupRoundEntity row,
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(rules);
        if (row.SourceSeasonId != source.Id || row.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Chaos Cup round {row.Id} has corrupt source linkage.");
        }

        if (row.SourceSeasonId != edition.SourceSeasonId)
        {
            throw new InvalidOperationException($"Chaos Cup round {row.Id} does not belong to the edition.");
        }

        if (row.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Chaos Cup round {row.StageIndex}/{row.GroupNumber}/{row.RoundNumber} has corrupt rules version.");
        }

        if (row.ChaosFormatVersion != ChaosCupFormat.ChaosFormatVersion)
        {
            throw new InvalidOperationException($"Chaos Cup round {row.StageIndex}/{row.GroupNumber}/{row.RoundNumber} has corrupt format version.");
        }

        ChaosCupRoundPayloadDocument payload = ChaosCupRoundPayloadDocument.FromStored(row.PayloadJson);
        if (!string.Equals(payload.Checksum, row.PayloadChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Chaos Cup round {row.StageIndex}/{row.GroupNumber}/{row.RoundNumber} checksum does not match its payload.");
        }

        if (payload.StageIndex != row.StageIndex || payload.GroupNumber != row.GroupNumber
            || payload.RoundNumber != row.RoundNumber || payload.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException($"Chaos Cup round {row.StageIndex}/{row.GroupNumber}/{row.RoundNumber} payload identity is corrupt.");
        }

        if (payload.RngBeforeState != unchecked((ulong)row.RngBeforeState)
            || payload.RngBeforeStream != unchecked((ulong)row.RngBeforeStream)
            || payload.RngAfterState != unchecked((ulong)row.RngAfterState)
            || payload.RngAfterStream != unchecked((ulong)row.RngAfterStream))
        {
            throw new InvalidOperationException($"Chaos Cup round {row.StageIndex}/{row.GroupNumber}/{row.RoundNumber} RNG does not match its payload.");
        }

        ValidateRoundPayload(payload, rules, payload.RngBeforeState, payload.RngBeforeStream);
    }

    public static void ValidateGroupRoundsContiguous(IReadOnlyList<ChaosCupRoundEntity> rounds, int stageIndex, int groupNumber)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        HashSet<int> seen = new();
        foreach (ChaosCupRoundEntity row in rounds)
        {
            if (row.StageIndex != stageIndex || row.GroupNumber != groupNumber)
            {
                throw new InvalidOperationException($"Chaos Cup group {stageIndex}/{groupNumber} contains a foreign round.");
            }

            if (row.RoundNumber < 1 || row.RoundNumber > ChaosCupFormat.RoundsPerGroup)
            {
                throw new InvalidOperationException($"Chaos Cup group {stageIndex}/{groupNumber} round {row.RoundNumber} is out of range.");
            }

            if (!seen.Add(row.RoundNumber))
            {
                throw new InvalidOperationException($"Chaos Cup group {stageIndex}/{groupNumber} contains duplicate round {row.RoundNumber}.");
            }
        }

        for (int expected = 1; expected <= rounds.Count; expected++)
        {
            if (!seen.Contains(expected))
            {
                throw new InvalidOperationException($"Chaos Cup group {stageIndex}/{groupNumber} must cover rounds 1..{rounds.Count} contiguously without gaps.");
            }
        }

        List<ChaosCupRoundEntity> ordered = rounds.OrderBy(r => r.RoundNumber).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].RngBeforeState != ordered[i - 1].RngAfterState
                || ordered[i].RngBeforeStream != ordered[i - 1].RngAfterStream)
            {
                throw new InvalidOperationException($"Chaos Cup group {stageIndex}/{groupNumber} has a broken RNG chain at round {ordered[i].RoundNumber}.");
            }
        }
    }

    public static void ValidateStageStandings(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        IReadOnlyList<ChaosCupDrawEntity> draws,
        IReadOnlyList<ChaosCupRoundEntity> rounds,
        IReadOnlyList<ChaosCupGroupStandingEntity> standings,
        int stageIndex,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(rules);
        int groupCount = ChaosCupFormat.GroupCountForStage(stageIndex);
        bool isFinal = ChaosCupFormat.IsFinalStage(stageIndex);

        Dictionary<int, List<ChaosCupDrawEntity>> drawsByGroup = draws
            .GroupBy(d => d.GroupNumber)
            .ToDictionary(g => g.Key, g => g.ToList());
        if (drawsByGroup.Count != groupCount)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} must draw exactly {groupCount} groups, was {drawsByGroup.Count}.");
        }

        Dictionary<int, List<ChaosCupGroupStandingEntity>> standingsByGroup = standings
            .GroupBy(s => s.GroupNumber)
            .ToDictionary(g => g.Key, g => g.ToList());

        CheckStageGroups(source, edition, drawsByGroup, rounds, standingsByGroup, stageIndex, groupCount, isFinal, rules);
        CheckStageCoverage(draws, standings, stageIndex);
        CheckStageAdvancement(standings, stageIndex, isFinal);
    }

    private static void CheckStageGroups(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        Dictionary<int, List<ChaosCupDrawEntity>> drawsByGroup,
        IReadOnlyList<ChaosCupRoundEntity> rounds,
        Dictionary<int, List<ChaosCupGroupStandingEntity>> standingsByGroup,
        int stageIndex,
        int groupCount,
        bool isFinal,
        RulesV1 rules)
    {
        foreach (int group in Enumerable.Range(1, groupCount))
        {
            if (!drawsByGroup.TryGetValue(group, out List<ChaosCupDrawEntity>? groupDraws)
                || groupDraws.Count != ChaosCupFormat.GroupSize)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} must draw exactly {ChaosCupFormat.GroupSize} athletes.");
            }

            List<ChaosCupRoundEntity> groupRounds = rounds
                .Where(r => r.GroupNumber == group)
                .OrderBy(r => r.RoundNumber)
                .ToList();
            ValidateGroupRoundsContiguous(groupRounds, stageIndex, group);

            bool groupComplete = groupRounds.Count == ChaosCupFormat.RoundsPerGroup;
            bool hasStandings = standingsByGroup.TryGetValue(group, out List<ChaosCupGroupStandingEntity>? groupStandings);

            if (!groupComplete && hasStandings)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} is partly played but already has standings.");
            }

            if (!groupComplete)
            {
                continue;
            }

            if (!hasStandings || groupStandings!.Count != ChaosCupFormat.GroupSize)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} is complete but misses standings.");
            }

            CheckGroupStandingRanks(source, edition, groupDraws, groupStandings!, groupRounds, stageIndex, group, isFinal, rules);
        }
    }

    private static void CheckStageCoverage(
        IReadOnlyList<ChaosCupDrawEntity> draws,
        IReadOnlyList<ChaosCupGroupStandingEntity> standings,
        int stageIndex)
    {
        HashSet<int> standingAthletes = standings.Select(s => s.SaveAthleteId).ToHashSet();
        if (standingAthletes.Count != standings.Count)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} contains duplicate advancing athlete.");
        }

        HashSet<int> drawnAthletes = draws.Select(d => d.SaveAthleteId).ToHashSet();
        if (!standingAthletes.IsSubsetOf(drawnAthletes) || standingAthletes.Count != drawnAthletes.Count)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} standings must cover exactly the drawn athletes.");
        }
    }

    private static void CheckStageAdvancement(
        IReadOnlyList<ChaosCupGroupStandingEntity> standings,
        int stageIndex,
        bool isFinal)
    {
        if (isFinal)
        {
            if (standings.Any(s => s.IsAdvanced))
            {
                throw new InvalidOperationException("Chaos Cup Final must not advance athletes; its ranks 1..32 are official places.");
            }

            return;
        }

        int advanced = standings.Count(s => s.IsAdvanced);
        int expected = ChaosCupFormat.AdvancingCountForStage(stageIndex);
        if (advanced != expected)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} must advance exactly {expected} athletes, was {advanced}.");
        }

        foreach (ChaosCupGroupStandingEntity standing in standings)
        {
            bool shouldAdvance = standing.GroupRank <= ChaosCupFormat.AdvancingPerGroup;
            if (standing.IsAdvanced != shouldAdvance)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {standing.GroupNumber} rank {standing.GroupRank} has corrupt advancement flag.");
            }
        }
    }

    public static void ValidateAdvancementLinkage(
        IReadOnlyList<ChaosCupGroupStandingEntity> prevStandings,
        IReadOnlyList<ChaosCupDrawEntity> nextDraws,
        int prevStageIndex)
    {
        ArgumentNullException.ThrowIfNull(prevStandings);
        ArgumentNullException.ThrowIfNull(nextDraws);
        HashSet<int> advancers = prevStandings.Where(s => s.IsAdvanced).Select(s => s.SaveAthleteId).ToHashSet();
        HashSet<int> next = nextDraws.Select(d => d.SaveAthleteId).ToHashSet();
        if (advancers.Count != ChaosCupFormat.AdvancingCountForStage(prevStageIndex))
        {
            throw new InvalidOperationException($"Chaos Cup stage {prevStageIndex} must advance exactly {ChaosCupFormat.AdvancingCountForStage(prevStageIndex)} athletes.");
        }

        if (!advancers.SetEquals(next))
        {
            throw new InvalidOperationException($"Chaos Cup stage {prevStageIndex + 1} draw must contain exactly the stage {prevStageIndex} advancers.");
        }
    }

    public static string ComputeEditionChecksum(IReadOnlyList<ChaosCupEntrantEntity> entrants)
    {
        ArgumentNullException.ThrowIfNull(entrants);
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (ChaosCupEntrantEntity row in entrants.OrderBy(e => e.SaveAthleteId))
        {
            builder.Append(row.SaveAthleteId);
            builder.Append(':');
            builder.Append(row.SportingColor);
            builder.Append(':');
            builder.Append(row.IsPoolInvite ? 'P' : 'L');
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    public static string ComputeStageDrawChecksum(int stageIndex, IReadOnlyList<ChaosCupDrawEntity> draws)
    {
        ArgumentNullException.ThrowIfNull(draws);
        using SHA256 sha = SHA256.Create();
        StringBuilder builder = new();
        foreach (ChaosCupDrawEntity row in draws.OrderBy(d => d.GroupNumber).ThenBy(d => d.DrawIndex))
        {
            builder.Append(stageIndex);
            builder.Append(':');
            builder.Append(row.GroupNumber);
            builder.Append(':');
            builder.Append(row.DrawIndex);
            builder.Append(':');
            builder.Append(row.SaveAthleteId);
            builder.Append('\n');
        }

        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static void CheckDrawLinkage(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        IReadOnlyList<ChaosCupDrawEntity> draws,
        int stageIndex,
        int expectedGroups)
    {
        ChaosCupDrawEntity first = draws[0];
        foreach (ChaosCupDrawEntity row in draws)
        {
            CheckSingleDrawRow(source, edition, row, first, stageIndex, expectedGroups);
        }

        if (first.RngAfterState == first.RngBeforeState && first.RngAfterStream == first.RngBeforeStream)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw must advance the save RNG.");
        }

        if (string.IsNullOrWhiteSpace(first.DrawChecksum))
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw has an empty checksum.");
        }

        string expected = ComputeStageDrawChecksum(stageIndex, draws);
        if (!string.Equals(first.DrawChecksum, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw checksum does not match its assignments.");
        }
    }

    private static void CheckSingleDrawRow(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        ChaosCupDrawEntity row,
        ChaosCupDrawEntity first,
        int stageIndex,
        int expectedGroups)
    {
        if (row.SourceSeasonId != source.Id || row.SourceSeasonNumber != source.SeasonNumber
            || row.SourceSeasonId != edition.SourceSeasonId)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw row {row.Id} has corrupt source linkage.");
        }

        if (row.StageIndex != stageIndex)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw contains a foreign stage {row.StageIndex} row.");
        }

        if (row.GroupNumber < 1 || row.GroupNumber > expectedGroups)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw group {row.GroupNumber} is out of range.");
        }

        if (row.DrawIndex < 0 || row.DrawIndex >= ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw index {row.DrawIndex} is out of range.");
        }

        if (row.GroupSize != ChaosCupFormat.GroupSize || row.StageGroupCount != expectedGroups)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw has corrupt group metadata.");
        }

        if (row.RulesVersion != edition.RulesVersion || row.ChaosFormatVersion != edition.ChaosFormatVersion)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw has corrupt version linkage.");
        }

        if (row.RngBeforeState != first.RngBeforeState || row.RngBeforeStream != first.RngBeforeStream
            || row.RngAfterState != first.RngAfterState || row.RngAfterStream != first.RngAfterStream
            || !string.Equals(row.DrawChecksum, first.DrawChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw has inconsistent RNG or checksum metadata.");
        }

        if (row.SaveAthleteId <= 0)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draw contains corrupt athlete id.");
        }
    }

    private static void CheckDrawCoverage(
        IReadOnlyList<ChaosCupDrawEntity> draws,
        int stageIndex,
        int expectedGroups,
        HashSet<int> entrantIds)
    {
        Dictionary<int, HashSet<int>> athletesByGroup = new();
        HashSet<int> seenAthletes = new();
        HashSet<(int Group, int DrawIndex)> seenPositions = new();
        foreach (ChaosCupDrawEntity row in draws)
        {
            if (!entrantIds.Contains(row.SaveAthleteId))
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} draws athlete {row.SaveAthleteId} outside the frozen field.");
            }

            if (!seenAthletes.Add(row.SaveAthleteId))
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} assigns athlete {row.SaveAthleteId} more than once.");
            }

            if (!seenPositions.Add((row.GroupNumber, row.DrawIndex)))
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {row.GroupNumber} contains duplicate draw index {row.DrawIndex}.");
            }

            if (!athletesByGroup.TryGetValue(row.GroupNumber, out HashSet<int>? members))
            {
                members = new HashSet<int>();
                athletesByGroup[row.GroupNumber] = members;
            }

            members.Add(row.SaveAthleteId);
        }

        if (athletesByGroup.Count != expectedGroups)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} must cover exactly {expectedGroups} groups, was {athletesByGroup.Count}.");
        }

        foreach ((int group, HashSet<int> members) in athletesByGroup)
        {
            if (members.Count != ChaosCupFormat.GroupSize)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} must hold exactly {ChaosCupFormat.GroupSize} athletes, was {members.Count}.");
            }
        }
    }

    private static void CheckFirstStageColorBalance(
        IReadOnlyList<ChaosCupDrawEntity> draws,
        Dictionary<int, SportingColor> colorByAthlete)
    {
        foreach (IGrouping<int, ChaosCupDrawEntity> group in draws.GroupBy(d => d.GroupNumber))
        {
            Dictionary<SportingColor, int> perColor = new();
            foreach (ChaosCupDrawEntity row in group)
            {
                if (!colorByAthlete.TryGetValue(row.SaveAthleteId, out SportingColor color))
                {
                    throw new InvalidOperationException($"Chaos Cup first-stage group {group.Key} holds athlete {row.SaveAthleteId} outside the frozen field.");
                }

                perColor.TryGetValue(color, out int current);
                perColor[color] = checked(current + 1);
            }

            foreach (SportingColor color in Enum.GetValues<SportingColor>())
            {
                if (!perColor.TryGetValue(color, out int count) || count != ChaosCupFormat.FirstStagePerColorPerGroup)
                {
                    throw new InvalidOperationException(
                        $"Chaos Cup first-stage group {group.Key} must hold exactly {ChaosCupFormat.FirstStagePerColorPerGroup} {color} athletes, was {count}.");
                }
            }
        }
    }

    private static void CheckGroupStandingRanks(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        List<ChaosCupDrawEntity> groupDraws,
        List<ChaosCupGroupStandingEntity> groupStandings,
        List<ChaosCupRoundEntity> groupRounds,
        int stageIndex,
        int group,
        bool isFinal,
        RulesV1 rules)
    {
        HashSet<int> drawn = groupDraws.Select(d => d.SaveAthleteId).ToHashSet();
        HashSet<int> ranks = new();
        HashSet<int> athletes = new();
        foreach (ChaosCupGroupStandingEntity standing in groupStandings)
        {
            CheckSingleStanding(source, edition, standing, drawn, ranks, athletes, stageIndex, group, isFinal);
        }

        if (!ranks.SetEquals(Enumerable.Range(1, ChaosCupFormat.GroupSize)))
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} must cover ranks 1..{ChaosCupFormat.GroupSize} exactly once.");
        }
    }

    private static void CheckSingleStanding(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        ChaosCupGroupStandingEntity standing,
        HashSet<int> drawn,
        HashSet<int> ranks,
        HashSet<int> athletes,
        int stageIndex,
        int group,
        bool isFinal)
    {
        if (standing.SourceSeasonId != source.Id || standing.SourceSeasonNumber != source.SeasonNumber
            || standing.SourceSeasonId != edition.SourceSeasonId)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} standing {standing.Id} has corrupt source linkage.");
        }

        if (standing.StageIndex != stageIndex || standing.GroupNumber != group)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} contains a foreign standing.");
        }

        if (standing.GroupRank < 1 || standing.GroupRank > ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} rank {standing.GroupRank} is out of range.");
        }

        if (!ranks.Add(standing.GroupRank))
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} contains duplicate rank {standing.GroupRank}.");
        }

        if (!athletes.Add(standing.SaveAthleteId))
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} contains duplicate athlete {standing.SaveAthleteId}.");
        }

        if (!drawn.Contains(standing.SaveAthleteId))
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} ranks athlete {standing.SaveAthleteId} outside its draw.");
        }

        if (standing.RoundWins < 0)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} standing has negative round wins.");
        }

        CheckStandingCounts(standing, stageIndex, group);
        CheckStandingAdvancement(standing, stageIndex, group, isFinal);
    }

    private static void CheckStandingCounts(ChaosCupGroupStandingEntity standing, int stageIndex, int group)
    {
        List<int>? counts = JsonSerializer.Deserialize<List<int>>(standing.RoundPlaceCountsJson);
        if (counts is null || counts.Count != ChaosCupFormat.GroupSize)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} standing has corrupt round-place counts.");
        }

        int sum = 0;
        foreach (int count in counts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} standing has negative placement counts.");
            }

            checked
            {
                sum += count;
            }
        }

        if (sum != ChaosCupFormat.RoundsPerGroup)
        {
            throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} standing sums to {sum} rounds, expected {ChaosCupFormat.RoundsPerGroup}.");
        }
    }

    private static void CheckStandingAdvancement(ChaosCupGroupStandingEntity standing, int stageIndex, int group, bool isFinal)
    {
        if (!isFinal)
        {
            bool shouldAdvance = standing.GroupRank <= ChaosCupFormat.AdvancingPerGroup;
            if (standing.IsAdvanced != shouldAdvance)
            {
                throw new InvalidOperationException($"Chaos Cup stage {stageIndex} group {group} rank {standing.GroupRank} has corrupt advancement flag.");
            }
        }
        else if (standing.IsAdvanced)
        {
            throw new InvalidOperationException("Chaos Cup Final standings must not carry advancement flags.");
        }
    }
}
