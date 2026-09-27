using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records;

/// <summary>
/// Pure fixed-point record computation from normalized history only.
/// Reads never touch round payloads (<c>Rounds.PayloadJson</c>,
/// <c>QualifierRounds.PayloadJson</c>), RNG, clock or database ordering for
/// sporting values. All arithmetic is integer-only; holder ordering is
/// deterministic (athlete name ascending, then athlete id ascending).
/// Rebuildable: every input projects normalized columns from
/// <c>SeasonStandings</c> / <c>StageStandings</c> / <c>SeasonMemberships</c> /
/// <c>Leagues</c> / <c>Movements</c> / <c>QualifierStandings</c> /
/// <c>AthleteCareers</c>.
/// Tie behavior: all athletes sharing the maximum value are joint holders
/// ordered deterministically; a tie never replaces the holder list, only an
/// outright higher value does. Vacant records (maximum zero or below) report
/// no holders.
/// </summary>
public static class RecordCalculator
{
    public sealed record SeasonStandingInput(
        int SeasonId,
        int SeasonNumber,
        int LeagueKind,
        int SaveAthleteId,
        bool IsChampion);

    public sealed record StageStandingInput(
        int SeasonId,
        int SeasonNumber,
        int StageNumber,
        int SaveAthleteId,
        int StageRank);

    public sealed record MembershipInput(
        int SeasonId,
        int SeasonNumber,
        int SaveAthleteId,
        int? LeagueKind,
        bool IsActive);

    public sealed record CareerInput(
        int SaveAthleteId,
        int RoundWins,
        int StageWins,
        int CurrentEffectiveBonusThousandths);

    public sealed record PromotionInput(
        int SaveAthleteId,
        int PromotionCount,
        int RelegationCount);

    public sealed record RecordHolders(
        string RecordKey,
        int Value,
        IReadOnlyList<int> HolderAthleteIds);

    internal sealed record AthleteScores(
        Dictionary<int, int> FeederTitles,
        Dictionary<int, int> SuperleagueTitles,
        Dictionary<int, int> TotalTitles,
        Dictionary<int, int> StageWins,
        Dictionary<int, int> RoundWins,
        Dictionary<int, int> SuperleagueAppearances,
        Dictionary<int, int> TotalAppearances,
        Dictionary<int, int> LongestTenure,
        Dictionary<int, int> Promotions,
        Dictionary<int, int> Relegations,
        Dictionary<int, int> EffectiveBonus,
        Dictionary<int, int> TitleStreak,
        Dictionary<int, int> StageWinStreak);

    /// <summary>
    /// Computes every record from normalized inputs. Inputs with corrupt
    /// negative sporting values or unknown identities abort.
    /// </summary>
    public static IReadOnlyList<RecordHolders> ComputeAll(
        IReadOnlyDictionary<int, string> athleteNames,
        IReadOnlyList<SeasonStandingInput> seasonStandings,
        IReadOnlyList<StageStandingInput> stageStandings,
        IReadOnlyList<MembershipInput> memberships,
        IReadOnlyList<CareerInput> careers,
        IReadOnlyList<PromotionInput> promotions)
    {
        ArgumentNullException.ThrowIfNull(athleteNames);
        ArgumentNullException.ThrowIfNull(seasonStandings);
        ArgumentNullException.ThrowIfNull(stageStandings);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(careers);
        ArgumentNullException.ThrowIfNull(promotions);
        AthleteScores scores = ComputeScores(seasonStandings, stageStandings, memberships, careers, promotions);
        return
        [
            BuildRecord(RecordKey.FeederTitles, scores.FeederTitles, athleteNames),
            BuildRecord(RecordKey.SuperleagueTitles, scores.SuperleagueTitles, athleteNames),
            BuildRecord(RecordKey.TotalTitles, scores.TotalTitles, athleteNames),
            BuildRecord(RecordKey.StageWins, scores.StageWins, athleteNames),
            BuildRecord(RecordKey.RoundWins, scores.RoundWins, athleteNames),
            BuildRecord(RecordKey.SuperleagueAppearances, scores.SuperleagueAppearances, athleteNames),
            BuildRecord(RecordKey.TotalAppearances, scores.TotalAppearances, athleteNames),
            BuildRecord(RecordKey.LongestSuperleagueTenure, scores.LongestTenure, athleteNames),
            BuildRecord(RecordKey.Promotions, scores.Promotions, athleteNames),
            BuildRecord(RecordKey.Relegations, scores.Relegations, athleteNames),
            BuildRecord(RecordKey.HighestEffectiveBonus, scores.EffectiveBonus, athleteNames),
            BuildRecord(RecordKey.LongestTitleStreak, scores.TitleStreak, athleteNames),
            BuildRecord(RecordKey.LongestStageWinStreak, scores.StageWinStreak, athleteNames),
        ];
    }

