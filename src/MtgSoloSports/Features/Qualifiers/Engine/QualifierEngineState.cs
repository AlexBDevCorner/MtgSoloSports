using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Qualifiers.Engine;

/// <summary>
/// Everything the shared qualifier engine needs to simulate and persist one
/// qualifier event (MSS-150). Each pipeline's loader builds this from its own
/// guards and field selection; the engine itself is boundary-agnostic.
/// <see cref="FieldSize"/>, <see cref="TotalRounds"/> and <see cref="Winners"/>
/// come from each path's own rule properties (32/16/8 for the Superleague
/// qualifier, 16/16/8 for feeder boundaries). <see cref="RankingLevel"/>
/// preserves each path's earned-bonus scale: the Superleague qualifier ranks
/// at the Feeder 1 baseline by design, feeder boundaries at their higher tier.
/// </summary>
internal sealed record QualifierEngineState(
    RulesV1 Rules,
    SaveMetadataEntity Metadata,
    SeasonEntity Source,
    SeasonEntity Next,
    QualifierBoundary Boundary,
    int SportingColor,
    int FieldSize,
    int TotalRounds,
    int Winners,
    LeagueLevel RankingLevel,
    IReadOnlyList<QualifierProvenance> Field,
    List<AdvanceRoundHandler.MemberRow> Roster,
    Dictionary<int, Bonus> ActiveBonuses,
    List<QualifierRoundPayload> Played,
    Pcg32State Rng);
