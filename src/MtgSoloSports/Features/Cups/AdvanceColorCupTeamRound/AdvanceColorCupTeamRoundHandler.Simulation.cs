using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.AdvanceColorCupTeamRound;

public sealed partial class AdvanceColorCupTeamRoundHandler
{
    internal static List<TeamEvent.TeamLegRanked> CompleteGroup(
        SaveDbContext context,
        AdvancePreparation preparation,
        AdvanceStep step,
        List<ColorCupTeamRoundPayloadDocument> groupPayloads,
        ref Pcg32State current)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(groupPayloads);
        List<List<TeamEvent.TeamGroupRoundEntry>> groupRounds = groupPayloads
            .OrderBy(p => p.RoundNumber)
            .Select(p => RunColorCupTeamHandler.ToGroupEntries(p, preparation.Groups[step.GroupNumber]))
            .ToList();
        IReadOnlyList<TeamEvent.TeamLegTotals> totals = TeamEvent.AccumulateLeg(
            groupRounds, preparation.Rules.ColorCupColorCount, groupPayloads.Count, preparation.Rules);
        Pcg32V1 legRng = Pcg32V1.Restore(current);
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked = TeamEvent.RankLeg(
            totals, legRng, preparation.Rules, preparation.Rules.ColorCupColorCount);
        ColorCupTeamInvariants.ValidateCompletedLeg(ranked, totals, step.GroupNumber, preparation.Rules);

        Dictionary<int, ColorCupSelectionEntity> selectionByAthlete = preparation.Selection.ToDictionary(e => e.SaveAthleteId);
        foreach (TeamEvent.TeamLegRanked leg in ranked)
        {
            RunColorCupTeamHandler.PersistSingleLeg(context, preparation.Source, leg, selectionByAthlete);
        }

        current = legRng.Snapshot();
        context.ApplyRngState(current);
        return ranked.ToList();
    }

    internal static async Task<RunColorCupTeamHandler.TeamSimulation> CompleteEventAsync(
        SaveDbContext context,
        Guid saveId,
        AdvancePreparation preparation,
        AdvanceStep step,
        ColorCupTeamRoundPayloadDocument payload,
        List<TeamEvent.TeamLegRanked> freshLegs,
        Pcg32State current,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(freshLegs);
        List<TeamEvent.TeamLegRanked> priorLegs = await ReconstructPriorLegsAsync(context, preparation, cancellationToken).ConfigureAwait(false);
        priorLegs.AddRange(freshLegs);

        IReadOnlyList<TeamEvent.TeamScoreInput> teamInputs = TeamEvent.BuildTeamInputs(priorLegs, preparation.Rules.ColorCupColorCount);
        Pcg32V1 teamRng = Pcg32V1.Restore(current);
        IReadOnlyList<TeamEvent.TeamRanked> rankedTeams = TeamEvent.RankTeams(teamInputs, teamRng);
        ColorCupTeamInvariants.ValidateTeams(rankedTeams, priorLegs, preparation.Rules);
        Pcg32State rngAfter = teamRng.Snapshot();
        List<ColorCupTeamRoundPayloadDocument> allPayloads = step.Rounds
            .Select(row => ColorCupTeamRoundPayloadDocument.FromStored(row.PayloadJson))
            .ToList();
        allPayloads.Add(payload);
        string checksum = RunColorCupTeamHandler.ComputeChecksum(rankedTeams);
        RunColorCupTeamHandler.TeamSimulation simulation = new(allPayloads, priorLegs, rankedTeams, rngAfter, checksum);

        RunColorCupTeamHandler.PersistTeamRows(context, preparation.Source, simulation);
        RunColorCupTeamHandler.PersistChampionHonours(context, preparation.Source, simulation);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.CupComplete);
        context.ApplyRngState(rngAfter);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await RunColorCupTeamHandler.ValidatePersistedAsync(
            context, preparation.Source, simulation, preparation.Rules,
            preparation.StageCountBefore, preparation.SeasonCountBefore, preparation.RoundCountBefore,
            preparation.LifetimeBefore, preparation.EffectiveBefore, preparation.ChampionshipBefore,
            cancellationToken).ConfigureAwait(false);
        await RunColorCupTeamHandler.EmitTeamStoriesAsync(context, preparation.Source, simulation, cancellationToken).ConfigureAwait(false);
        return simulation;
    }
}
