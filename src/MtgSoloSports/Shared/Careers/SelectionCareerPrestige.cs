using MtgSoloSports.SimulationKernel.Careers;

namespace MtgSoloSports.Shared.Careers;

/// <summary>
/// Shared career-prestige bridge for squad selection (MSS-096).
/// New Color/Type Cup selections use the same weighted full-placement career
/// history as Career Leaders: prestige raw is the athlete's total career
/// points through the selected source season, including nonpodium league and
/// Cup results. The seven career-category points sum exactly to prestige raw.
/// Missing athletes (no finalized placements) map to zero prestige.
/// Pure integer math, deterministic, no RNG/clock.
/// </summary>
public static class SelectionCareerPrestige
{
    public const string PrestigeSource = "Career-MSS-083-v1";

    public static int PrestigeRawFor(
        int athleteId,
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> scores)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (athleteId <= 0)
        {
            throw new InvalidOperationException($"Selection prestige has corrupt athlete {athleteId}.");
        }

        return scores.TryGetValue(athleteId, out CareerPerformanceScorer.AthleteCareerScore? score)
            ? score.TotalPoints
            : 0;
    }

    public static void BreakdownFor(
        int athleteId,
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> scores,
        out int superleague,
        out int feeder1,
        out int feeder2,
        out int feeder3,
        out int colorIndividual,
        out int colorTeam,
        out int typeTeam)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (athleteId <= 0)
        {
            throw new InvalidOperationException($"Selection prestige has corrupt athlete {athleteId}.");
        }

        if (!scores.TryGetValue(athleteId, out CareerPerformanceScorer.AthleteCareerScore? score))
        {
            superleague = 0;
            feeder1 = 0;
            feeder2 = 0;
            feeder3 = 0;
            colorIndividual = 0;
            colorTeam = 0;
            typeTeam = 0;
            return;
        }

        superleague = score.SuperleaguePoints;
        feeder1 = score.Feeder1Points;
        feeder2 = score.Feeder2Points;
        feeder3 = score.Feeder3Points;
        colorIndividual = score.ColorCupIndividualPoints;
        colorTeam = score.ColorCupTeamPoints;
        typeTeam = score.TypeCupTeamPoints;
        ValidateSum(athleteId, score);
    }

    internal static void ValidateSum(int athleteId, CareerPerformanceScorer.AthleteCareerScore score)
    {
        ArgumentNullException.ThrowIfNull(score);
        checked
        {
            int sum = score.SuperleaguePoints + score.Feeder1Points + score.Feeder2Points + score.Feeder3Points
                + score.ColorCupIndividualPoints + score.ColorCupTeamPoints + score.TypeCupTeamPoints;
            if (sum != score.TotalPoints)
            {
                throw new InvalidOperationException(
                    $"Career prestige breakdown for athlete {athleteId} sums to {sum} but total raw is {score.TotalPoints}.");
            }
        }

        if (score.TotalPoints < 0
            || score.SuperleaguePoints < 0 || score.Feeder1Points < 0 || score.Feeder2Points < 0
            || score.Feeder3Points < 0 || score.ColorCupIndividualPoints < 0
            || score.ColorCupTeamPoints < 0 || score.TypeCupTeamPoints < 0)
        {
            throw new InvalidOperationException($"Career prestige for athlete {athleteId} has corrupt negative points.");
        }
    }
}
