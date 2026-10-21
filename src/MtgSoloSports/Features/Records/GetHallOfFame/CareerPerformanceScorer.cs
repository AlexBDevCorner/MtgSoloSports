using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Records.GetHallOfFame;

/// <summary>
/// Deterministic career-performance scorer for Hall of Fame Career Leaders
/// (MSS-083, weights <c>MSS-083-v1</c>). Every officially finalized placement
/// contributes positive points; a better rank in the same event always scores
/// more. Field-size normalization keeps P5/16 and P5/64 distinct, the last
/// place keeps a small nonzero floor, and one-athlete fields score full marks.
/// League tiers use explicit weights (Superleague ≫ Feeder 1 ≫ Feeder 2 ≫
/// Feeder 3); major cups carry major-event weights with team results credited
/// per participating athlete at a reduced share. Title/podium premiums reward
/// winning without letting lower-tier title counts dominate elite careers.
/// Pure integer math (no double/float, no RNG, no clock, no database order).
/// Calibration: with otherwise identical careers, 3× Superleague titles
/// (3 × 1500 = 4500) comfortably outscore 4× Feeder 1 titles (4 × 600 = 2400),
/// and two Superleague runner-ups (2 × 1210 = 2420) can beat four Feeder 1
/// titles, so broad high-level success is not trapped beneath lower-tier
/// accumulation. The Phyrexian Marauder (many feeder titles plus scattered
/// Superleague/midfield finishes) vs World Breaker (consecutive Superleague
/// titles) comparison is decided by these general weights, never by
/// athlete-specific constants: elite-tier points dominate per event.
/// </summary>
public static class CareerPerformanceScorer
{
    public const string WeightsVersion = "MSS-083-v1";

    public const int SuperleagueWeight = 1000;
    public const int Feeder1Weight = 400;
    public const int Feeder2Weight = 200;
    public const int Feeder3Weight = 100;
    public const int ColorCupIndividualWeight = 800;
    public const int ColorCupTeamWeight = 600;
    public const int TypeCupTeamWeight = 600;

    /// <summary>Per-athlete share of a team result (one of four legs).</summary>
    public const int TeamSharePermille = 250;

    /// <summary>
    /// Reduced credit for an eliminated Type Cup qualifier: 30% of the
    /// equivalent Final team score. Finalists never receive this credit; only
    /// their authoritative Final result counts.
    /// </summary>
    public const int QualificationCreditPermille = 300;

    public const int FirstPlacePremiumPermille = 1500;
    public const int SecondPlacePremiumPermille = 1250;
    public const int ThirdPlacePremiumPermille = 1150;
    public const int NonPodiumPremiumPermille = 1000;

    public const int PlacementScale = 1000;
    public const int PremiumScale = 1000;
    public const int ShareScale = 1000;

    public enum Category
    {
        Superleague = 0,
        Feeder1 = 1,
        Feeder2 = 2,
        Feeder3 = 3,
        ColorCupIndividual = 4,
        ColorCupTeam = 5,
        TypeCupTeam = 6,
    }

    public sealed record PlacementEvent(
        int AthleteId,
        int SeasonNumber,
        Category EventCategory,
        int Rank,
        int FieldSize,
        bool IsFinal);

    public sealed record AthleteCareerScore(
        int TotalPoints,
        int SuperleaguePoints,
        int Feeder1Points,
        int Feeder2Points,
        int Feeder3Points,
        int ColorCupIndividualPoints,
        int ColorCupTeamPoints,
        int TypeCupTeamPoints,
        int LeagueTitles,
        int SuperleagueTitles,
        int MajorWins,
        int ScoredEvents);

