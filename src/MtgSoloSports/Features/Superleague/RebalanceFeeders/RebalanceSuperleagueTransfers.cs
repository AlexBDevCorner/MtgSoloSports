using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Derives Superleague departures/returns for the feeder-rebalance reveal from
/// persisted historical rows only (never resimulates).
/// A departure is a source-feeder athlete occupying the next Superleague after
/// all automatic/qualifier outcomes; a return is a source-Superleague athlete
/// occupying a next feeder. Challenger losers and retained incumbents never
/// leave their league type and are therefore not transfers.
/// </summary>
public static class RebalanceSuperleagueTransfers
{
    public const string DepartureKind = "SuperleagueDeparture";

    public const string ReturnKind = "SuperleagueReturn";

    public static (IReadOnlyList<RebalanceMovementMember> Departed, IReadOnlyList<RebalanceMovementMember> Returned) Map(
        IReadOnlyList<LeagueEntity> sourceFeeders,
        LeagueEntity? sourceSuperleague,
        IReadOnlyList<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague,
        IReadOnlyList<SeasonMembershipEntity> sourceMemberships,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        IReadOnlyDictionary<int, int> sourceRankByAthlete,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyDictionary<int, string?> images,
        IReadOnlyDictionary<int, LeagueEntity> leaguesById)
    {
        ArgumentNullException.ThrowIfNull(sourceFeeders);
        ArgumentNullException.ThrowIfNull(nextFeeders);
        ArgumentNullException.ThrowIfNull(nextSuperleague);
        ArgumentNullException.ThrowIfNull(sourceMemberships);
        ArgumentNullException.ThrowIfNull(nextMemberships);
        ArgumentNullException.ThrowIfNull(sourceRankByAthlete);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(leaguesById);

        Dictionary<int, SeasonMembershipEntity> nextByAthlete = nextMemberships.ToDictionary(m => m.SaveAthleteId);
        TransferSets sets = CollectTransfers(
            sourceFeeders, sourceSuperleague, nextFeeders, nextSuperleague,
            sourceMemberships, nextByAthlete, sourceRankByAthlete, names, images, leaguesById);
        SortTransfers(sets.Departed, static m => m.FromLeagueName);
        SortTransfers(sets.Returned, static m => m.ToLeagueName);
        return (sets.Departed, sets.Returned);
    }

    public static IReadOnlyDictionary<string, (int Departed, int Returned)> CountsByColor(
        IReadOnlyList<RebalanceMovementMember> departed,
        IReadOnlyList<RebalanceMovementMember> returned)
    {
        ArgumentNullException.ThrowIfNull(departed);
        ArgumentNullException.ThrowIfNull(returned);
        Dictionary<string, (int Departed, int Returned)> counts = new(StringComparer.Ordinal);
        foreach (RebalanceMovementMember member in departed)
        {
            counts.TryGetValue(member.SportingColor, out (int Departed, int Returned) current);
            counts[member.SportingColor] = (current.Departed + 1, current.Returned);
        }

        foreach (RebalanceMovementMember member in returned)
        {
            counts.TryGetValue(member.SportingColor, out (int Departed, int Returned) current);
            counts[member.SportingColor] = (current.Departed, current.Returned + 1);
        }

        return counts;
    }

    private sealed record TransferSets(List<RebalanceMovementMember> Departed, List<RebalanceMovementMember> Returned);

    private static TransferSets CollectTransfers(
        IReadOnlyList<LeagueEntity> sourceFeeders,
        LeagueEntity? sourceSuperleague,
        IReadOnlyList<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague,
        IReadOnlyList<SeasonMembershipEntity> sourceMemberships,
        IReadOnlyDictionary<int, SeasonMembershipEntity> nextByAthlete,
        IReadOnlyDictionary<int, int> sourceRankByAthlete,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyDictionary<int, string?> images,
        IReadOnlyDictionary<int, LeagueEntity> leaguesById)
    {
        HashSet<int> sourceFeederIds = sourceFeeders.Select(l => l.Id).ToHashSet();
        HashSet<int> nextFeederIds = nextFeeders.Select(l => l.Id).ToHashSet();
        List<RebalanceMovementMember> departed = new();
        List<RebalanceMovementMember> returned = new();

        foreach (SeasonMembershipEntity source in sourceMemberships.OrderBy(m => m.SaveAthleteId))
        {
            if (!nextByAthlete.TryGetValue(source.SaveAthleteId, out SeasonMembershipEntity? next))
            {
                throw new InvalidOperationException($"Athlete {source.SaveAthleteId} has no next-season membership.");
            }

            if (IsDeparture(source, next, sourceFeederIds, nextSuperleague.Id))
            {
                departed.Add(MapDeparture(source, nextSuperleague, sourceRankByAthlete, names, images, leaguesById));
            }
            else if (IsReturn(source, next, sourceSuperleague, nextFeederIds))
            {
                returned.Add(MapReturn(source, next, sourceRankByAthlete, names, images, leaguesById));
            }
        }

        return new TransferSets(departed, returned);
    }

