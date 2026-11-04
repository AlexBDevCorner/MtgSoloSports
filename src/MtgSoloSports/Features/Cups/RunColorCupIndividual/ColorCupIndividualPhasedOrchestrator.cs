using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.Stages;
using MtgSoloSports.SimulationKernel.TieBreaking;

namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Cup-specific two-phase orchestrator for the phased individual Color Cup
/// (MSS-120): 16 qualifying rounds of 32 followed by 16 final rounds of the
/// top-16 qualifiers with 100% score carryover. Reuses field-size simulation
/// primitives without affecting normal league rounds and never calls a
/// mixed-size <c>StageCalculator.Accumulate</c>. All sporting math is
/// fixed-point integer-only with deterministic name-ordinal canonical order;
/// the only randomness is the caller-supplied versioned RNG.
/// </summary>
public static class ColorCupIndividualPhasedOrchestrator
{
    public sealed record CutoffResolution(
        IReadOnlyList<int> QualifierAthleteIds,
        Pcg32State RngAfterCutoff,
        bool TieDrawConsumed,
        IReadOnlyList<StageAthleteTotals> QualifyingTotals,
        IReadOnlyList<int> QualifyingOrderAthleteIds);

    /// <summary>
    /// Resolves the round-16 cutoff from qualifying payloads plus the RNG state
    /// after round 16. Untied cutoffs leave the RNG unchanged; tied cutoffs
    /// shuffle the straddling run once (name-ordered before shuffling).
    /// </summary>
    public static CutoffResolution ResolveCutoff(
        IReadOnlyList<ColorCupIndividualRoundPayloadDocument> qualifyingPayloads,
        RulesV1 rules,
        Pcg32State rngBeforeCutoff)
    {
        ArgumentNullException.ThrowIfNull(qualifyingPayloads);
        ArgumentNullException.ThrowIfNull(rules);
        ValidateQualifyingPayloads(qualifyingPayloads);
        IReadOnlyList<StageAthleteTotals> totals = BuildQualifyingTotals(qualifyingPayloads, rules);
        return ResolveCutoffFromTotals(totals, rngBeforeCutoff);
    }

    /// <summary>
    /// Resolves the cutoff from already-accumulated qualifying totals (pure,
    /// fixture-friendly). Ordering and RNG rules are identical to
    /// <see cref="ResolveCutoff"/>; payload callers should prefer that overload.
    /// </summary>
    public static CutoffResolution ResolveCutoffFromTotals(
        IReadOnlyList<StageAthleteTotals> totals,
        Pcg32State rngBeforeCutoff)
    {
        ArgumentNullException.ThrowIfNull(totals);
        if (totals.Count != ColorCupIndividualFormat.FieldSize)
        {
            throw new InvalidOperationException($"Color Cup qualifying cutoff requires exactly {ColorCupIndividualFormat.FieldSize} totals, was {totals.Count}.");
        }

        List<StageAthleteTotals> ordered = OrderQualifying(totals);
        CutoffResolution? untied = TryUntiedCutoff(ordered, rngBeforeCutoff, totals);
        if (untied is not null)
        {
            return untied;
        }

        return ResolveTiedCutoff(ordered, rngBeforeCutoff, totals);
    }

    /// <summary>
    /// Recovers the immutable final roster from the first persisted final
    /// payload (round 17). Once the final has started, reopen/resume must use
    /// this persisted set rather than re-resolving the cutoff with post-final
    /// RNG, which would reroll a tied cutoff on every reopen.
    /// </summary>
    public static IReadOnlyList<int> RecoverQualifiersFromFinalPayload(
        ColorCupIndividualRoundPayloadDocument firstFinalPayload)
    {
        ArgumentNullException.ThrowIfNull(firstFinalPayload);
        CheckFinalPayloadIdentity(firstFinalPayload);
        HashSet<int> seen = new();
        List<int> qualifiers = new(firstFinalPayload.Placements.Count);
        foreach (var placement in firstFinalPayload.Placements)
        {
            if (!seen.Add(placement.AthleteId))
            {
                throw new InvalidOperationException($"Color Cup final payload contains duplicate athlete id {placement.AthleteId}.");
            }

            qualifiers.Add(placement.AthleteId);
        }

        return qualifiers;
    }

