using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Qualifiers.Engine;

/// <summary>
/// A fully completed qualifier event as computed by the shared engine
/// (MSS-150): the payloads are the source of truth, the ranked standings are
/// derived deterministically from them, and <see cref="RngAfter"/> is the
/// RNG state the caller must commit together with the persisted results.
/// </summary>
internal sealed record QualifierEngineSimulation(
    IReadOnlyList<QualifierProvenance> Field,
    List<QualifierRoundPayload> Payloads,
    IReadOnlyList<StageAthleteTotals> Totals,
    IReadOnlyList<StageRankedAthlete> Ranked,
    Pcg32State RngAfter,
    string Checksum);
