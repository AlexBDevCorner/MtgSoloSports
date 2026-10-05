using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Scoring;

/// <summary>
/// Pure fixed-point sporting mathematics. All inputs and outputs are integers.
/// Final round points: base points x (1 + active bonus%), retained in thousandths.
/// </summary>
public static class ScoringCalculator
{
    /// <summary>
    /// Returns base championship points for a 1-based finishing position, with no bonus multiplier.
    /// </summary>
    public static Points ChampionshipPointsForPosition(int position, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (position < 1 || position > rules.LeagueSize)
        {
            throw new ArgumentOutOfRangeException(nameof(position), $"Position must be 1..{rules.LeagueSize}.");
        }

        checked
        {
            return Points.FromPoints(rules.ScoringTable[position - 1]);
        }
    }

    /// <summary>
    /// Returns the base round points for a 1-based finishing position, with no bonus multiplier.
    /// </summary>
    public static Points BaseRoundPointsForPosition(int position, RulesV1 rules) => ChampionshipPointsForPosition(position, rules);

    /// <summary>
    /// Type Cup team-event base points for a 1-based finishing position (Game Rules §15).
    /// Positions 1..32 use the snapshot scoring table exactly, so every field of
    /// 2–32 teams scores identically to <see cref="BaseRoundPointsForPosition"/>.
    /// Positions beyond the 32-entry league table score the table minimum (1 point):
    /// the extension is deterministic, integer-only, monotonic non-increasing, and
    /// additive, so existing saves and persisted replays for 2–32-team fields are
    /// byte-identical and no rules-version bump is required.
    /// </summary>
    public static Points TypeCupBasePointsForPosition(int position, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (position < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Position must be at least 1.");
        }

        checked
        {
            int points = position <= rules.ScoringTable.Count
                ? rules.ScoringTable[position - 1]
                : rules.ScoringTable[rules.ScoringTable.Count - 1];
            return Points.FromPoints(points);
        }
    }

    /// <summary>
    /// Type Cup team-event final points for a 1-based finishing position with the
    /// given active bonus, using <see cref="TypeCupBasePointsForPosition"/>.
    /// </summary>
    public static Points TypeCupFinalPointsForPosition(int position, Bonus activeBonus, RulesV1 rules)
    {
        Points basePoints = TypeCupBasePointsForPosition(position, rules);
        return ApplyBonus(basePoints, activeBonus);
    }

    /// <summary>
    /// Applies active bonus to base points using integer arithmetic:
    /// final = baseThousandths x (100000 + bonusThousandths) / 100000,
    /// truncated to thousandths. Bonus is a percentage: 7552 is +7.552%.
    /// </summary>
    public static Points ApplyBonus(Points basePoints, Bonus activeBonus)
    {
        checked
        {
            long product = (long)basePoints.Thousandths * (RulesV1.BonusPercentScale + activeBonus.Thousandths);
            return Points.FromThousandths((int)(product / RulesV1.BonusPercentScale));
        }
    }

    /// <summary>
    /// Final round points for a 1-based finishing position with the given active bonus.
    /// </summary>
    public static Points FinalRoundPointsForPosition(int position, Bonus activeBonus, RulesV1 rules)
    {
        Points basePoints = BaseRoundPointsForPosition(position, rules);
        return ApplyBonus(basePoints, activeBonus);
    }

    /// <summary>
    /// Sums final round points into a stage total.
    /// </summary>
    public static Points StageTotal(IEnumerable<Points> roundFinals)
    {
        ArgumentNullException.ThrowIfNull(roundFinals);
        int total = 0;
        checked
        {
            foreach (Points round in roundFinals)
            {
                total += round.Thousandths;
            }
        }

        return Points.FromThousandths(total);
    }