    private static bool IsDeparture(
        SeasonMembershipEntity source,
        SeasonMembershipEntity next,
        HashSet<int> sourceFeederIds,
        int nextSuperleagueId)
    {
        return source.LeagueId is not null
            && sourceFeederIds.Contains(source.LeagueId.Value)
            && next.LeagueId == nextSuperleagueId;
    }

    private static bool IsReturn(
        SeasonMembershipEntity source,
        SeasonMembershipEntity next,
        LeagueEntity? sourceSuperleague,
        HashSet<int> nextFeederIds)
    {
        return sourceSuperleague is not null
            && source.LeagueId == sourceSuperleague.Id
            && next.LeagueId is not null
            && nextFeederIds.Contains(next.LeagueId.Value);
    }

    private static void SortTransfers(List<RebalanceMovementMember> members, Func<RebalanceMovementMember, string> leagueName)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(leagueName);
        members.Sort((a, b) =>
        {
            int league = string.Compare(leagueName(a), leagueName(b), StringComparison.Ordinal);
            if (league != 0)
            {
                return league;
            }

            int rank = a.FromSeasonRank.CompareTo(b.FromSeasonRank);
            return rank != 0 ? rank : a.AthleteId.CompareTo(b.AthleteId);
        });
    }

    internal static RebalanceMovementMember MapDeparture(
        SeasonMembershipEntity source,
        LeagueEntity nextSuperleague,
        IReadOnlyDictionary<int, int> sourceRankByAthlete,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyDictionary<int, string?> images,
        IReadOnlyDictionary<int, LeagueEntity> leaguesById)
    {
        int rank = RequireSourceRank(sourceRankByAthlete, source.SaveAthleteId, "Departing");
        names.TryGetValue(source.SaveAthleteId, out string? name);
        images.TryGetValue(source.SaveAthleteId, out string? imageUrl);
        leaguesById.TryGetValue(source.LeagueId!.Value, out LeagueEntity? from);
        string color = ((SportingColor)source.SportingColor).ToString();
        return new RebalanceMovementMember(
            source.SaveAthleteId,
            name ?? $"Athlete {source.SaveAthleteId}",
            color,
            source.LeagueId.Value,
            from?.Name ?? $"League {source.LeagueId.Value}",
            nextSuperleague.Id,
            nextSuperleague.Name,
            DepartureKind,
            rank,
            imageUrl);
    }

    internal static RebalanceMovementMember MapReturn(
        SeasonMembershipEntity source,
        SeasonMembershipEntity next,
        IReadOnlyDictionary<int, int> sourceRankByAthlete,
        IReadOnlyDictionary<int, string> names,
        IReadOnlyDictionary<int, string?> images,
        IReadOnlyDictionary<int, LeagueEntity> leaguesById)
    {
        int rank = RequireSourceRank(sourceRankByAthlete, source.SaveAthleteId, "Returning");
        names.TryGetValue(source.SaveAthleteId, out string? name);
        images.TryGetValue(source.SaveAthleteId, out string? imageUrl);
        leaguesById.TryGetValue(source.LeagueId!.Value, out LeagueEntity? from);
        leaguesById.TryGetValue(next.LeagueId!.Value, out LeagueEntity? to);
        string color = ((SportingColor)next.SportingColor).ToString();
        return new RebalanceMovementMember(
            source.SaveAthleteId,
            name ?? $"Athlete {source.SaveAthleteId}",
            color,
            source.LeagueId.Value,
            from?.Name ?? $"League {source.LeagueId.Value}",
            next.LeagueId.Value,
            to?.Name ?? $"League {next.LeagueId.Value}",
            ReturnKind,
            rank,
            imageUrl);
    }

    private static int RequireSourceRank(IReadOnlyDictionary<int, int> ranks, int athleteId, string label)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        if (!ranks.TryGetValue(athleteId, out int rank) || rank < 1 || rank > 32)
        {
            throw new InvalidOperationException($"{label} athlete {athleteId} has corrupt source rank.");
        }

        return rank;
    }
}