    /// <summary>
    /// Accumulates phased totals with full carryover: qualifying points plus
    /// final points for finalists, qualifying only for eliminated athletes.
    /// </summary>
    public static IReadOnlyList<StageAthleteTotals> AccumulatePhasedTotals(
        IReadOnlyList<ColorCupIndividualRoundPayloadDocument> payloads,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(rules);
        CheckPhasedPayloadCount(payloads);
        (IReadOnlyList<StageAthleteTotals> qualTotals, IReadOnlyList<StageAthleteTotals> finalTotals) =
            AccumulateBothPhases(payloads, rules);
        List<StageAthleteTotals> merged = MergeTotals(qualTotals, finalTotals);
        ValidateMergedTotals(merged);
        merged.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        return merged;
    }

    /// <summary>
    /// Ranks phased totals best-first: finalists 1–16 by combined totals with
    /// the established stage ordering (seeded draw from <paramref name="rng"/>
    /// only for exactly tied finalists); eliminated athletes 17–32 strictly by
    /// the frozen round-16 <paramref name="qualifyingOrderAthleteIds"/> order
    /// with no further RNG. Final points are strictly positive so finalists
    /// always outrank eliminated athletes; a violation aborts.
    /// </summary>
    public static IReadOnlyList<StageRankedAthlete> RankPhasedTotals(
        IReadOnlyList<StageAthleteTotals> totals,
        IReadOnlyList<int> qualifyingOrderAthleteIds,
        Pcg32V1 rng,
        RulesV1 rules,
        LeagueLevel level)
    {
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(qualifyingOrderAthleteIds);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();
        ValidateMergedTotals(totals);
        ValidateQualifyingOrder(totals, qualifyingOrderAthleteIds);
        Dictionary<int, StageAthleteTotals> byId = totals.ToDictionary(t => t.AthleteId);
        List<StageAthleteTotals> finalists = SelectFinalists(byId, qualifyingOrderAthleteIds);
        List<StageAthleteTotals> eliminated = SelectEliminated(byId, qualifyingOrderAthleteIds);
        CheckFinalistsOutrankEliminated(finalists, eliminated);
        List<StageAthleteTotals> finalistsByScore = SortByScore(finalists);
        IReadOnlyList<StageRankedAthlete> finalistRanked = RankScoreGroups(finalistsByScore, rng, rules, level);
        List<StageRankedAthlete> ranked = new(finalistRanked);
        Dictionary<int, int> orderIndex = qualifyingOrderAthleteIds
            .Select((id, index) => (id, index))
            .ToDictionary(p => p.id, p => p.index);
        eliminated.Sort((left, right) => orderIndex[left.AthleteId].CompareTo(orderIndex[right.AthleteId]));
        foreach (StageAthleteTotals entry in eliminated)
        {
            ranked.Add(AwardStanding(ranked.Count + 1, entry, rules, level));
        }

        return ranked;
    }

    private static void ValidateQualifyingPayloads(IReadOnlyList<ColorCupIndividualRoundPayloadDocument> qualifyingPayloads)
    {
        if (qualifyingPayloads.Count != ColorCupIndividualFormat.QualifyingRounds)
        {
            throw new InvalidOperationException(
                $"Color Cup qualifying cutoff requires exactly {ColorCupIndividualFormat.QualifyingRounds} rounds, was {qualifyingPayloads.Count}.");
        }

        foreach (ColorCupIndividualRoundPayloadDocument payload in qualifyingPayloads)
        {
            CheckQualifyingPayload(payload);
        }
    }

    private static void CheckQualifyingPayload(ColorCupIndividualRoundPayloadDocument payload)
    {
        if (ColorCupIndividualFormat.ResolveStored(payload.FormatVersion) != ColorCupIndividualFormat.PhasedFormatVersion)
        {
            throw new InvalidOperationException("Color Cup qualifying cutoff requires phased payloads.");
        }

        if (payload.Placements.Count != ColorCupIndividualFormat.QualifyingFieldSize)
        {
            throw new InvalidOperationException(
                $"Color Cup qualifying round must contain exactly {ColorCupIndividualFormat.QualifyingFieldSize} placements, was {payload.Placements.Count}.");
        }
    }