    internal static AthleteScores ComputeScores(
        IReadOnlyList<SeasonStandingInput> seasonStandings,
        IReadOnlyList<StageStandingInput> stageStandings,
        IReadOnlyList<MembershipInput> memberships,
        IReadOnlyList<CareerInput> careers,
        IReadOnlyList<PromotionInput> promotions)
    {
        Dictionary<int, int> feeder = [];
        Dictionary<int, int> super = [];
        Dictionary<int, int> total = [];
        CountTitles(seasonStandings, feeder, super, total);
        Dictionary<int, int> stageWins = careers.ToDictionary(c => c.SaveAthleteId, c => ValidateNonNegative(c.StageWins, c.SaveAthleteId));
        Dictionary<int, int> roundWins = careers.ToDictionary(c => c.SaveAthleteId, c => ValidateNonNegative(c.RoundWins, c.SaveAthleteId));
        Dictionary<int, int> bonus = careers.ToDictionary(c => c.SaveAthleteId, c => c.CurrentEffectiveBonusThousandths);
        Dictionary<int, int> superApps = CountSuperAppearances(memberships);
        Dictionary<int, int> totalApps = CountTotalAppearances(memberships);
        Dictionary<int, int> tenure = ComputeLongestTenure(memberships);
        Dictionary<int, int> promoCounts = promotions.ToDictionary(p => p.SaveAthleteId, p => ValidateNonNegative(p.PromotionCount, p.SaveAthleteId));
        Dictionary<int, int> relegCounts = promotions.ToDictionary(p => p.SaveAthleteId, p => ValidateNonNegative(p.RelegationCount, p.SaveAthleteId));
        EnsureAthleteCoverage(feeder, super, total, stageWins, roundWins, bonus, superApps, totalApps, tenure, promoCounts, relegCounts);
        Dictionary<int, int> titleStreak = ComputeTitleStreak(seasonStandings);
        Dictionary<int, int> stageStreak = ComputeStageWinStreak(stageStandings, memberships);
        foreach (int athleteId in titleStreak.Keys)
        {
            feeder.TryAdd(athleteId, 0);
            super.TryAdd(athleteId, 0);
            total.TryAdd(athleteId, 0);
            stageWins.TryAdd(athleteId, 0);
            roundWins.TryAdd(athleteId, 0);
            bonus.TryAdd(athleteId, 0);
            superApps.TryAdd(athleteId, 0);
            totalApps.TryAdd(athleteId, 0);
            tenure.TryAdd(athleteId, 0);
            promoCounts.TryAdd(athleteId, 0);
            relegCounts.TryAdd(athleteId, 0);
        }

        foreach (int athleteId in stageStreak.Keys)
        {
            feeder.TryAdd(athleteId, 0);
            super.TryAdd(athleteId, 0);
            total.TryAdd(athleteId, 0);
            stageWins.TryAdd(athleteId, 0);
            roundWins.TryAdd(athleteId, 0);
            bonus.TryAdd(athleteId, 0);
            superApps.TryAdd(athleteId, 0);
            totalApps.TryAdd(athleteId, 0);
            tenure.TryAdd(athleteId, 0);
            promoCounts.TryAdd(athleteId, 0);
            relegCounts.TryAdd(athleteId, 0);
        }

        return new AthleteScores(feeder, super, total, stageWins, roundWins, superApps, totalApps, tenure, promoCounts, relegCounts, bonus, titleStreak, stageStreak);
    }

