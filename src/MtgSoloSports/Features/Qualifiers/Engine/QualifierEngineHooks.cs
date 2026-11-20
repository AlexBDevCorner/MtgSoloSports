using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.Qualifiers.Engine;

/// <summary>
/// Per-path completion behavior the shared qualifier engine invokes
/// (MSS-150). Each pipeline supplies its own hooks so genuine behavioral
/// differences stay explicit and owned by their feature instead of silently
/// drifting inside a shared body: per-round and completed-qualifier
/// validation strictness, next-roster destination rules, persisted-state
/// validation, and optional extra story events (the Superleague qualifier
/// additionally emits first-appearance stories; feeder boundaries emit none).
/// </summary>
internal sealed record QualifierEngineHooks(
    Action<QualifierRoundPayload, QualifierEngineState, Pcg32State> ValidateRound,
    Action<IReadOnlyList<StageRankedAthlete>, IReadOnlyList<StageAthleteTotals>, RulesV1> ValidateCompleted,
    Func<SaveDbContext, QualifierEngineState, QualifierEngineSimulation, CancellationToken, Task> ApplyToNextRosterAsync,
    Func<SaveDbContext, QualifierEngineState, QualifierEngineSimulation, CancellationToken, Task> ValidatePersistedAsync,
    Func<SaveDbContext, QualifierEngineState, QualifierEngineSimulation, Dictionary<int, LeagueEntity>, Dictionary<int, SeasonMembershipEntity>, CancellationToken, Task<bool>>? EmitExtraStoriesAsync);