    public static int WeightFor(Category category) => category switch
    {
        Category.Superleague => SuperleagueWeight,
        Category.Feeder1 => Feeder1Weight,
        Category.Feeder2 => Feeder2Weight,
        Category.Feeder3 => Feeder3Weight,
        Category.ColorCupIndividual => ColorCupIndividualWeight,
        Category.ColorCupTeam => ColorCupTeamWeight,
        Category.TypeCupTeam => TypeCupTeamWeight,
        _ => throw new ArgumentOutOfRangeException(nameof(category), $"Unknown career category {(int)category}."),
    };

    public static Category CategoryForLevel(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => Category.Superleague,
        LeagueLevel.Feeder1 => Category.Feeder1,
        LeagueLevel.Feeder2 => Category.Feeder2,
        LeagueLevel.Feeder3 => Category.Feeder3,
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };

    /// <summary>
    /// Field-size-normalized placement factor in thousandths: 1000 at P1 down
    /// to a small positive floor at last place. One-athlete fields score 1000.
    /// </summary>
    public static int PlacementFactorThousandths(int rank, int fieldSize)
    {
        if (fieldSize < 1)
        {
            throw new InvalidOperationException($"Field size {fieldSize} is corrupt; must be positive.");
        }

        if (rank < 1 || rank > fieldSize)
        {
            throw new InvalidOperationException($"Rank {rank} outside field of {fieldSize}; sporting state is corrupt.");
        }

        if (fieldSize == 1)
        {
            return PlacementScale;
        }

        int factor = checked((fieldSize - rank + 1) * PlacementScale / fieldSize);
        return Math.Max(1, factor);
    }

    public static int AchievementPremiumPermille(int rank) => rank switch
    {
        1 => FirstPlacePremiumPermille,
        2 => SecondPlacePremiumPermille,
        3 => ThirdPlacePremiumPermille,
        _ when rank >= 4 => NonPodiumPremiumPermille,
        _ => throw new InvalidOperationException($"Rank {rank} is corrupt; must be positive."),
    };

    /// <summary>
    /// Scores one finalized placement. Integer-only:
    /// weight × placement × achievement × participation / 1e9.
    /// Qualification (non-final Type Cup) results carry the reduced credit.
    /// </summary>
    public static int ScoreEvent(PlacementEvent scored)
    {
        ArgumentNullException.ThrowIfNull(scored);
        if (scored.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Placement references corrupt athlete {scored.AthleteId}.");
        }

        if (scored.SeasonNumber < 1)
        {
            throw new InvalidOperationException($"Placement references corrupt season {scored.SeasonNumber}.");
        }

        int weight = WeightFor(scored.EventCategory);
        int factor = PlacementFactorThousandths(scored.Rank, scored.FieldSize);
        int premium = AchievementPremiumPermille(scored.Rank);
        int share = ParticipationShare(scored);
        long points = (long)weight * factor * premium * share;
        return checked((int)(points / ((long)PlacementScale * PremiumScale * ShareScale)));
    }

    internal static int ParticipationShare(PlacementEvent scored)
    {
        ArgumentNullException.ThrowIfNull(scored);
        int share = scored.EventCategory is Category.ColorCupTeam or Category.TypeCupTeam
            ? TeamSharePermille
            : ShareScale;
        if (scored.EventCategory == Category.TypeCupTeam && !scored.IsFinal)
        {
            share = checked(share * QualificationCreditPermille / ShareScale);
        }

        return share;
    }

    /// <summary>
    /// Aggregates scored events per athlete. Deterministic: sums are
    /// order-independent; every event counts exactly once (callers dedup).
    /// </summary>
    public static IReadOnlyDictionary<int, AthleteCareerScore> ComputeScores(IEnumerable<PlacementEvent> scoredEvents)
    {
        ArgumentNullException.ThrowIfNull(scoredEvents);
        Dictionary<int, Mutable> totals = [];
        foreach (PlacementEvent scored in scoredEvents)
        {
            AccumulateSingle(totals, scored);
        }

        return Freeze(totals);
    }