    internal static void CountTitles(
        IReadOnlyList<SeasonStandingInput> seasonStandings,
        Dictionary<int, int> feeder,
        Dictionary<int, int> super,
        Dictionary<int, int> total)
    {
        foreach (SeasonStandingInput row in seasonStandings)
        {
            if (!row.IsChampion)
            {
                continue;
            }

            if (row.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Season standing references corrupt athlete {row.SaveAthleteId}.");
            }

            feeder.TryAdd(row.SaveAthleteId, 0);
            super.TryAdd(row.SaveAthleteId, 0);
            total.TryAdd(row.SaveAthleteId, 0);
            if (row.LeagueKind == (int)LeagueKind.Superleague)
            {
                super[row.SaveAthleteId] = checked(super[row.SaveAthleteId] + 1);
            }
            else
            {
                feeder[row.SaveAthleteId] = checked(feeder[row.SaveAthleteId] + 1);
            }

            total[row.SaveAthleteId] = checked(total[row.SaveAthleteId] + 1);
        }
    }

    internal static Dictionary<int, int> CountSuperAppearances(IReadOnlyList<MembershipInput> memberships)
    {
        Dictionary<int, int> counts = [];
        foreach (MembershipInput row in memberships)
        {
            if (!row.IsActive || row.LeagueKind != (int)LeagueKind.Superleague)
            {
                continue;
            }

            counts.TryAdd(row.SaveAthleteId, 0);
            counts[row.SaveAthleteId] = checked(counts[row.SaveAthleteId] + 1);
        }

        return counts;
    }

    internal static Dictionary<int, int> CountTotalAppearances(IReadOnlyList<MembershipInput> memberships)
    {
        Dictionary<int, int> counts = [];
        foreach (MembershipInput row in memberships)
        {
            if (!row.IsActive)
            {
                continue;
            }

            counts.TryAdd(row.SaveAthleteId, 0);
            counts[row.SaveAthleteId] = checked(counts[row.SaveAthleteId] + 1);
        }

        return counts;
    }

    /// <summary>
    /// Longest run of consecutive seasons active in the Superleague.
    /// Seasons are consecutive by season number; a pool season or feeder
    /// season breaks the run.
    /// </summary>
    internal static Dictionary<int, int> ComputeLongestTenure(IReadOnlyList<MembershipInput> memberships)
    {
        Dictionary<int, List<int>> superSeasons = [];
        foreach (MembershipInput row in memberships)
        {
            if (!row.IsActive || row.LeagueKind != (int)LeagueKind.Superleague)
            {
                continue;
            }

            if (!superSeasons.TryGetValue(row.SaveAthleteId, out List<int>? seasons))
            {
                seasons = [];
                superSeasons[row.SaveAthleteId] = seasons;
            }

            seasons.Add(row.SeasonNumber);
        }

        Dictionary<int, int> longest = [];
        foreach ((int athleteId, List<int> seasons) in superSeasons)
        {
            seasons.Sort();
            int best = 0;
            int run = 0;
            int previous = int.MinValue;
            foreach (int season in seasons)
            {
                if (season == previous + 1)
                {
                    run++;
                }
                else
                {
                    run = 1;
                }

                best = Math.Max(best, run);
                previous = season;
            }

            longest[athleteId] = best;
        }

        return longest;
    }

