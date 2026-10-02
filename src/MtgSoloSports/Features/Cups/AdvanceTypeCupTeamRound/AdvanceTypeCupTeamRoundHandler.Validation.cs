using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.AdvanceTypeCupTeamRound;

public sealed partial class AdvanceTypeCupTeamRoundHandler
{
    internal static void ValidatePartialLegs(
        List<TypeCupTeamGroupStandingEntity> legs,
        List<TypeCupSelectionEntity> selection,
        int currentGroup,
        int teamCount)
    {
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(selection);
        HashSet<int> memberIds = selection.Select(e => e.SaveAthleteId).ToHashSet();
        foreach (TypeCupTeamGroupStandingEntity leg in legs)
        {
            if (leg.GroupNumber < 1 || leg.GroupNumber >= currentGroup)
            {
                throw new InvalidOperationException(
                    $"Type Cup team event holds standings for group {leg.GroupNumber} before group {currentGroup} is complete; the event state is corrupt.");
            }

            if (!memberIds.Contains(leg.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Type Cup team leg for athlete {leg.SaveAthleteId} has no selection provenance.");
            }

            if (leg.SelectionRank != leg.GroupNumber)
            {
                throw new InvalidOperationException(
                    $"Type Cup team leg {leg.Id} selection rank #{leg.SelectionRank} does not match group {leg.GroupNumber}.");
            }
        }

        foreach (int completed in Enumerable.Range(1, currentGroup - 1))
        {
            List<int> ranks = legs
                .Where(l => l.GroupNumber == completed)
                .Select(l => l.GroupRank)
                .OrderBy(r => r)
                .ToList();
            if (!ranks.SequenceEqual(Enumerable.Range(1, teamCount)))
            {
                throw new InvalidOperationException(
                    $"Type Cup team group {completed} holds {ranks.Count} leg standings, expected exactly ranks 1..{teamCount}.");
            }
        }
    }

    /// <summary>
    /// Replays the RNG chain over all persisted rounds and completed-group leg
    /// rankings, verifying every payload links to its predecessor (round
    /// RngBefore equals the previous tip; group leg rankings consume RNG
    /// exactly as at creation time) and that the global RNG row equals the
    /// replayed tip. With no persisted rounds the current RNG row is the tip.
    /// Any divergence aborts instead of forking the deterministic stream.
    /// </summary>
    internal static Pcg32State VerifyRngChain(
        List<TypeCupTeamRoundEntity> rounds,
        AdvancePreparation preparation,
        Dictionary<string, int> teamIds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(teamIds);
        if (rounds.Count == 0)
        {
            return preparation.RngBefore;
        }

        List<TypeCupTeamRoundPayloadDocument> documents = rounds
            .Select(row => TypeCupTeamRoundPayloadDocument.FromStored(row.PayloadJson))
            .ToList();
        Pcg32State tip = ReplayRngTip(documents, preparation, teamIds);
        if (preparation.RngBefore.State != tip.State || preparation.RngBefore.Stream != tip.Stream)
        {
            throw new InvalidOperationException(
                $"Type Cup team event for Season {preparation.Source.SeasonNumber} resumes from a different RNG state than its last persisted round; refusing to diverge.");
        }

        return tip;
    }

    internal static Pcg32State ReplayRngTip(
        List<TypeCupTeamRoundPayloadDocument> documents,
        AdvancePreparation preparation,
        Dictionary<string, int> teamIds)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(teamIds);
        Pcg32State tip = new(0UL, 0UL);
        bool hasTip = false;
        int index = 0;
        foreach (int groupNumber in Enumerable.Range(1, preparation.Rules.TypeCupMinTeamSize))
        {
            List<List<TeamEvent.TeamGroupRoundEntry>> groupRounds = new(preparation.Rules.TypeCupGroupRounds);
            foreach (int roundNumber in Enumerable.Range(1, preparation.Rules.TypeCupGroupRounds))
            {
                if (index >= documents.Count)
                {
                    return tip;
                }

                TypeCupTeamRoundPayloadDocument document = documents[index];
                if (document.GroupNumber != groupNumber || document.RoundNumber != roundNumber)
                {
                    throw new InvalidOperationException(
                        $"Type Cup team RNG replay found group {document.GroupNumber} round {document.RoundNumber}, expected group {groupNumber} round {roundNumber}.");
                }

                if (!hasTip)
                {
                    tip = new Pcg32State(document.RngBeforeState, document.RngBeforeStream);
                    hasTip = true;
                }
                else if (tip.State != document.RngBeforeState || tip.Stream != document.RngBeforeStream)
                {
                    throw new InvalidOperationException(
                        $"Type Cup team group {groupNumber} round {roundNumber} breaks the persisted RNG chain.");
                }

                tip = new Pcg32State(document.RngAfterState, document.RngAfterStream);
                groupRounds.Add(RunTypeCupTeamHandler.ToGroupEntries(
                    document, preparation.Groups[groupNumber], teamIds));
                index++;
            }

            tip = ReplayGroupLegRng(groupRounds, preparation, tip);
        }

        return tip;
    }

