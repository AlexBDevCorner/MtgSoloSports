using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;

namespace MtgSoloSports.Features.Athletes.Projections;

/// <summary>
/// Pure fixed-point aggregation of athlete career and season projections from
/// authoritative history. Reads only normalized <c>StageStanding</c> /
/// <c>SeasonStanding</c> / <c>SeasonMembership</c> rows; never touches round
/// payloads, RNG, clock or database ordering for sporting values.
/// All arithmetic is integer-only.
/// </summary>
public static class AthleteProjectionCalculator
{
    public sealed record SeasonAggregate(
        int RoundWins,
        int StageWins,
        int StageSeconds,
        int StageThirds,
        int EarnedBonusThousandths,
        int TotalChampionshipPointsThousandths,
        int TotalStageScoreThousandths,
        int TotalBaseScoreThousandths);

    public sealed record CareerAggregate(
        int RoundWins,
        int StageWins,
        int StageSeconds,
        int StageThirds,
        int LifetimeEarnedBonusThousandths);

    /// <summary>
    /// Sums one season's stage standings for one athlete.
    /// Stage rank 1/2/3 counts wins/seconds/thirds; round wins sum stage
    /// <c>RoundWins</c> (each stage's count of round 1st places).
    /// </summary>
    public static SeasonAggregate AggregateSeasonStages(IEnumerable<StageStandingEntity> stageRows)
    {
        ArgumentNullException.ThrowIfNull(stageRows);
        int roundWins = 0;
        int stageWins = 0;
        int seconds = 0;
        int thirds = 0;
        int earned = 0;
        int championship = 0;
        int stageScore = 0;
        int baseScore = 0;
        checked
        {
            foreach (StageStandingEntity row in stageRows)
            {
                if (row.RoundWins < 0 || row.EarnedBonusThousandths < 0)
                {
                    throw new InvalidOperationException($"Stage standing {row.Id} has corrupt non-negative sporting values.");
                }

                roundWins += row.RoundWins;
                earned += row.EarnedBonusThousandths;
                championship += row.ChampionshipPointsThousandths;
                stageScore += row.StageScoreThousandths;
                baseScore += row.BaseScoreThousandths;
                switch (row.StageRank)
                {
                    case 1: stageWins++; break;
                    case 2: seconds++; break;
                    case 3: thirds++; break;
                    default: break;
                }

                if (row.StageRank < 1 || row.StageRank > 32)
                {
                    throw new InvalidOperationException($"Stage standing {row.Id} has corrupt rank {row.StageRank}.");
                }
            }
        }

        return new SeasonAggregate(roundWins, stageWins, seconds, thirds, earned, championship, stageScore, baseScore);
    }

    /// <summary>
    /// Sums career totals across all of one athlete's stage standings.
    /// </summary>
    public static CareerAggregate AggregateCareer(IEnumerable<StageStandingEntity> allStageRows)
    {
        ArgumentNullException.ThrowIfNull(allStageRows);
        SeasonAggregate summed = AggregateSeasonStages(allStageRows);
        return new CareerAggregate(
            summed.RoundWins,
            summed.StageWins,
            summed.StageSeconds,
            summed.StageThirds,
            summed.EarnedBonusThousandths);
    }

    /// <summary>
    /// Builds bonus contributions for <see cref="BonusCalculator.EffectiveBonus"/>
    /// from one athlete's stage standings. The caller supplies the
    /// season-id to season-number map; unknown seasons abort (never silently skipped).
    /// </summary>
    public static IReadOnlyList<BonusContribution> BuildContributions(
        IEnumerable<StageStandingEntity> allStageRows,
        IReadOnlyDictionary<int, int> seasonNumbers)
    {
        ArgumentNullException.ThrowIfNull(allStageRows);
        ArgumentNullException.ThrowIfNull(seasonNumbers);
        List<BonusContribution> contributions = [];
        foreach (StageStandingEntity row in allStageRows)
        {
            if (!seasonNumbers.TryGetValue(row.SeasonId, out int earnedSeason))
            {
                throw new InvalidOperationException($"Stage standing {row.Id} references unknown season {row.SeasonId}.");
            }

            contributions.Add(new BonusContribution(
                earnedSeason,
                row.StageNumber,
                SimulationKernel.FixedPoint.Bonus.FromThousandths(row.EarnedBonusThousandths)));
        }

        return contributions;
    }

    /// <summary>
    /// Computes the current effective bonus for the next unplayed boundary.
    /// When <paramref name="latestSeasonComplete"/> is true the next boundary is
    /// the next season's stage 1 (Stage 32 bonus enters at 80% decay); otherwise
    /// it is the athlete's next stage within <paramref name="currentSeasonNumber"/>.
    /// <paramref name="nextStageNumber"/> must be 1..StagesPerSeason.
    /// </summary>
    public static int ComputeCurrentEffectiveThousandths(
        IEnumerable<BonusContribution> contributions,
        int currentSeasonNumber,
        int nextStageNumber,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        ArgumentNullException.ThrowIfNull(rules);
        SimulationKernel.FixedPoint.Bonus effective = BonusCalculator.EffectiveBonus(
            contributions, currentSeasonNumber, nextStageNumber, rules);
        return effective.Thousandths;
    }

    /// <summary>
    /// Resolves the next unplayed (season, stage) boundary for one athlete.
    /// Active athletes in an incomplete season resume at their league's next
    /// stage; completed leagues and completed seasons resume at the next
    /// season's stage 1. Pool athletes reuse the current season because their
    /// effective bonus is stage-invariant within a season (no current-season
    /// contributions), so any legal stage gives the identical result.
    /// </summary>
    public static (int EffectiveSeason, int EffectiveStage) ResolveNextBoundary(
        int latestSeasonNumber,
        bool latestSeasonComplete,
        bool wasActiveInLatest,
        int completedStagesInLatestLeague,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (latestSeasonNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(latestSeasonNumber));
        }

        if (latestSeasonComplete)
        {
            return (latestSeasonNumber + 1, 1);
        }

        if (!wasActiveInLatest)
        {
            return (latestSeasonNumber, 1);
        }

        if (completedStagesInLatestLeague < 0 || completedStagesInLatestLeague > rules.StagesPerSeason)
        {
            throw new InvalidOperationException($"Corrupt completed-stage count {completedStagesInLatestLeague}.");
        }

        if (completedStagesInLatestLeague >= rules.StagesPerSeason)
        {
            return (latestSeasonNumber + 1, 1);
        }

        return (latestSeasonNumber, completedStagesInLatestLeague + 1);
    }
}
