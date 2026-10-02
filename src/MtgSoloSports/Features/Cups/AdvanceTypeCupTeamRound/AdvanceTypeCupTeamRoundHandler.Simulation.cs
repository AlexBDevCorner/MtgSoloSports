using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.AdvanceTypeCupTeamRound;

public sealed partial class AdvanceTypeCupTeamRoundHandler
{
    internal static List<TeamEvent.TeamLegRanked> CompleteGroup(
        SaveDbContext context,
        AdvancePreparation preparation,
        AdvanceStep step,
        List<TypeCupTeamRoundPayloadDocument> groupPayloads,
        ref Pcg32State current)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(groupPayloads);
        List<List<TeamEvent.TeamGroupRoundEntry>> groupRounds = groupPayloads
            .OrderBy(p => p.RoundNumber)
            .Select(p => RunTypeCupTeamHandler.ToGroupEntries(p, preparation.Groups[step.GroupNumber], step.TeamIds))
            .ToList();
        IReadOnlyList<TeamEvent.TeamLegTotals> totals = TeamEvent.AccumulateLeg(
            groupRounds, preparation.TeamCount, groupPayloads.Count, preparation.Rules);
        Pcg32V1 legRng = Pcg32V1.Restore(current);
        IReadOnlyList<TeamEvent.TeamLegRanked> ranked = TeamEvent.RankLeg(totals, legRng, preparation.Rules, preparation.TeamCount);
        TypeCupTeamInvariants.ValidateCompletedLeg(ranked, totals, step.GroupNumber, preparation.Rules, preparation.TeamCount);

        Dictionary<int, TypeCupSelectionEntity> selectionByAthlete = preparation.Selection.ToDictionary(e => e.SaveAthleteId);
        foreach (TeamEvent.TeamLegRanked leg in ranked)
        {
            RunTypeCupTeamHandler.PersistSingleLeg(context, preparation.Source, leg, selectionByAthlete);
        }

        current = legRng.Snapshot();
        context.ApplyRngState(current);
        return ranked.ToList();
    }

    internal static async Task<RunTypeCupTeamHandler.TeamSimulation> CompleteEventAsync(
        SaveDbContext context,
        Guid saveId,
        AdvancePreparation preparation,
        AdvanceStep step,
        TypeCupTeamRoundPayloadDocument payload,
        List<TeamEvent.TeamLegRanked> freshLegs,
        Pcg32State current,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(freshLegs);
        List<TeamEvent.TeamLegRanked> priorLegs = await ReconstructPriorLegsAsync(context, preparation, step, cancellationToken).ConfigureAwait(false);
        priorLegs.AddRange(freshLegs);

        IReadOnlyList<TeamEvent.TeamScoreInput> teamInputs = TeamEvent.BuildTeamInputs(priorLegs, preparation.TeamCount);
        Pcg32V1 teamRng = Pcg32V1.Restore(current);
        IReadOnlyList<TeamEvent.TeamRanked> rankedTeams = TeamEvent.RankTeams(teamInputs, teamRng);
        TypeCupTeamInvariants.ValidateTeams(rankedTeams, priorLegs, preparation.Rules, preparation.TeamCount);
        Pcg32State rngAfter = teamRng.Snapshot();
        List<TypeCupTeamRoundPayloadDocument> allPayloads = step.Rounds
            .Select(row => TypeCupTeamRoundPayloadDocument.FromStored(row.PayloadJson))
            .ToList();
        allPayloads.Add(payload);
        string checksum = RunTypeCupTeamHandler.ComputeChecksum(rankedTeams);
        RunTypeCupTeamHandler.TeamSimulation simulation = new(allPayloads, priorLegs, rankedTeams, rngAfter, checksum);

        RunTypeCupTeamHandler.PersistTeamRows(context, preparation.Source, simulation);
        RunTypeCupTeamHandler.PersistChampionHonours(context, preparation.Source, simulation);
        await RunTypeCupTeamHandler.ApplyNationalityAsync(context, preparation.Selection, cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        metadata.Phase = Features.Saves.SavePhaseParser.ToText(Features.Saves.SavePhase.CupComplete);
        context.ApplyRngState(rngAfter);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await RunTypeCupTeamHandler.ValidatePersistedAsync(
            context, preparation.Source, simulation, preparation.Rules,
            preparation.StageCountBefore, preparation.SeasonCountBefore, preparation.RoundCountBefore,
            preparation.LifetimeBefore, preparation.EffectiveBefore, preparation.ChampionshipBefore,
            cancellationToken).ConfigureAwait(false);
        await RunTypeCupTeamHandler.EmitTeamStoriesAsync(context, preparation.Source, simulation, cancellationToken).ConfigureAwait(false);
        return simulation;
    }
}
