using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.History.EventRounds;

/// <summary>
/// Read-only access to stored postseason event rounds for the History event
/// slices: row listing in (group, round) order, payload decoding and
/// completion state. Never locks, never validates as a runner, never
/// resimulates.
/// </summary>
internal static class HistoryEventRows
{
    internal sealed record StoredRound(int? Group, int Round, int RulesVersion, string Checksum, Pcg32State Before, Pcg32State After, string PayloadJson);

    internal static void ValidateRequest(Guid saveId, int seasonNumber, string eventKey)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(seasonNumber));
        }

        if (!PostseasonEvents.IsKnown(eventKey))
        {
            throw new ArgumentException($"Unknown postseason event '{eventKey}'.", nameof(eventKey));
        }
    }

    internal static async Task<SeasonEntity> LoadSeasonAsync(SaveDbContext context, int seasonNumber, CancellationToken cancellationToken)
    {
        SeasonEntity? season = await context.Seasons.AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber, cancellationToken)
            .ConfigureAwait(false);
        return season ?? throw new HistoryNotFoundException($"Season {seasonNumber} does not exist.");
    }

    internal static async Task<List<StoredRound>> LoadAsync(SaveDbContext context, SeasonEntity season, string key, CancellationToken cancellationToken)
    {
        switch (key)
        {
            case PostseasonEvents.Qualifier:
                SeasonEntity? next = await context.Seasons.AsNoTracking()
                    .SingleOrDefaultAsync(e => e.SeasonNumber == season.SeasonNumber + 1, cancellationToken).ConfigureAwait(false);
                if (next is null)
                {
                    return [];
                }

                return (await context.QualifierRounds.AsNoTracking()
                        .Where(e => e.FromSeasonId == season.Id && e.ToSeasonId == next.Id)
                        .OrderBy(e => e.RoundNumber)
                        .ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(e => Stored(null, e.RoundNumber, e.RulesVersion, e.PayloadChecksum, e.RngBeforeState, e.RngBeforeStream, e.RngAfterState, e.RngAfterStream, e.PayloadJson))
                    .ToList();
            case PostseasonEvents.ColorCupIndividual:
                return (await context.ColorCupIndividualRounds.AsNoTracking()
                        .Where(e => e.SourceSeasonId == season.Id)
                        .OrderBy(e => e.RoundNumber)
                        .ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(e => Stored(null, e.RoundNumber, e.RulesVersion, e.PayloadChecksum, e.RngBeforeState, e.RngBeforeStream, e.RngAfterState, e.RngAfterStream, e.PayloadJson))
                    .ToList();
            case PostseasonEvents.ColorCupTeam:
                return (await context.ColorCupTeamRounds.AsNoTracking()
                        .Where(e => e.SourceSeasonId == season.Id)
                        .OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber)
                        .ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(e => Stored(e.GroupNumber, e.RoundNumber, e.RulesVersion, e.PayloadChecksum, e.RngBeforeState, e.RngBeforeStream, e.RngAfterState, e.RngAfterStream, e.PayloadJson))
                    .ToList();
            default:
                return (await context.TypeCupTeamRounds.AsNoTracking()
                        .Where(e => e.SourceSeasonId == season.Id)
                        .OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber)
                        .ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(e => Stored(e.GroupNumber, e.RoundNumber, e.RulesVersion, e.PayloadChecksum, e.RngBeforeState, e.RngBeforeStream, e.RngAfterState, e.RngAfterStream, e.PayloadJson))
                    .ToList();
        }
    }

    internal static IReadOnlyList<RoundPayloadEntry> DecodePlacements(string key, string stored) => key switch
    {
        PostseasonEvents.Qualifier => QualifierRoundPayloadDocument.FromJson(RoundPayloadCodec.DecodeToJson(stored)).Placements,
        PostseasonEvents.ColorCupIndividual => ColorCupIndividualRoundPayloadDocument.FromStored(stored).Placements,
        PostseasonEvents.ColorCupTeam => ColorCupTeamRoundPayloadDocument.FromStored(stored).Placements,
        _ => TypeCupTeamRoundPayloadDocument.FromStored(stored).Placements,
    };

    internal static async Task<bool> IsCompleteAsync(SaveDbContext context, SeasonEntity season, string key, CancellationToken cancellationToken) => key switch
    {
        PostseasonEvents.Qualifier => await context.QualifierStandings.AnyAsync(e => e.FromSeasonId == season.Id, cancellationToken).ConfigureAwait(false),
        PostseasonEvents.ColorCupIndividual => await context.ColorCupIndividualStandings.AnyAsync(e => e.SourceSeasonId == season.Id, cancellationToken).ConfigureAwait(false),
        PostseasonEvents.ColorCupTeam => await context.ColorCupTeamStandings.AnyAsync(e => e.SourceSeasonId == season.Id, cancellationToken).ConfigureAwait(false),
        _ => await context.TypeCupTeamStandings.AnyAsync(e => e.SourceSeasonId == season.Id, cancellationToken).ConfigureAwait(false),
    };

    private static StoredRound Stored(
        int? group, int round, int rulesVersion, string checksum,
        long beforeState, long beforeStream, long afterState, long afterStream, string payloadJson) =>
        new(
            group,
            round,
            rulesVersion,
            checksum,
            new Pcg32State(unchecked((ulong)beforeState), unchecked((ulong)beforeStream)),
            new Pcg32State(unchecked((ulong)afterState), unchecked((ulong)afterStream)),
            payloadJson);
}
