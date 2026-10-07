using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Tournament-stage field: one competition field (legacy single-field, direct
/// Final, one qualification group, or the Final) with its own team list,
/// rank-group partitions, rosters and deterministic team ids. Each stage uses
/// the existing four rank-group by eight-round format independently.
/// </summary>
internal sealed record TypeCupStageField(
    TypeCupTournamentPlan.StageKey Key,
    int FieldSize,
    Dictionary<int, List<TypeCupSelectionEntity>> Groups,
    Dictionary<int, List<AdvanceRoundHandler.MemberRow>> Rosters,
    Dictionary<string, int> TeamIds,
    IReadOnlyList<string> TeamTypes);
