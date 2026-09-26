using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Scoring;

/// <summary>
/// Pure fixed-point aggregation of career bonus into the active bonus for a stage.
/// All arithmetic is integer-only; season-age decay comes from the rules snapshot.
/// </summary>
public static class BonusCalculator
{
    /// <summary>
    /// Computes the active bonus for a stage from individual stage contributions.
    /// Contributions from the current season at or after <paramref name="currentStage"/>
    /// are pending and excluded. All other contributions decay by season age
    /// (<c>currentSeason - earnedSeason</c>) using the snapshot weights
    /// 100/80/60/40/20/0. Stage 32 of a prior season therefore enters the next
    /// season at 80% because its age is already 1. Aging continues while an
    /// athlete is in the common pool because age is purely season-based.
    /// </summary>
    public static Bonus EffectiveBonus(
        IEnumerable<BonusContribution> contributions,
        int currentSeason,
        int currentStage,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        ArgumentNullException.ThrowIfNull(rules);

        if (currentSeason < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(currentSeason), "Current season must be >= 1.");
        }

        if (currentStage < 1 || currentStage > rules.StagesPerSeason)
        {
            throw new ArgumentOutOfRangeException(nameof(currentStage), $"Current stage must be 1..{rules.StagesPerSeason}.");
        }

        long total = 0;
        checked
        {
            foreach (BonusContribution contribution in contributions)
            {
                if (contribution.EarnedSeason < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(contributions), "Earned season must be >= 1.");
                }

                if (contribution.EarnedStage < 1 || contribution.EarnedStage > rules.StagesPerSeason)
                {
                    throw new ArgumentOutOfRangeException(nameof(contributions), $"Earned stage must be 1..{rules.StagesPerSeason}.");
                }

                if (contribution.EarnedSeason > currentSeason)
                {
                    throw new InvalidOperationException(
                        $"Bonus from future season {contribution.EarnedSeason} cannot contribute to season {currentSeason}.");
                }

                if (contribution.EarnedSeason == currentSeason && contribution.EarnedStage >= currentStage)
                {
                    continue;
                }

                int age = currentSeason - contribution.EarnedSeason;
                Bonus decayed = ScoringCalculator.ApplyDecay(contribution.Earned, age, rules);
                total += decayed.Thousandths;
            }

            return Bonus.FromThousandths(checked((int)total));
        }
    }

    /// <summary>
    /// Computes the active bonus at a season boundary from per-season totals.
    /// Each season total decays by <c>currentSeason - season</c>. The current
    /// season total is treated as fully active (age 0); callers at a season
    /// boundary normally pass zero for the current season because no stage of
    /// the new season has completed yet.
    /// </summary>
    public static Bonus EffectiveBonusFromSeasonTotals(
        IEnumerable<SeasonBonus> seasonTotals,
        int currentSeason,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(seasonTotals);
        ArgumentNullException.ThrowIfNull(rules);

        if (currentSeason < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(currentSeason), "Current season must be >= 1.");
        }

        long total = 0;
        checked
        {
            foreach (SeasonBonus seasonBonus in seasonTotals)
            {
                if (seasonBonus.Season < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(seasonTotals), "Season must be >= 1.");
                }

                if (seasonBonus.Season > currentSeason)
                {
                    throw new InvalidOperationException(
                        $"Bonus from future season {seasonBonus.Season} cannot contribute to season {currentSeason}.");
                }

                int age = currentSeason - seasonBonus.Season;
                Bonus decayed = ScoringCalculator.ApplyDecay(seasonBonus.Total, age, rules);
                total += decayed.Thousandths;
            }

            return Bonus.FromThousandths(checked((int)total));
        }
    }
}
