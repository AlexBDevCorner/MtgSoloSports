using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Single shared pure builder for Cup selection league-strength-aware inputs (MSS-064).
/// Both Color Cup and Type Cup selection must use this component; no duplicated
/// league-level lookup or strength scaling may live in the feature slices.
/// Resolves an explicit persisted <see cref="LeagueLevel"/> (never league-name parsing)
/// to the versioned permille factor, then scales completed-season performance and
/// recent form with checked deterministic integer arithmetic (multiply first, then
/// integer-divide by 1000, truncating toward zero).
/// Performance raw = source-season championship points × factor / 1000.
/// Recent form = exactly the completed source season's final
/// <see cref="RulesV1.RecentFormStageCount"/> league stages (generalized from
/// StagesPerSeason, e.g. 23-32 under 32-stage rules) weighted 1..10 oldest-to-newest,
/// then scaled by the same source-season factor. Pool athletes (no source-season
/// league) have zero performance and form; historical stages never leak.
/// Active athletes must present a complete final-window; missing/corrupt rows abort.
/// SimulationKernel pure: no HTTP, EF Core, filesystem, clock or RNG.
/// </summary>
public static class CupSelectionMetrics
{
    public const int StrengthDivisor = 1000;

    public sealed record CupMetrics(
        LeagueLevel? Level,
        int StrengthFactorPermille,
        int UnadjustedPerformanceThousandths,
        int AdjustedPerformanceThousandths,
        int UnadjustedFormAggregate,
        int AdjustedFormRaw);