    private static IReadOnlyList<StageAthleteTotals> BuildQualifyingTotals(
        IReadOnlyList<ColorCupIndividualRoundPayloadDocument> qualifyingPayloads,
        RulesV1 rules)
    {
        List<List<StageRoundEntry>> roundEntries = new(qualifyingPayloads.Count);
        foreach (ColorCupIndividualRoundPayloadDocument payload in qualifyingPayloads)
        {
            roundEntries.Add(ToStageEntries(payload));
        }

        return StageCalculator.AccumulateWithFieldSize(
            roundEntries, rules, ColorCupIndividualFormat.QualifyingFieldSize, ColorCupIndividualFormat.QualifyingRounds);
    }

    private static List<StageRoundEntry> ToStageEntries(ColorCupIndividualRoundPayloadDocument payload)
    {
        List<StageRoundEntry> entries = new(payload.Placements.Count);
        foreach (var placement in payload.Placements)
        {
            entries.Add(new StageRoundEntry(
                placement.AthleteId,
                placement.Name,
                placement.Position,
                placement.BaseThousandths,
                placement.FinalThousandths));
        }

        return entries;
    }

    private static List<StageAthleteTotals> OrderQualifying(IReadOnlyList<StageAthleteTotals> totals)
    {
        List<StageAthleteTotals> ordered = [.. totals];
        ordered.Sort(CompareQualifying);
        return ordered;
    }

    private static CutoffResolution? TryUntiedCutoff(
        List<StageAthleteTotals> ordered,
        Pcg32State rngBeforeCutoff,
        IReadOnlyList<StageAthleteTotals> totals)
    {
        StageAthleteTotals sixteenth = ordered[ColorCupIndividualFormat.CutoffRank - 1];
        StageAthleteTotals seventeenth = ordered[ColorCupIndividualFormat.CutoffRank];
        if (sixteenth.StageScoreThousandths != seventeenth.StageScoreThousandths)
        {
            return new CutoffResolution(TakeTopIds(ordered), rngBeforeCutoff, false, totals, OrderedIds(ordered));
        }

        if (CompareDeterministicPart(sixteenth, seventeenth) != 0)
        {
            return new CutoffResolution(TakeTopIds(ordered), rngBeforeCutoff, false, totals, OrderedIds(ordered));
        }

        return null;
    }

    private static List<int> TakeTopIds(List<StageAthleteTotals> ordered)
    {
        List<int> qualifiers = new(ColorCupIndividualFormat.CutoffRank);
        for (int i = 0; i < ColorCupIndividualFormat.CutoffRank; i++)
        {
            qualifiers.Add(ordered[i].AthleteId);
        }

        return qualifiers;
    }

    private static List<int> OrderedIds(List<StageAthleteTotals> ordered)
    {
        List<int> ids = new(ordered.Count);
        foreach (StageAthleteTotals entry in ordered)
        {
            ids.Add(entry.AthleteId);
        }

        return ids;
    }

    private static CutoffResolution ResolveTiedCutoff(
        List<StageAthleteTotals> ordered,
        Pcg32State rngBeforeCutoff,
        IReadOnlyList<StageAthleteTotals> totals)
    {
        StageAthleteTotals sixteenth = ordered[ColorCupIndividualFormat.CutoffRank - 1];
        (int runStart, int runEnd) = FindStraddlingRun(ordered, sixteenth);
        List<StageAthleteTotals> tied = ordered.GetRange(runStart, runEnd - runStart);
        Pcg32V1 rng = Pcg32V1.Restore(rngBeforeCutoff);
        DeterministicShuffle.Shuffle(tied, rng);
        Pcg32State after = rng.Snapshot();
        List<StageAthleteTotals> resolved = Reassemble(ordered, tied, runStart, runEnd);
        return new CutoffResolution(TakeTopIds(resolved), after, true, totals, OrderedIds(resolved));
    }

    private static (int RunStart, int RunEnd) FindStraddlingRun(
        List<StageAthleteTotals> ordered,
        StageAthleteTotals sixteenth)
    {
        int runStart = ColorCupIndividualFormat.CutoffRank - 1;
        while (runStart > 0 && IsCutoffTied(ordered[runStart - 1], sixteenth))
        {
            runStart--;
        }

        int runEnd = ColorCupIndividualFormat.CutoffRank;
        while (runEnd < ordered.Count && IsCutoffTied(ordered[runEnd], sixteenth))
        {
            runEnd++;
        }

        return (runStart, runEnd);
    }

