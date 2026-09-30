using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.GetRoundReplay;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.History.EventRounds;

/// <summary>Builds <see cref="EventRoundView"/> from a stored payload's placements.</summary>
public static class EventRoundViews
{
    public static async Task<EventRoundView> BuildAsync(
        SaveDbContext context,
        int seasonNumber,
        string eventKey,
        int? group,
        int roundNumber,
        int rulesVersion,
        string checksum,
        Pcg32State before,
        Pcg32State after,
        IReadOnlyList<RoundPayloadEntry> placements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(placements);
        List<int> athleteIds = placements.Select(p => p.AthleteId).ToList();
        Dictionary<int, SaveAthleteEntity> cards = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => athleteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        string title = PostseasonEvents.Title(eventKey);
        List<HistoryRoundPlacement> mapped = new(placements.Count);
        foreach (RoundPayloadEntry placement in placements.OrderBy(p => p.Position))
        {
            if (!cards.TryGetValue(placement.AthleteId, out SaveAthleteEntity? card))
            {
                throw new InvalidOperationException($"{title} round {roundNumber} references unknown athlete {placement.AthleteId}.");
            }

            mapped.Add(new HistoryRoundPlacement(
                placement.AthleteId,
                placement.Name,
                placement.Position,
                placement.BaseThousandths,
                placement.ActiveBonusThousandths,
                placement.FinalThousandths,
                placement.CumulativeBeforeThousandths,
                placement.CumulativeAfterThousandths,
                placement.RankBefore,
                placement.RankAfter,
                placement.RankMovement,
                card.ImageUrl,
                card.SetCode,
                card.TypeLine));
        }

        return new EventRoundView(
            seasonNumber, eventKey, title, group, roundNumber, rulesVersion, checksum,
            before.State, before.Stream, after.State, after.Stream, mapped);
    }
}