    internal static Pcg32State ReplayGroupLegRng(
        List<List<TeamEvent.TeamGroupRoundEntry>> groupRounds,
        AdvancePreparation preparation,
        Pcg32State tip)
    {
        ArgumentNullException.ThrowIfNull(groupRounds);
        ArgumentNullException.ThrowIfNull(preparation);
        if (groupRounds.Count != preparation.Rules.TypeCupGroupRounds)
        {
            return tip;
        }

        int groupSize = groupRounds[0].Count;
        IReadOnlyList<TeamEvent.TeamLegTotals> totals = TeamEvent.AccumulateLeg(
            groupRounds, groupSize, preparation.Rules.TypeCupGroupRounds, preparation.Rules);
        Pcg32V1 legRng = Pcg32V1.Restore(tip);
        _ = TeamEvent.RankLeg(totals, legRng, preparation.Rules, groupSize);
        return legRng.Snapshot();
    }

    internal static Dictionary<int, Points> BuildGroupCumulative(
        List<TypeCupTeamRoundEntity> rounds,
        int groupNumber,
        List<AdvanceRoundHandler.MemberRow> roster,
        SeasonEntity source)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(source);
        HashSet<int> rosterIds = roster.Select(r => r.AthleteId).ToHashSet();
        Dictionary<int, Points> cumulative = new(roster.Count);
        bool first = true;
        foreach (TypeCupTeamRoundEntity row in rounds.Where(r => r.GroupNumber == groupNumber))
        {
            AccumulateStoredRound(row, groupNumber, rosterIds, cumulative, ref first);
        }