    private static bool IsCutoffTied(StageAthleteTotals candidate, StageAthleteTotals sixteenth)
    {
        return candidate.StageScoreThousandths == sixteenth.StageScoreThousandths &&
            CompareDeterministicPart(candidate, sixteenth) == 0;
    }

    private static List<StageAthleteTotals> Reassemble(
        List<StageAthleteTotals> ordered,
        List<StageAthleteTotals> tied,
        int runStart,
        int runEnd)
    {
        List<StageAthleteTotals> resolved = new(ordered.Count);
        resolved.AddRange(ordered.GetRange(0, runStart));
        resolved.AddRange(tied);
        resolved.AddRange(ordered.GetRange(runEnd, ordered.Count - runEnd));
        return resolved;
    }

    private static void CheckFinalPayloadIdentity(ColorCupIndividualRoundPayloadDocument firstFinalPayload)
    {
        if (ColorCupIndividualFormat.ResolveStored(firstFinalPayload.FormatVersion) != ColorCupIndividualFormat.PhasedFormatVersion)
        {
            throw new InvalidOperationException("Color Cup final roster recovery requires a phased payload.");
        }

        if (firstFinalPayload.RoundNumber != ColorCupIndividualFormat.QualifyingRounds + 1)
        {
            throw new InvalidOperationException(
                $"Color Cup final roster recovery requires round {ColorCupIndividualFormat.QualifyingRounds + 1}, was {firstFinalPayload.RoundNumber}.");
        }

        if (firstFinalPayload.Placements.Count != ColorCupIndividualFormat.FinalFieldSize)
        {
            throw new InvalidOperationException(
                $"Color Cup final roster recovery requires exactly {ColorCupIndividualFormat.FinalFieldSize} placements, was {firstFinalPayload.Placements.Count}.");
        }
    }

    private static void CheckPhasedPayloadCount(IReadOnlyList<ColorCupIndividualRoundPayloadDocument> payloads)
    {
        if (payloads.Count != ColorCupIndividualFormat.QualifyingRounds + ColorCupIndividualFormat.FinalRounds)
        {
            throw new InvalidOperationException(
                $"Color Cup phased accumulation requires exactly {ColorCupIndividualFormat.QualifyingRounds + ColorCupIndividualFormat.FinalRounds} rounds, was {payloads.Count}.");
        }
    }

    private static (IReadOnlyList<StageAthleteTotals> QualTotals, IReadOnlyList<StageAthleteTotals> FinalTotals) AccumulateBothPhases(
        IReadOnlyList<ColorCupIndividualRoundPayloadDocument> payloads,
        RulesV1 rules)
    {
        List<ColorCupIndividualRoundPayloadDocument> qualifying = payloads.Take(ColorCupIndividualFormat.QualifyingRounds).ToList();
        List<ColorCupIndividualRoundPayloadDocument> finals = payloads.Skip(ColorCupIndividualFormat.QualifyingRounds).ToList();
        List<List<StageRoundEntry>> qualEntries = ToRoundEntries(qualifying);
        List<List<StageRoundEntry>> finalEntries = ToRoundEntries(finals);
        IReadOnlyList<StageAthleteTotals> qualTotals = StageCalculator.AccumulateWithFieldSize(
            qualEntries, rules, ColorCupIndividualFormat.QualifyingFieldSize, ColorCupIndividualFormat.QualifyingRounds);
        IReadOnlyList<StageAthleteTotals> finalTotals = StageCalculator.AccumulateWithFieldSize(
            finalEntries, rules, ColorCupIndividualFormat.FinalFieldSize, ColorCupIndividualFormat.FinalRounds);
        return (qualTotals, finalTotals);
    }

    private static List<StageAthleteTotals> MergeTotals(
        IReadOnlyList<StageAthleteTotals> qualTotals,
        IReadOnlyList<StageAthleteTotals> finalTotals)
    {
        Dictionary<int, StageAthleteTotals> finalById = finalTotals.ToDictionary(t => t.AthleteId);
        if (finalById.Count != ColorCupIndividualFormat.FinalFieldSize)
        {
            throw new InvalidOperationException($"Color Cup final accumulation requires exactly {ColorCupIndividualFormat.FinalFieldSize} finalists, was {finalById.Count}.");
        }

        List<StageAthleteTotals> merged = new(qualTotals.Count);
        foreach (StageAthleteTotals qual in qualTotals)
        {
            merged.Add(MergeOne(qual, finalById));
        }

        return merged;
    }