    /// <summary>
    /// Longest run of consecutive champion seasons (any league).
    /// </summary>
    internal static Dictionary<int, int> ComputeTitleStreak(IReadOnlyList<SeasonStandingInput> seasonStandings)
    {
        Dictionary<int, List<int>> titleSeasons = [];
        foreach (SeasonStandingInput row in seasonStandings)
        {
            if (!row.IsChampion)
            {
                continue;
            }

            if (!titleSeasons.TryGetValue(row.SaveAthleteId, out List<int>? seasons))
            {
                seasons = [];
                titleSeasons[row.SaveAthleteId] = seasons;
            }

            seasons.Add(row.SeasonNumber);
        }

        Dictionary<int, int> longest = [];
        foreach ((int athleteId, List<int> seasons) in titleSeasons)
        {
            seasons.Sort();
            int best = 0;
            int run = 0;
            int previous = int.MinValue;
            foreach (int season in seasons)
            {
                if (season == previous + 1)
                {
                    run++;
                }
                else
                {
                    run = 1;
                }

                best = Math.Max(best, run);
                previous = season;
            }

            longest[athleteId] = best;
        }

        return longest;
    }

    /// <summary>
    /// Longest run of consecutive stage wins ordered by (season, stage).
    /// Consecutive means the next global stage: same season stage+1, or the
    /// next season's stage 1 after stage 32 when both seasons are consecutive
    /// numbers. Any non-win, missing stage (pool season) or season gap breaks
    /// the run. Inputs must already be stage-1 wins only for streak purposes;
    /// non-wins are ignored here because only wins extend a run, but gaps are
    /// detected from the athlete's full active stage sequence supplied via
    /// <paramref name="memberships"/> plus win positions.
    /// </summary>
    internal static Dictionary<int, int> ComputeStageWinStreak(
        IReadOnlyList<StageStandingInput> stageStandings,
        IReadOnlyList<MembershipInput> memberships)
    {
        _ = memberships;
        Dictionary<int, HashSet<(int Season, int Stage)>> winsByAthlete = CollectStageWins(stageStandings);
        Dictionary<int, List<(int Season, int Stage)>> activeStages = OrderActiveStages(stageStandings);
        return MeasureStageStreaks(activeStages, winsByAthlete);
    }

    internal static Dictionary<int, HashSet<(int Season, int Stage)>> CollectStageWins(
        IReadOnlyList<StageStandingInput> stageStandings)
    {
        Dictionary<int, HashSet<(int Season, int Stage)>> winsByAthlete = [];
        foreach (StageStandingInput row in stageStandings)
        {
            if (row.StageRank != 1)
            {
                continue;
            }

            if (row.StageNumber < 1 || row.StageNumber > 32)
            {
                throw new InvalidOperationException($"Stage standing for athlete {row.SaveAthleteId} has corrupt stage {row.StageNumber}.");
            }

            if (!winsByAthlete.TryGetValue(row.SaveAthleteId, out HashSet<(int Season, int Stage)>? wins))
            {
                wins = [];
                winsByAthlete[row.SaveAthleteId] = wins;
            }

            wins.Add((row.SeasonNumber, row.StageNumber));
        }

        return winsByAthlete;
    }

    internal static Dictionary<int, List<(int Season, int Stage)>> OrderActiveStages(
        IReadOnlyList<StageStandingInput> stageStandings)
    {
        Dictionary<int, List<(int Season, int Stage)>> activeStages = [];
        foreach (IGrouping<int, StageStandingInput> group in stageStandings.GroupBy(r => r.SaveAthleteId))
        {
            List<(int Season, int Stage)> ordered = group
                .Select(r => (r.SeasonNumber, r.StageNumber))
                .OrderBy(p => p.SeasonNumber)
                .ThenBy(p => p.StageNumber)
                .ToList();
            activeStages[group.Key] = ordered;
        }

        return activeStages;
    }

    internal static Dictionary<int, int> MeasureStageStreaks(
        Dictionary<int, List<(int Season, int Stage)>> activeStages,
        Dictionary<int, HashSet<(int Season, int Stage)>> winsByAthlete)
    {
        Dictionary<int, int> longest = [];
        foreach ((int athleteId, List<(int Season, int Stage)> ordered) in activeStages)
        {
            longest[athleteId] = MeasureSingleStreak(ordered, winsByAthlete, athleteId);
        }

        return longest;
    }

