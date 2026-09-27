using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Records.GetHallOfFame;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Serves the Hall of Fame
/// career leaderboard from the same normalized record inputs as
/// <c>GetRecords</c> (no round payloads). Ordering is deterministic:
/// total titles, Superleague titles, stage wins, round wins, athlete name,
/// athlete id. Read-only: no lock, no RNG, no mutation.
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

    public async Task<GetHallOfFameResponse> HandleAsync(Guid saveId, int take = DefaultTake, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        int limited = Math.Clamp(take, 1, MaxTake);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        RecordLoader.RecordInputs inputs = await RecordLoader.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> colors = await LoadColorsAsync(context, cancellationToken).ConfigureAwait(false);
        return Map(saveId, inputs, colors, limited);
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
        int take)
    {
        RecordCalculator.AthleteScores scores = RecordCalculator.ComputeScores(
            inputs.SeasonStandings,
            inputs.StageStandings,
            inputs.Memberships,
            inputs.Careers,
            inputs.Promotions);
        List<HallOfFameEntry> ordered = BuildEntries(inputs, colors, scores);
        List<HallOfFameEntry> ranked = RankEntries(ordered, take);
        return new GetHallOfFameResponse(saveId, take, inputs.AthleteNames.Count, ranked);
    }

    internal static List<HallOfFameEntry> BuildEntries(
        RecordLoader.RecordInputs inputs,
        Dictionary<int, int> colors,
        RecordCalculator.AthleteScores scores)
    {
        List<HallOfFameEntry> ordered = new(inputs.AthleteNames.Count);
        foreach (int athleteId in inputs.AthleteNames.Keys)
        {
            ordered.Add(BuildSingle(inputs, colors, scores, athleteId));
        }

        return ordered;
    }

    internal static HallOfFameEntry BuildSingle(
        RecordLoader.RecordInputs inputs,
        Dictionary<int, int> colors,
        RecordCalculator.AthleteScores scores,
        int athleteId)
    {
        scores.FeederTitles.TryGetValue(athleteId, out int feeder);
        scores.SuperleagueTitles.TryGetValue(athleteId, out int super);
        scores.TotalTitles.TryGetValue(athleteId, out int total);
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
            feeder,
            super,
            total,
            stageWins,
            roundWins,
            superApps,
            totalApps,
            tenure,
            promos,
            releg,
            bonus,
            titleStreak,
            stageStreak);
    }

    internal static List<HallOfFameEntry> RankEntries(List<HallOfFameEntry> ordered, int take)
    {
        return ordered
            .OrderByDescending(e => e.TotalTitles)
            .ThenByDescending(e => e.SuperleagueTitles)
            .ThenByDescending(e => e.StageWins)
            .ThenByDescending(e => e.RoundWins)
            .ThenBy(e => e.AthleteName, StringComparer.Ordinal)
            .ThenBy(e => e.AthleteId)
            .Take(take)
            .Select((entry, index) => entry with { Rank = index + 1 })
            .ToList();
    }
}