    private static StageAthleteTotals MergeOne(
        StageAthleteTotals qual,
        Dictionary<int, StageAthleteTotals> finalById)
    {
        if (!finalById.TryGetValue(qual.AthleteId, out StageAthleteTotals? final))
        {
            return qual;
        }

        int stageScore = checked(qual.StageScoreThousandths + final.StageScoreThousandths);
        int baseScore = checked(qual.BaseScoreThousandths + final.BaseScoreThousandths);
        List<int> counts = MergeCounts(qual, final);
        return new StageAthleteTotals(qual.AthleteId, qual.Name, stageScore, baseScore, counts);
    }

    private static List<int> MergeCounts(StageAthleteTotals qual, StageAthleteTotals final)
    {
        if (final.RoundPlaceCounts.Count != ColorCupIndividualFormat.FinalFieldSize)
        {
            throw new InvalidOperationException("Color Cup final place counts must cover 16 positions.");
        }

        List<int> counts = new(new int[ColorCupIndividualFormat.FieldSize]);
        for (int i = 0; i < ColorCupIndividualFormat.FieldSize; i++)
        {
            counts[i] = qual.RoundPlaceCounts[i];
        }

        for (int i = 0; i < ColorCupIndividualFormat.FinalFieldSize; i++)
        {
            checked
            {
                counts[i] += final.RoundPlaceCounts[i];
            }
        }

        return counts;
    }

    private static List<StageAthleteTotals> SortByScore(IReadOnlyList<StageAthleteTotals> totals)
    {
        List<StageAthleteTotals> byScore = [.. totals];
        byScore.Sort(static (left, right) =>
        {
            int score = right.StageScoreThousandths.CompareTo(left.StageScoreThousandths);
            return score != 0 ? score : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });
        return byScore;
    }

    private static void ValidateQualifyingOrder(
        IReadOnlyList<StageAthleteTotals> totals,
        IReadOnlyList<int> qualifyingOrderAthleteIds)
    {
        if (qualifyingOrderAthleteIds.Count != ColorCupIndividualFormat.FieldSize)
        {
            throw new InvalidOperationException(
                $"Color Cup qualifying order requires exactly {ColorCupIndividualFormat.FieldSize} athletes, was {qualifyingOrderAthleteIds.Count}.");
        }

        HashSet<int> totalsIds = totals.Select(t => t.AthleteId).ToHashSet();
        HashSet<int> orderIds = new();
        foreach (int id in qualifyingOrderAthleteIds)
        {
            if (!totalsIds.Contains(id))
            {
                throw new InvalidOperationException($"Color Cup qualifying order references unknown athlete {id}.");
            }

            if (!orderIds.Add(id))
            {
                throw new InvalidOperationException($"Color Cup qualifying order contains duplicate athlete id {id}.");
            }
        }
    }

    private static List<StageAthleteTotals> SelectFinalists(
        Dictionary<int, StageAthleteTotals> byId,
        IReadOnlyList<int> qualifyingOrderAthleteIds)
    {
        HashSet<int> qualifierIds = qualifyingOrderAthleteIds.Take(ColorCupIndividualFormat.CutoffRank).ToHashSet();
        List<StageAthleteTotals> finalists = new(ColorCupIndividualFormat.CutoffRank);
        foreach (int id in qualifierIds)
        {
            StageAthleteTotals entry = byId[id];
            if (SumCounts(entry) != ColorCupIndividualFormat.QualifyingRounds + ColorCupIndividualFormat.FinalRounds)
            {
                throw new InvalidOperationException($"Color Cup finalist '{entry.Name}' must appear in all 32 rounds.");
            }

            finalists.Add(entry);
        }

        return finalists;
    }

    private static List<StageAthleteTotals> SelectEliminated(
        Dictionary<int, StageAthleteTotals> byId,
        IReadOnlyList<int> qualifyingOrderAthleteIds)
    {
        List<StageAthleteTotals> eliminated = new(ColorCupIndividualFormat.FieldSize - ColorCupIndividualFormat.CutoffRank);
        foreach (int id in qualifyingOrderAthleteIds.Skip(ColorCupIndividualFormat.CutoffRank))
        {
            StageAthleteTotals entry = byId[id];
            if (SumCounts(entry) != ColorCupIndividualFormat.QualifyingRounds)
            {
                throw new InvalidOperationException($"Color Cup eliminated athlete '{entry.Name}' must stop at the 16-round cutoff.");
            }

            eliminated.Add(entry);
        }

        return eliminated;
    }