    internal static void AccumulateSingle(Dictionary<int, Mutable> totals, PlacementEvent scored)
    {
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(scored);
        int points = ScoreEvent(scored);
        if (points <= 0)
        {
            throw new InvalidOperationException($"Placement scored {points}; every finalized finish must score positive.");
        }

        if (!totals.TryGetValue(scored.AthleteId, out Mutable? mutable))
        {
            mutable = new Mutable();
            totals[scored.AthleteId] = mutable;
        }

        mutable.Total = checked(mutable.Total + points);
        mutable.ScoredEvents = checked(mutable.ScoredEvents + 1);
        AccumulateCategory(mutable, scored, points);
        AccumulateTitle(mutable, scored);
    }

    internal static void AccumulateCategory(Mutable mutable, PlacementEvent scored, int points)
    {
        ArgumentNullException.ThrowIfNull(mutable);
        ArgumentNullException.ThrowIfNull(scored);
        switch (scored.EventCategory)
        {
            case Category.Superleague:
                mutable.Superleague = checked(mutable.Superleague + points);
                break;
            case Category.Feeder1:
                mutable.Feeder1 = checked(mutable.Feeder1 + points);
                break;
            case Category.Feeder2:
                mutable.Feeder2 = checked(mutable.Feeder2 + points);
                break;
            case Category.Feeder3:
                mutable.Feeder3 = checked(mutable.Feeder3 + points);
                break;
            case Category.ColorCupIndividual:
                mutable.ColorIndividual = checked(mutable.ColorIndividual + points);
                break;
            case Category.ColorCupTeam:
                mutable.ColorTeam = checked(mutable.ColorTeam + points);
                break;
            case Category.TypeCupTeam:
                mutable.TypeTeam = checked(mutable.TypeTeam + points);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scored), $"Unknown career category {(int)scored.EventCategory}.");
        }
    }

    internal static void AccumulateTitle(Mutable mutable, PlacementEvent scored)
    {
        ArgumentNullException.ThrowIfNull(mutable);
        ArgumentNullException.ThrowIfNull(scored);
        if (scored.Rank != 1)
        {
            return;
        }

        switch (scored.EventCategory)
        {
            case Category.Superleague:
            case Category.Feeder1:
            case Category.Feeder2:
            case Category.Feeder3:
                mutable.LeagueTitles = checked(mutable.LeagueTitles + 1);
                if (scored.EventCategory == Category.Superleague)
                {
                    mutable.SuperleagueTitles = checked(mutable.SuperleagueTitles + 1);
                }

                break;
            case Category.ColorCupIndividual:
            case Category.ColorCupTeam:
                mutable.MajorWins = checked(mutable.MajorWins + 1);
                break;
            case Category.TypeCupTeam when scored.IsFinal:
                mutable.MajorWins = checked(mutable.MajorWins + 1);
                break;
            default:
                break;
        }
    }

    internal static IReadOnlyDictionary<int, AthleteCareerScore> Freeze(Dictionary<int, Mutable> totals)
    {
        ArgumentNullException.ThrowIfNull(totals);
        Dictionary<int, AthleteCareerScore> scores = new(totals.Count);
        foreach ((int athleteId, Mutable mutable) in totals)
        {
            scores[athleteId] = new AthleteCareerScore(
                mutable.Total,
                mutable.Superleague,
                mutable.Feeder1,
                mutable.Feeder2,
                mutable.Feeder3,
                mutable.ColorIndividual,
                mutable.ColorTeam,
                mutable.TypeTeam,
                mutable.LeagueTitles,
                mutable.SuperleagueTitles,
                mutable.MajorWins,
                mutable.ScoredEvents);
        }

        return scores;
    }

    internal sealed class Mutable
    {
        public int Total;
        public int Superleague;
        public int Feeder1;
        public int Feeder2;
        public int Feeder3;
        public int ColorIndividual;
        public int ColorTeam;
        public int TypeTeam;
        public int LeagueTitles;
        public int SuperleagueTitles;
        public int MajorWins;
        public int ScoredEvents;
    }
}