    /// <summary>
    /// Round bonus earned for a 1-based finishing position. Places outside 1..10 earn zero.
    /// Tiered scale comes from the versioned snapshot: Superleague 2/1, Feeder 1 1/1,
    /// Feeder 2 1/2, Feeder 3 1/4 (v2). v1 supports Superleague and Feeder 1 only.
    /// </summary>
    public static Bonus RoundBonusForPosition(int position, RulesV1 rules, LeagueLevel level)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (position < 1 || position > rules.LeagueSize)
        {
            throw new ArgumentOutOfRangeException(nameof(position), $"Position must be 1..{rules.LeagueSize}.");
        }

        int thousandths = position <= rules.RoundBonusThousandths.Count ? rules.RoundBonusThousandths[position - 1] : 0;
        TierBonusScale scale = rules.GetBonusScale(level);
        return Bonus.FromThousandths(thousandths).ScaleRatio(scale.Numerator, scale.Denominator);
    }

    /// <summary>
    /// Round bonus earned for a 1-based finishing position. Places outside 1..10 earn zero.
    /// v1 compatibility path: Superleague earns twice the regular amount.
    /// New code should pass a <see cref="LeagueLevel"/> instead.
    /// </summary>
    public static Bonus RoundBonusForPosition(int position, RulesV1 rules, bool isSuperleague) =>
        RoundBonusForPosition(position, rules, LeagueHierarchy.FromLegacySuperleagueFlag(isSuperleague));

    /// <summary>
    /// Stage bonus earned for a 1-based finishing position. Places outside 1..10 earn zero.
    /// Tiered scale comes from the versioned snapshot (see <see cref="RoundBonusForPosition(int, RulesV1, LeagueLevel)"/>).
    /// </summary>
    public static Bonus StageBonusForPosition(int position, RulesV1 rules, LeagueLevel level)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (position < 1 || position > rules.LeagueSize)
        {
            throw new ArgumentOutOfRangeException(nameof(position), $"Position must be 1..{rules.LeagueSize}.");
        }

        int thousandths = position <= rules.StageBonusThousandths.Count ? rules.StageBonusThousandths[position - 1] : 0;
        TierBonusScale scale = rules.GetBonusScale(level);
        return Bonus.FromThousandths(thousandths).ScaleRatio(scale.Numerator, scale.Denominator);
    }

    /// <summary>
    /// Stage bonus earned for a 1-based finishing position. Places outside 1..10 earn zero.
    /// v1 compatibility path: Superleague earns twice the regular amount.
    /// New code should pass a <see cref="LeagueLevel"/> instead.
    /// </summary>
    public static Bonus StageBonusForPosition(int position, RulesV1 rules, bool isSuperleague) =>
        StageBonusForPosition(position, rules, LeagueHierarchy.FromLegacySuperleagueFlag(isSuperleague));

    /// <summary>
    /// Applies season-age decay to a bonus earned in an older season.
    /// Age 0 is the current season; age 5 or more contributes zero.
    /// </summary>
    public static Bonus ApplyDecay(Bonus earned, int seasonAge, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (seasonAge < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seasonAge), "Season age cannot be negative.");
        }

        int index = seasonAge >= rules.BonusAgeWeightsThousandths.Count ? rules.BonusAgeWeightsThousandths.Count - 1 : seasonAge;
        int weight = rules.BonusAgeWeightsThousandths[index];
        checked
        {
            long product = (long)earned.Thousandths * weight;
            return Bonus.FromThousandths((int)(product / RulesV1.FixedScale));
        }
    }

    /// <summary>
    /// Computes a Color Cup selection rating from normalized components using snapshot weights.
    /// </summary>
    public static SelectionScore SelectionRating(
        int bonusNormThousandths,
        int performanceNormThousandths,
        int formNormThousandths,
        int prestigeNormThousandths,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return SelectionScore.Combine(
            bonusNormThousandths,
            performanceNormThousandths,
            formNormThousandths,
            prestigeNormThousandths,
            rules.CupBonusWeightPermille,
            rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille,
            rules.CupPrestigeWeightPermille);
    }
}