    private static void CheckFinalistsOutrankEliminated(
        List<StageAthleteTotals> finalists,
        List<StageAthleteTotals> eliminated)
    {
        int minFinalist = finalists.Min(t => t.StageScoreThousandths);
        int maxEliminated = eliminated.Max(t => t.StageScoreThousandths);
        if (minFinalist <= maxEliminated)
        {
            throw new InvalidOperationException(
                "Color Cup finalists must outrank eliminated athletes on combined totals with full carryover.");
        }
    }

    private static IReadOnlyList<StageRankedAthlete> RankScoreGroups(
        List<StageAthleteTotals> byScore,
        Pcg32V1 rng,
        RulesV1 rules,
        LeagueLevel level)
    {
        List<StageRankedAthlete> ranked = new(byScore.Count);
        int index = 0;
        while (index < byScore.Count)
        {
            int runEnd = FindScoreRunEnd(byScore, index);
            List<StageAthleteTotals> group = byScore.GetRange(index, runEnd - index);
            IReadOnlyList<StageAthleteTotals> ordered = OrderTiedGroup(group, rng);
            foreach (StageAthleteTotals entry in ordered)
            {
                ranked.Add(AwardStanding(ranked.Count + 1, entry, rules, level));
            }

            index = runEnd;
        }

        return ranked;
    }

    private static int FindScoreRunEnd(List<StageAthleteTotals> byScore, int start)
    {
        int runEnd = start + 1;
        while (runEnd < byScore.Count &&
            byScore[runEnd].StageScoreThousandths == byScore[start].StageScoreThousandths)
        {
            runEnd++;
        }

        return runEnd;
    }

    private static List<List<StageRoundEntry>> ToRoundEntries(IReadOnlyList<ColorCupIndividualRoundPayloadDocument> payloads)
    {
        List<List<StageRoundEntry>> roundEntries = new(payloads.Count);
        foreach (ColorCupIndividualRoundPayloadDocument payload in payloads)
        {
            roundEntries.Add(ToStageEntries(payload));
        }

        return roundEntries;
    }

    private static void ValidateMergedTotals(IReadOnlyList<StageAthleteTotals> totals)
    {
        if (totals.Count != ColorCupIndividualFormat.FieldSize)
        {
            throw new InvalidOperationException($"Color Cup phased totals require exactly {ColorCupIndividualFormat.FieldSize} athletes, was {totals.Count}.");
        }

        CheckMergedIdentities(totals);
        CheckMergedAppearances(totals);
    }

