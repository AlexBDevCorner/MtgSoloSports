using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Builds additive cutoff views from persisted payloads. Legacy returns nulls.
/// Phased resolves the authoritative cutoff via the same orchestrator the
/// runner uses, so the displayed top-16 boundary matches the persisted
/// round-17 roster even when the cutoff splits an exact tie.
/// </summary>
public static class ColorCupIndividualCutoffBuilder
{
    public static (ColorCupIndividualCutoff Cutoff, Dictionary<int, ColorCupIndividualAdvancing> ByAthlete) BuildForCompleted(
        IReadOnlyList<ColorCupIndividualRoundPayloadDocument> payloads,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(rules);
        int formatVersion = ColorCupIndividualInvariants.ResolvePayloadFormat(payloads);
        if (!ColorCupIndividualFormat.IsPhased(formatVersion))
        {
            return (LegacyCutoff(formatVersion), new Dictionary<int, ColorCupIndividualAdvancing>());
        }

        ColorCupIndividualPhasedOrchestrator.CutoffResolution resolution = ResolveCutoff(payloads, rules);
        IReadOnlyList<StageAthleteTotals> merged =
            ColorCupIndividualPhasedOrchestrator.AccumulatePhasedTotals(payloads, rules);
        Dictionary<int, ColorCupIndividualAdvancing> byAthlete = BuildAdvancing(resolution, merged);
        ColorCupIndividualCutoff cutoff = BuildCutoff(formatVersion, resolution);
        return (cutoff, byAthlete);
    }

    private static ColorCupIndividualCutoff LegacyCutoff(int formatVersion) =>
        new(formatVersion, false, null, null, null, null, null, null);

    private static ColorCupIndividualPhasedOrchestrator.CutoffResolution ResolveCutoff(
        IReadOnlyList<ColorCupIndividualRoundPayloadDocument> payloads,
        RulesV1 rules)
    {
        List<ColorCupIndividualRoundPayloadDocument> qualifying = payloads.Take(ColorCupIndividualFormat.QualifyingRounds).ToList();
        Pcg32State round16After = new(qualifying[^1].RngAfterState, qualifying[^1].RngAfterStream);
        return ColorCupIndividualPhasedOrchestrator.ResolveCutoff(qualifying, rules, round16After);
    }

    private static Dictionary<int, ColorCupIndividualAdvancing> BuildAdvancing(
        ColorCupIndividualPhasedOrchestrator.CutoffResolution resolution,
        IReadOnlyList<StageAthleteTotals> merged)
    {
        Dictionary<int, StageAthleteTotals> qualById = resolution.QualifyingTotals.ToDictionary(t => t.AthleteId);
        Dictionary<int, StageAthleteTotals> mergedById = merged.ToDictionary(t => t.AthleteId);
        Dictionary<int, int> orderIndex = resolution.QualifyingOrderAthleteIds
            .Select((id, index) => (id, index))
            .ToDictionary(p => p.id, p => p.index);
        Dictionary<int, ColorCupIndividualAdvancing> byAthlete = new(mergedById.Count);
        foreach (int athleteId in mergedById.Keys)
        {
            byAthlete[athleteId] = BuildOne(athleteId, orderIndex, qualById, mergedById);
        }

        return byAthlete;
    }

    private static ColorCupIndividualAdvancing BuildOne(
        int athleteId,
        Dictionary<int, int> orderIndex,
        Dictionary<int, StageAthleteTotals> qualById,
        Dictionary<int, StageAthleteTotals> mergedById)
    {
        int qualifyingRank = orderIndex[athleteId] + 1;
        bool qualified = qualifyingRank <= ColorCupIndividualFormat.CutoffRank;
        int qualScore = qualById[athleteId].StageScoreThousandths;
        int combined = mergedById[athleteId].StageScoreThousandths;
        int finalScore = checked(combined - qualScore);
        return new ColorCupIndividualAdvancing(qualifyingRank, qualified, qualScore, finalScore);
    }

    private static ColorCupIndividualCutoff BuildCutoff(
        int formatVersion,
        ColorCupIndividualPhasedOrchestrator.CutoffResolution resolution)
    {
        List<int> qualifiers = resolution.QualifierAthleteIds.ToList();
        List<int> eliminated = resolution.QualifyingOrderAthleteIds.Skip(ColorCupIndividualFormat.CutoffRank).ToList();
        return new(
            formatVersion,
            true,
            ColorCupIndividualFormat.CutoffRank,
            ColorCupIndividualFormat.FinalFieldSize,
            resolution.QualifyingOrderAthleteIds.ToList(),
            qualifiers,
            eliminated,
            resolution.TieDrawConsumed);
    }
}