        return cumulative;
    }

    internal static void AccumulateStoredRound(
        TypeCupTeamRoundEntity row,
        int groupNumber,
        HashSet<int> rosterIds,
        Dictionary<int, Points> cumulative,
        ref bool first)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rosterIds);
        ArgumentNullException.ThrowIfNull(cumulative);
        TypeCupTeamRoundPayloadDocument document = TypeCupTeamRoundPayloadDocument.FromStored(row.PayloadJson);
        HashSet<int> placementIds = new();
        foreach (var placement in document.Placements)
        {
            CheckCumulativePlacement(row, groupNumber, rosterIds, cumulative, first, placement, placementIds);
            cumulative[placement.AthleteId] = Points.FromThousandths(placement.CumulativeAfterThousandths);
        }

        if (placementIds.Count != rosterIds.Count)
        {
            throw new InvalidOperationException(
                $"Type Cup team group {groupNumber} round {row.RoundNumber} must contain exactly {rosterIds.Count} athletes, was {placementIds.Count}.");
        }

        first = false;
    }

    internal static void CheckCumulativePlacement(
        TypeCupTeamRoundEntity row,
        int groupNumber,
        HashSet<int> rosterIds,
        Dictionary<int, Points> cumulative,
        bool first,
        RoundPayloadEntry placement,
        HashSet<int> placementIds)
    {
        if (!rosterIds.Contains(placement.AthleteId))
        {
            throw new InvalidOperationException(
                $"Type Cup team group {groupNumber} round {row.RoundNumber} contains athlete {placement.AthleteId} from the wrong rank group.");
        }

        if (!placementIds.Add(placement.AthleteId))
        {
            throw new InvalidOperationException(
                $"Type Cup team group {groupNumber} round {row.RoundNumber} contains duplicate athlete id {placement.AthleteId}.");
        }

        Points before = Points.FromThousandths(placement.CumulativeBeforeThousandths);
        if (first)
        {
            if (before.Thousandths != 0)
            {
                throw new InvalidOperationException(
                    $"Type Cup team group {groupNumber} round {row.RoundNumber} does not start its cumulative chain at zero.");
            }

            return;
        }

        if (!cumulative.TryGetValue(placement.AthleteId, out Points expected) || expected.Thousandths != before.Thousandths)
        {
            throw new InvalidOperationException(
                $"Type Cup team group {groupNumber} round {row.RoundNumber} breaks the cumulative chain for '{placement.Name}'.");
        }
    }

    internal static async Task<List<TeamEvent.TeamLegRanked>> ReconstructPriorLegsAsync(
        SaveDbContext context,
        AdvancePreparation preparation,
        AdvanceStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(step);
        List<TypeCupTeamGroupStandingEntity> entities = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == preparation.Source.Id)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.GroupRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<int> participantIds = preparation.Selection.Select(e => e.SaveAthleteId).ToList();
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => participantIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        List<TeamEvent.TeamLegRanked> legs = new(entities.Count);
        foreach (TypeCupTeamGroupStandingEntity leg in entities)
        {
            legs.Add(ReconstructLeg(leg, names, step.TeamIds));
        }

        return legs;
    }

    internal static TeamEvent.TeamLegRanked ReconstructLeg(
        TypeCupTeamGroupStandingEntity leg,
        Dictionary<int, string> names,
        Dictionary<string, int> teamIds)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(teamIds);
        if (!names.TryGetValue(leg.SaveAthleteId, out string? name))
        {
            throw new InvalidOperationException($"Type Cup team leg for athlete {leg.SaveAthleteId} references an unknown athlete.");
        }

        if (!teamIds.TryGetValue(leg.CreatureType, out int teamId))
        {
            throw new InvalidOperationException($"Type Cup team '{leg.CreatureType}' has no deterministic team id.");
        }

        List<int>? counts = JsonSerializer.Deserialize<List<int>>(leg.RoundPlaceCountsJson);
        if (counts is null || counts.Count == 0)
        {
            throw new InvalidOperationException($"Type Cup team leg for '{name}' has corrupt round-place counts.");
        }

        return new TeamEvent.TeamLegRanked(
            leg.SaveAthleteId,
            name,
            teamId,
            leg.CreatureType,
            leg.GroupRank,
            leg.GroupScoreThousandths,
            leg.BaseScoreThousandths,
            leg.RoundWins,
            counts);
    }

    internal static async Task ValidatePartialAsync(
        SaveDbContext context,
        AdvancePreparation preparation,
        AdvanceStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(step);
        int expectedRounds = step.Rounds.Count + 1;
        int persistedRounds = await context.TypeCupTeamRounds
            .CountAsync(e => e.SourceSeasonId == preparation.Source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (persistedRounds != expectedRounds)
        {
            throw new InvalidOperationException(
                $"Type Cup team event for Season {preparation.Source.SeasonNumber} persisted {persistedRounds} rounds, expected {expectedRounds}.");
        }

        if (step.RoundNumber == preparation.Rules.TypeCupGroupRounds)
        {
            List<int> ranks = await context.TypeCupTeamGroupStandings
                .Where(e => e.SourceSeasonId == preparation.Source.Id && e.GroupNumber == step.GroupNumber)
                .Select(e => e.GroupRank)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!ranks.OrderBy(r => r).SequenceEqual(Enumerable.Range(1, preparation.TeamCount)))
            {
                throw new InvalidOperationException(
                    $"Type Cup team group {step.GroupNumber} completed without exactly one leg standing per rank.");
            }
        }

        bool hasTeams = await context.TypeCupTeamStandings
            .AnyAsync(e => e.SourceSeasonId == preparation.Source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (hasTeams)
        {
            throw new InvalidOperationException(
                $"Type Cup team event for Season {preparation.Source.SeasonNumber} holds team standings before the final round.");
        }
    }
}