    private static void CheckMergedIdentities(IReadOnlyList<StageAthleteTotals> totals)
    {
        HashSet<int> ids = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (StageAthleteTotals entry in totals)
        {
            if (entry.AthleteId <= 0)
            {
                throw new InvalidOperationException($"Color Cup phased totals contain invalid athlete id {entry.AthleteId}.");
            }

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                throw new InvalidOperationException("Color Cup phased totals contain an athlete with an empty name.");
            }

            if (!ids.Add(entry.AthleteId))
            {
                throw new InvalidOperationException($"Color Cup phased totals contain duplicate athlete id {entry.AthleteId}.");
            }

            if (!names.Add(entry.Name))
            {
                throw new InvalidOperationException($"Color Cup phased totals contain duplicate athlete '{entry.Name}'.");
            }

            if (entry.StageScoreThousandths < 0 || entry.BaseScoreThousandths < 0)
            {
                throw new InvalidOperationException($"Color Cup phased totals for '{entry.Name}' cannot be negative.");
            }

            if (entry.RoundPlaceCounts.Count != ColorCupIndividualFormat.FieldSize)
            {
                throw new InvalidOperationException($"Color Cup phased totals for '{entry.Name}' must have {ColorCupIndividualFormat.FieldSize} round-place counts, was {entry.RoundPlaceCounts.Count}.");
            }
        }
    }

    private static void CheckMergedAppearances(IReadOnlyList<StageAthleteTotals> totals)
    {
        int finalists = 0;
        int eliminated = 0;
        foreach (StageAthleteTotals entry in totals)
        {
            int sum = SumCounts(entry);
            if (sum == ColorCupIndividualFormat.QualifyingRounds + ColorCupIndividualFormat.FinalRounds)
            {
                finalists++;
            }
            else if (sum == ColorCupIndividualFormat.QualifyingRounds)
            {
                eliminated++;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Color Cup phased totals for '{entry.Name}' sum to {sum} round appearances, expected {ColorCupIndividualFormat.QualifyingRounds} or {ColorCupIndividualFormat.QualifyingRounds + ColorCupIndividualFormat.FinalRounds}.");
            }
        }

        if (finalists != ColorCupIndividualFormat.CutoffRank || eliminated != ColorCupIndividualFormat.FieldSize - ColorCupIndividualFormat.CutoffRank)
        {
            throw new InvalidOperationException(
                $"Color Cup phased totals require exactly {ColorCupIndividualFormat.CutoffRank} finalists and {ColorCupIndividualFormat.FieldSize - ColorCupIndividualFormat.CutoffRank} eliminated, was {finalists}/{eliminated}.");
        }
    }

    private static int SumCounts(StageAthleteTotals entry)
    {
        int sum = 0;
        foreach (int count in entry.RoundPlaceCounts)
        {
            if (count < 0)
            {
                throw new InvalidOperationException($"Color Cup phased totals for '{entry.Name}' contain a negative placement count.");
            }

            checked
            {
                sum += count;
            }
        }

        return sum;
    }

    private static int CompareQualifying(StageAthleteTotals left, StageAthleteTotals right)
    {
        int score = right.StageScoreThousandths.CompareTo(left.StageScoreThousandths);
        if (score != 0)
        {
            return score;
        }

        int deterministic = CompareDeterministicPart(left, right);
        if (deterministic != 0)
        {
            return deterministic;
        }

        return string.Compare(left.Name, right.Name, StringComparison.Ordinal);
    }

    private static int CompareDeterministicPart(StageAthleteTotals left, StageAthleteTotals right)
    {
        for (int i = 0; i < ColorCupIndividualFormat.FieldSize; i++)
        {
            int l = left.RoundPlaceCounts[i];
            int r = right.RoundPlaceCounts[i];
            if (l != r)
            {
                return l > r ? -1 : 1;
            }
        }

        if (left.BaseScoreThousandths != right.BaseScoreThousandths)
        {
            return left.BaseScoreThousandths > right.BaseScoreThousandths ? -1 : 1;
        }

        return 0;
    }

    private static IReadOnlyList<StageAthleteTotals> OrderTiedGroup(
        List<StageAthleteTotals> group,
        Pcg32V1 rng)
    {
        if (group.Count == 1)
        {
            return group;
        }

        IReadOnlyList<RankedEntry<StageAthleteTotals>> ranked = TieBreaker.RankWithSeededDraw(
            group,
            entry => new DeterministicTieBreakKey(
                [],
                entry.RoundPlaceCounts,
                entry.BaseScoreThousandths),
            entry => entry.Name,
            rng);
        return ranked.Select(r => r.Entry).ToList();
    }

    private static StageRankedAthlete AwardStanding(int rank, StageAthleteTotals entry, RulesV1 rules, LeagueLevel level)
    {
        int championship = rules.ScoringTable[rank - 1] * RulesV1.FixedScale;
        int earned = checked(ComputeRoundBonusTotal(entry, rules, level) +
            ScoringCalculator.StageBonusForPosition(rank, rules, level).Thousandths);
        return new StageRankedAthlete(
            entry.AthleteId,
            entry.Name,
            rank,
            entry.StageScoreThousandths,
            entry.BaseScoreThousandths,
            championship,
            entry.RoundWins,
            entry.RoundPlaceCounts,
            earned);
    }

    private static int ComputeRoundBonusTotal(StageAthleteTotals entry, RulesV1 rules, LeagueLevel level)
    {
        int total = 0;
        checked
        {
            for (int position = 1; position <= entry.RoundPlaceCounts.Count; position++)
            {
                int count = entry.RoundPlaceCounts[position - 1];
                if (count == 0)
                {
                    continue;
                }

                int perFinish = ScoringCalculator.RoundBonusForPosition(position, rules, level).Thousandths;
                total += checked(count * perFinish);
            }
        }

        return total;
    }
}