    internal static int MeasureSingleStreak(
        List<(int Season, int Stage)> ordered,
        Dictionary<int, HashSet<(int Season, int Stage)>> winsByAthlete,
        int athleteId)
    {
        if (!winsByAthlete.TryGetValue(athleteId, out HashSet<(int Season, int Stage)>? wins))
        {
            return 0;
        }

        int best = 0;
        int run = 0;
        (int Season, int Stage)? previous = null;
        foreach ((int Season, int Stage) stage in ordered)
        {
            if (!wins.Contains(stage))
            {
                run = 0;
                previous = null;
                continue;
            }

            run = previous is not null && IsConsecutiveStage(previous.Value, stage) ? run + 1 : 1;
            best = Math.Max(best, run);
            previous = stage;
        }

        return best;
    }

    internal static bool IsConsecutiveStage((int Season, int Stage) previous, (int Season, int Stage) current)
    {
        if (current.Season == previous.Season)
        {
            return current.Stage == previous.Stage + 1;
        }

        return current.Season == previous.Season + 1 && previous.Stage == 32 && current.Stage == 1;
    }

    internal static void EnsureAthleteCoverage(
        Dictionary<int, int> feeder,
        Dictionary<int, int> super,
        Dictionary<int, int> total,
        Dictionary<int, int> stageWins,
        Dictionary<int, int> roundWins,
        Dictionary<int, int> bonus,
        Dictionary<int, int> superApps,
        Dictionary<int, int> totalApps,
        Dictionary<int, int> tenure,
        Dictionary<int, int> promoCounts,
        Dictionary<int, int> relegCounts)
    {
        HashSet<int> all = new(feeder.Keys);
        all.UnionWith(super.Keys);
        all.UnionWith(total.Keys);
        all.UnionWith(stageWins.Keys);
        all.UnionWith(roundWins.Keys);
        all.UnionWith(bonus.Keys);
        all.UnionWith(superApps.Keys);
        all.UnionWith(totalApps.Keys);
        all.UnionWith(tenure.Keys);
        all.UnionWith(promoCounts.Keys);
        all.UnionWith(relegCounts.Keys);
        foreach (int athleteId in all)
        {
            feeder.TryAdd(athleteId, 0);
            super.TryAdd(athleteId, 0);
            total.TryAdd(athleteId, 0);
            stageWins.TryAdd(athleteId, 0);
            roundWins.TryAdd(athleteId, 0);
            bonus.TryAdd(athleteId, 0);
            superApps.TryAdd(athleteId, 0);
            totalApps.TryAdd(athleteId, 0);
            tenure.TryAdd(athleteId, 0);
            promoCounts.TryAdd(athleteId, 0);
            relegCounts.TryAdd(athleteId, 0);
        }
    }

    internal static int ValidateNonNegative(int value, int athleteId)
    {
        if (value < 0)
        {
            throw new InvalidOperationException($"Athlete {athleteId} has corrupt negative sporting value {value}.");
        }

        return value;
    }

    /// <summary>
    /// Builds one record: maximum value wins; all athletes at the maximum are
    /// joint holders ordered by name then id. Zero or negative maxima are
    /// vacant (no holders). Deterministic; never uses database ordering.
    /// </summary>
    internal static RecordHolders BuildRecord(
        string recordKey,
        Dictionary<int, int> values,
        IReadOnlyDictionary<int, string> athleteNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordKey);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(athleteNames);
        if (values.Count == 0)
        {
            return new RecordHolders(recordKey, 0, []);
        }

        int maximum = values.Values.Max();
        if (maximum <= 0)
        {
            return new RecordHolders(recordKey, 0, []);
        }

        List<int> holders = values
            .Where(kv => kv.Value == maximum)
            .Select(kv => kv.Key)
            .OrderBy(id => athleteNames.TryGetValue(id, out string? name) ? name : string.Empty, StringComparer.Ordinal)
            .ThenBy(id => id)
            .ToList();
        return new RecordHolders(recordKey, maximum, holders);
    }
}