    /// <summary>
    /// Returns the versioned competition-strength factor for an explicit persisted
    /// league level. Historical single-feeder v1 competition maps conceptually to
    /// Feeder 1 via <see cref="LeagueHierarchy.LevelForDivision"/> before calling;
    /// this method never parses league names.
    /// </summary>
    public static int GetStrengthFactor(RulesV1 rules, LeagueLevel level)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.GetCupStrengthFactor(level);
    }

    /// <summary>
    /// Scales one unadjusted input by a strength factor: (unadjusted × factor) / 1000,
    /// checked with explicit truncation. Used for both performance and form.
    /// </summary>
    public static int ScaleByStrength(int unadjustedThousandths, int strengthPermille)
    {
        if (unadjustedThousandths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unadjustedThousandths), "Cup selection inputs cannot be negative.");
        }

        if (strengthPermille < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(strengthPermille), "Competition-strength factor cannot be negative.");
        }

        checked
        {
            return (int)(((long)unadjustedThousandths * strengthPermille) / StrengthDivisor);
        }
    }

    /// <summary>
    /// Expected final-window stage numbers generalized from the snapshot:
    /// StagesPerSeason - RecentFormStageCount + 1 through StagesPerSeason.
    /// </summary>
    public static IReadOnlyList<int> FinalWindowStageNumbers(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        int start = checked(rules.StagesPerSeason - rules.RecentFormStageCount + 1);
        if (start < 1)
        {
            throw new InvalidOperationException($"Cup form window has corrupt start {start}.");
        }

        List<int> stages = new(rules.RecentFormStageCount);
        for (int stage = start; stage <= rules.StagesPerSeason; stage++)
        {
            stages.Add(stage);
        }

        return stages;
    }

    /// <summary>
    /// Computes the unadjusted final-window form aggregate from exactly the source
    /// season's final-window entries, ordered oldest-to-newest with weights 1..10.
    /// Requires exactly one entry per expected window stage; missing, duplicate or
    /// out-of-window entries abort. Entries must already be filtered to the source season.
    /// </summary>
    public static int ComputeUnadjustedFormAggregate(
        IReadOnlyList<ColorCupSelection.StageFormEntry> finalWindowEntries,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(finalWindowEntries);
        ArgumentNullException.ThrowIfNull(rules);
        IReadOnlyList<int> expected = FinalWindowStageNumbers(rules);
        if (finalWindowEntries.Count != rules.RecentFormStageCount)
        {
            throw new InvalidOperationException(
                $"Source-season form window must hold exactly {rules.RecentFormStageCount} stage rows, was {finalWindowEntries.Count}.");
        }

        Dictionary<int, int> pointsByStage = new(rules.RecentFormStageCount);
        foreach (ColorCupSelection.StageFormEntry entry in finalWindowEntries)
        {
            if (entry is null)
            {
                throw new InvalidOperationException("Source-season form has a null stage row.");
            }

            if (entry.StageNumber < expected[0] || entry.StageNumber > rules.StagesPerSeason)
            {
                throw new InvalidOperationException(
                    $"Source-season form stage {entry.StageNumber} is outside the final window {expected[0]}..{rules.StagesPerSeason}.");
            }

            if (entry.ChampionshipPointsThousandths < 0)
            {
                throw new InvalidOperationException("Source-season form championship points cannot be negative.");
            }

            if (pointsByStage.ContainsKey(entry.StageNumber))
            {
                throw new InvalidOperationException(
                    $"Source-season form has duplicate stage {entry.StageNumber}.");
            }

            pointsByStage.Add(entry.StageNumber, entry.ChampionshipPointsThousandths);
        }

        foreach (int stage in expected)
        {
            if (!pointsByStage.ContainsKey(stage))
            {
                throw new InvalidOperationException(
                    $"Source-season form is missing expected final-window stage {stage}.");
            }
        }

        checked
        {
            long total = 0;
            for (int i = 0; i < expected.Count; i++)
            {
                int points = pointsByStage[expected[i]];
                int weight = rules.RecentFormWeights[i];
                total += (long)points * weight;
            }

            return checked((int)total);
        }
    }

    /// <summary>
    /// Resolves one athlete's explicit persisted source-season league level.
    /// Returns null for Pool (no source-season league row). Never parses league names;
    /// levels come from <see cref="LeagueHierarchy.LevelForDivision"/> via the persisted
    /// (Kind, FeederDivision) pair. Missing level rows abort.
    /// </summary>
    public static LeagueLevel? ResolveLevel(
        IReadOnlyDictionary<int, int> levelsByLeagueId,
        IReadOnlyDictionary<int, int> leagueIdByAthlete,
        int athleteId,
        int sourceSeasonNumber)
    {
        ArgumentNullException.ThrowIfNull(levelsByLeagueId);
        ArgumentNullException.ThrowIfNull(leagueIdByAthlete);
        if (!leagueIdByAthlete.TryGetValue(athleteId, out int leagueId))
        {
            return null;
        }

        if (!levelsByLeagueId.TryGetValue(leagueId, out int levelValue))
        {
            throw new InvalidOperationException(
                $"Source season {sourceSeasonNumber} league {leagueId} has no resolved level.");
        }

        return (LeagueLevel)levelValue;
    }

    /// <summary>
    /// Builds strength-aware performance/form inputs for one athlete from its
    /// completed source-season data. <paramref name="athleteStages"/> are the athlete's
    /// stage rows across seasons (the helper filters to <paramref name="sourceSeasonNumber"/>);
    /// historical stages never leak. <paramref name="level"/> is null for Pool.
    /// Pool always yields zero performance and form. Active requires a source-season
    /// standing and a complete final window; violations abort rather than falling
    /// back to older seasons.
    /// </summary>
    public static CupMetrics Build(
        int? unadjustedPerformanceThousandths,
        IReadOnlyList<ColorCupSelection.StageFormEntry> athleteStages,
        int sourceSeasonNumber,
        LeagueLevel? level,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(athleteStages);
        ArgumentNullException.ThrowIfNull(rules);
        if (sourceSeasonNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSeasonNumber), "Source season must be positive.");
        }

        if (level is null)
        {
            return new CupMetrics(null, 0, 0, 0, 0, 0);
        }

        int factor = GetStrengthFactor(rules, level.Value);
        int unadjustedPerformance = unadjustedPerformanceThousandths ?? throw new InvalidOperationException(
            $"Active source-season athlete has no season standing for performance.");
        if (unadjustedPerformance < 0)
        {
            throw new InvalidOperationException("Source-season championship points cannot be negative.");
        }

        List<ColorCupSelection.StageFormEntry> window = athleteStages
            .Where(e => e.SeasonNumber == sourceSeasonNumber)
            .ToList();
        // Keep only the final-window stages; ComputeUnadjustedFormAggregate validates
        // completeness (missing/corrupt rows abort, never fallback to older seasons).
        IReadOnlyList<int> expected = FinalWindowStageNumbers(rules);
        HashSet<int> expectedSet = expected.ToHashSet();
        List<ColorCupSelection.StageFormEntry> finalWindow = window
            .Where(e => expectedSet.Contains(e.StageNumber))
            .ToList();
        int unadjustedForm = ComputeUnadjustedFormAggregate(finalWindow, rules);
        int adjustedPerformance = ScaleByStrength(unadjustedPerformance, factor);
        int adjustedForm = ScaleByStrength(unadjustedForm, factor);
        return new CupMetrics(level, factor, unadjustedPerformance, adjustedPerformance, unadjustedForm, adjustedForm);
    }
}
