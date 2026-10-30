using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared.Careers;
using MtgSoloSports.SimulationKernel.Careers;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Records.GetHallOfFame;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Serves the Hall of Fame
/// career leaderboard from the weighted MSS-083 performance score computed
/// over officially finalized league and major-cup placements in the selected
/// inclusive season range (all time by default). Ordering is deterministic:
/// career points, major-event wins, Superleague titles, Superleague points,
/// athlete name, athlete id. Read-only: no lock, no RNG, no mutation.
/// Titles are period-scoped supporting statistics; legacy stage/round/tenure
/// fields remain all-time career totals for compatibility.
/// </summary>
public sealed class GetHallOfFameHandler
{
    public const int DefaultTake = 20;

    public const int MaxTake = 100;

    private readonly SaveStore _store;

    public GetHallOfFameHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetHallOfFameResponse> HandleAsync(
        Guid saveId,
        int take = DefaultTake,
        int? fromSeason = null,
        int? toSeason = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (fromSeason is <= 0)
        {
            throw new ArgumentException("From season must be positive.", nameof(fromSeason));
        }

        if (toSeason is <= 0)
        {
            throw new ArgumentException("To season must be positive.", nameof(toSeason));
        }

        int? effectiveFrom = fromSeason ?? toSeason;
        int? effectiveTo = toSeason ?? fromSeason;
        if (effectiveFrom is not null && effectiveTo is not null && effectiveFrom.Value > effectiveTo.Value)
        {
            throw new ArgumentException("From season must not exceed to season.", nameof(fromSeason));
        }

        int limited = Math.Clamp(take, 1, MaxTake);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        RecordLoader.RecordInputs inputs = await RecordLoader.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> colors = await LoadColorsAsync(context, cancellationToken).ConfigureAwait(false);
        CareerPerformanceLoader.LoadResult performance = await CareerPerformanceLoader.LoadAsync(
            context, effectiveFrom, effectiveTo, cancellationToken).ConfigureAwait(false);
        return Map(saveId, inputs, colors, performance, limited, effectiveFrom, effectiveTo);
    }

    internal static async Task<Dictionary<int, int>> LoadColorsAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SportingColor, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static GetHallOfFameResponse Map(
        Guid saveId,
        RecordLoader.RecordInputs inputs,
        Dictionary<int, int> colors,
        CareerPerformanceLoader.LoadResult performance,
        int take,
        int? fromSeason,
        int? toSeason)
    {
        RecordCalculator.AthleteScores scores = RecordCalculator.ComputeScores(
            inputs.SeasonStandings,
            inputs.StageStandings,
            inputs.Memberships,
            inputs.Careers,
            inputs.Promotions);
        Dictionary<int, CareerPerformanceScorer.AthleteCareerScore> career =
            new(CareerPerformanceScorer.ComputeScores(performance.Events));
        List<HallOfFameEntry> ordered = BuildEntries(inputs, colors, scores, career);
        List<HallOfFameEntry> ranked = RankEntries(ordered, take);
        return new GetHallOfFameResponse(
            saveId, take, inputs.AthleteNames.Count, ranked,
            fromSeason, toSeason, performance.MinSeasonNumber, performance.MaxSeasonNumber,
            CareerPerformanceScorer.WeightsVersion);
    }

    internal static List<HallOfFameEntry> BuildEntries(
        RecordLoader.RecordInputs inputs,
        Dictionary<int, int> colors,
        RecordCalculator.AthleteScores scores,
        Dictionary<int, CareerPerformanceScorer.AthleteCareerScore> career)
    {
        List<HallOfFameEntry> ordered = new(inputs.AthleteNames.Count);
        foreach (int athleteId in inputs.AthleteNames.Keys)
        {
            if (!career.ContainsKey(athleteId))
            {
                continue;
            }

            ordered.Add(BuildSingle(inputs, colors, scores, career, athleteId));
        }

        return ordered;
    }

    internal static HallOfFameEntry BuildSingle(
        RecordLoader.RecordInputs inputs,
        Dictionary<int, int> colors,
        RecordCalculator.AthleteScores scores,
        Dictionary<int, CareerPerformanceScorer.AthleteCareerScore> career,
        int athleteId)
    {
        career.TryGetValue(athleteId, out CareerPerformanceScorer.AthleteCareerScore? performance);
        int feederTitles = performance is null ? 0 : performance.LeagueTitles - performance.SuperleagueTitles;
        int superTitles = performance?.SuperleagueTitles ?? 0;
        int totalTitles = performance?.LeagueTitles ?? 0;
        scores.StageWins.TryGetValue(athleteId, out int stageWins);
        scores.RoundWins.TryGetValue(athleteId, out int roundWins);
        scores.SuperleagueAppearances.TryGetValue(athleteId, out int superApps);
        scores.TotalAppearances.TryGetValue(athleteId, out int totalApps);
        scores.LongestTenure.TryGetValue(athleteId, out int tenure);
        scores.Promotions.TryGetValue(athleteId, out int promos);
        scores.Relegations.TryGetValue(athleteId, out int releg);
        scores.EffectiveBonus.TryGetValue(athleteId, out int bonus);
        scores.TitleStreak.TryGetValue(athleteId, out int titleStreak);
        scores.StageWinStreak.TryGetValue(athleteId, out int stageStreak);
        inputs.AthleteNames.TryGetValue(athleteId, out string? name);
        colors.TryGetValue(athleteId, out int color);
        string colorName = Enum.IsDefined(typeof(SportingColor), color)
            ? ((SportingColor)color).ToString()
            : "Unknown";
        return new HallOfFameEntry(
            0,
            athleteId,
            name ?? $"Athlete {athleteId}",
            color,
            colorName,
            feederTitles,
            superTitles,
            totalTitles,
            stageWins,
            roundWins,
            superApps,
            totalApps,
            tenure,
            promos,
            releg,
            bonus,
            titleStreak,
            stageStreak,
            performance?.TotalPoints ?? 0,
            performance?.SuperleaguePoints ?? 0,
            performance?.Feeder1Points ?? 0,
            performance?.Feeder2Points ?? 0,
            performance?.Feeder3Points ?? 0,
            performance?.ColorCupIndividualPoints ?? 0,
            performance?.ColorCupTeamPoints ?? 0,
            performance?.TypeCupTeamPoints ?? 0,
            performance?.MajorWins ?? 0,
            performance?.ScoredEvents ?? 0);
    }

    internal static List<HallOfFameEntry> RankEntries(List<HallOfFameEntry> ordered, int take)
    {
        return ordered
            .OrderByDescending(e => e.CareerPoints)
            .ThenByDescending(e => e.MajorWins)
            .ThenByDescending(e => e.SuperleagueTitles)
            .ThenByDescending(e => e.SuperleaguePoints)
            .ThenBy(e => e.AthleteName, StringComparer.Ordinal)
            .ThenBy(e => e.AthleteId)
            .Take(take)
            .Select((entry, index) => entry with { Rank = index + 1 })
            .ToList();
    }
}
