using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.GetHallOfFame;
using MtgSoloSports.Features.Stories;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal static class UniverseAlmanacLeaderQueries
{
    internal const int WeightedTake = 20;

    internal const int PointsTake = 10;

    internal const int StreakTake = 5;

    internal static async Task<IReadOnlyList<AlmanacCareerRecordEntry>> LoadCareerRecordsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        RecordLoader.RecordInputs inputs = await RecordLoader.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            inputs.AthleteNames,
            inputs.SeasonStandings,
            inputs.StageStandings,
            inputs.Memberships,
            inputs.Careers,
            inputs.Promotions);
        List<AlmanacCareerRecordEntry> entries = new(computed.Count);
        foreach (RecordCalculator.RecordHolders holders in computed)
        {
            entries.Add(MapRecord(holders, inputs.AthleteNames));
        }

        return entries;
    }

    internal static AlmanacCareerRecordEntry MapRecord(
        RecordCalculator.RecordHolders holders,
        Dictionary<int, string> names)
    {
        string label = StoryEventRenderer.FormatRecordKey(holders.RecordKey);
        string display = StoryEventRenderer.FormatRecordValue(holders.RecordKey, holders.Value) ?? holders.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<AlmanacRecordHolder> holderEntries = holders.HolderAthleteIds
            .Select(id =>
            {
                names.TryGetValue(id, out string? name);
                return new AlmanacRecordHolder(id, name ?? $"Athlete {id}");
            })
            .ToList();
        return new AlmanacCareerRecordEntry(holders.RecordKey, label, holders.Value, display, holderEntries);
    }

    internal static async Task<AlmanacCareerLeaders> LoadWeightedLeadersAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        RecordLoader.RecordInputs inputs = await RecordLoader.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> colors = await LoadColorsAsync(context, cancellationToken).ConfigureAwait(false);
        CareerPerformanceLoader.LoadResult performance = await CareerPerformanceLoader.LoadAsync(context, null, null, cancellationToken).ConfigureAwait(false);
        GetHallOfFameResponse mapped = GetHallOfFameHandler.Map(Guid.Empty, inputs, colors, performance, WeightedTake, null, null);
        return new AlmanacCareerLeaders(
            CareerPerformanceScorer.WeightsVersion,
            mapped.TotalAthletes,
            mapped.FromSeason,
            mapped.ToSeason,
            mapped.Leaders);
    }

    internal static async Task<Dictionary<int, int>> LoadColorsAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SportingColor, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<LeaguePointsLeaderEntry>> LoadSuperleaguePointsLeadersAsync(
        SaveDbContext context,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<LeagueEntity> superleagues = await context.Leagues
            .AsNoTracking()
            .Where(e => e.Kind == (int)LeagueKind.Superleague)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (superleagues.Count == 0)
        {
            return [];
        }

        HashSet<int> superleagueIds = superleagues.Select(l => l.Id).ToHashSet();
        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => superleagueIds.Contains(e.LeagueId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeaguePointsAccumulator> accumulators = new();
        foreach (SeasonStandingEntity row in rows)
        {
            if (!accumulators.TryGetValue(row.SaveAthleteId, out LeaguePointsAccumulator? acc))
            {
                acc = new LeaguePointsAccumulator();
                accumulators[row.SaveAthleteId] = acc;
            }

            acc.Total += row.TotalChampionshipPointsThousandths;
            acc.Seasons++;
            acc.BestRank = acc.Seasons == 1 ? row.SeasonRank : Math.Min(acc.BestRank, row.SeasonRank);
            if (row.IsChampion)
            {
                acc.Titles++;
            }
        }

        return accumulators
            .OrderByDescending(kv => kv.Value.Total)
            .ThenByDescending(kv => kv.Value.Titles)
            .ThenBy(kv => names.TryGetValue(kv.Key, out string? name) ? name : string.Empty, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key)
            .Take(PointsTake)
            .Select(kv =>
            {
                names.TryGetValue(kv.Key, out string? name);
                return new LeaguePointsLeaderEntry(kv.Key, name ?? $"Athlete {kv.Key}", kv.Value.Total, kv.Value.Seasons, kv.Value.Titles, kv.Value.BestRank);
            })
            .ToList();
    }

    internal static (IReadOnlyList<StreakEntry> TitleStreaks, IReadOnlyList<StreakEntry> StageStreaks) BuildStreaks(AlmanacCareerLeaders leaders)
    {
        List<StreakEntry> titles = leaders.Leaders
            .Where(e => e.LongestTitleStreak > 1)
            .OrderByDescending(e => e.LongestTitleStreak)
            .ThenByDescending(e => e.SuperleagueTitles)
            .ThenBy(e => e.AthleteName, StringComparer.Ordinal)
            .ThenBy(e => e.AthleteId)
            .Take(StreakTake)
            .Select(e => new StreakEntry(e.AthleteId, e.AthleteName, e.LongestTitleStreak))
            .ToList();
        List<StreakEntry> stages = leaders.Leaders
            .Where(e => e.LongestStageWinStreak > 1)
            .OrderByDescending(e => e.LongestStageWinStreak)
            .ThenByDescending(e => e.StageWins)
            .ThenBy(e => e.AthleteName, StringComparer.Ordinal)
            .ThenBy(e => e.AthleteId)
            .Take(StreakTake)
            .Select(e => new StreakEntry(e.AthleteId, e.AthleteName, e.LongestStageWinStreak))
            .ToList();
        return (titles, stages);
    }

    private sealed class LeaguePointsAccumulator
    {
        public long Total { get; set; }

        public int Seasons { get; set; }

        public int Titles { get; set; }

        public int BestRank { get; set; }
    }
}
