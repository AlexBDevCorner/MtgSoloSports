using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.SimulationKernel.Careers;

/// <summary>
/// Deterministic career-performance scorer for Hall of Fame Career Leaders
/// (MSS-083 weights, extended by MSS-104 with <c>MSS-104-v1</c>). Every
/// officially finalized placement contributes positive points; a better rank
/// in the same event always scores more. Field-size normalization keeps P5/16
/// and P5/64 distinct, the last place keeps a small nonzero floor, and
/// one-athlete fields score full marks. League tiers use explicit weights
/// (Superleague ≫ Feeder 1 ≫ Feeder 2 ≫ Feeder 3); major cups carry
/// major-event weights with team results credited per participating athlete at
/// a reduced share. Title/podium premiums reward winning without letting
/// lower-tier title counts dominate elite careers. Pure integer math (no
/// double/float, no RNG, no clock, no database order).
/// Calibration: with otherwise identical careers, 3× Superleague titles
/// (3 × 1500 = 4500) comfortably outscore 4× Feeder 1 titles (4 × 600 = 2400),
/// and two Superleague runner-ups (2 × 1210 = 2420) can beat four Feeder 1
/// titles, so broad high-level success is not trapped beneath lower-tier
/// accumulation. The Phyrexian Marauder (many feeder titles plus scattered
/// Superleague/midfield finishes) vs World Breaker (consecutive Superleague
/// titles) comparison is decided by these general weights, never by
/// athlete-specific constants: elite-tier points dominate per event.
/// Chaos Cup (MSS-104): individual weight 450 sits below Color individual 800
/// and Superleague 1000; the Final uses the standard formula over 32 places
/// (champion 450 × 1000 × 1500 / 1e9 = 675, last place 13). Eliminations carry
/// no global rank 33..1024; they use a synthetic 5-tier exit encoding
/// (Rank 1 = semifinal exit .. Rank 5 = first-stage exit, FieldSize 5) with a
/// fixed tunable curve 10/7/4/2/1 so every entrant scores positive, each stage
/// scores uniformly, and Final P32 (13) strictly exceeds the best exit (10).
/// Tune by editing the exit constants while preserving
/// 1 ≤ exit ≤ 10 &lt; 13 and champion ≈ 675.
/// Shared kernel home (MSS-094): both Records Hall of Fame and Reports
/// almanac/dossier paths must use this component; no feature-local copy.
/// SimulationKernel pure: no HTTP, EF Core, filesystem, clock or RNG.
/// </summary>
public static class CareerPerformanceScorer
{
    public const string WeightsVersion = "MSS-104-v1";

    public const int SuperleagueWeight = 1000;
    public const int Feeder1Weight = 400;
    public const int Feeder2Weight = 200;
    public const int Feeder3Weight = 100;
    public const int ColorCupIndividualWeight = 800;
    public const int ColorCupTeamWeight = 600;
    public const int TypeCupTeamWeight = 600;
    public const int ChaosCupIndividualWeight = 450;

    /// <summary>Synthetic exit field: five elimination tiers, not a global rank.</summary>
    public const int ChaosExitFieldSize = 5;

    /// <summary>Eliminated in the 2-group stage (semifinal): best exit, must stay below Final P32.</summary>
    public const int ChaosExitPointsStageWith2Groups = 10;

    /// <summary>Eliminated in the 4-group stage.</summary>
    public const int ChaosExitPointsStageWith4Groups = 7;

    /// <summary>Eliminated in the 8-group stage.</summary>
    public const int ChaosExitPointsStageWith8Groups = 4;

    /// <summary>Eliminated in the 16-group stage.</summary>
    public const int ChaosExitPointsStageWith16Groups = 2;

    /// <summary>Eliminated in the 32-group stage (first stage): smallest positive credit.</summary>
    public const int ChaosExitPointsStageWith32Groups = 1;

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
        ChaosCupIndividual = 7,
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
        int ChaosCupIndividualPoints,
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
        Category.ChaosCupIndividual => ChaosCupIndividualWeight,
        _ => throw new ArgumentOutOfRangeException(nameof(category), $"Unknown career category {(int)category}."),
    };

    /// <summary>
    /// Fixed Chaos exit credit for the synthetic 5-tier encoding
    /// (Rank 1 = semifinal exit .. Rank 5 = first-stage exit, FieldSize 5).
    /// Same rank always scores the same positive points; Final P32 (13)
    /// strictly exceeds the best exit (10).
    /// </summary>
    public static int ChaosExitPointsForRank(int rank) => rank switch
    {
        1 => ChaosExitPointsStageWith2Groups,
        2 => ChaosExitPointsStageWith4Groups,
        3 => ChaosExitPointsStageWith8Groups,
        4 => ChaosExitPointsStageWith16Groups,
        5 => ChaosExitPointsStageWith32Groups,
        _ => throw new InvalidOperationException($"Chaos exit rank {rank} is corrupt; must be 1..{ChaosExitFieldSize}."),
    };

    /// <summary>
    /// Maps a Chaos stage index (0..4, nonfinal) to the synthetic exit rank
    /// (5..1). Stage 4 (2 groups, semifinal) is Rank 1; stage 0 (32 groups) is
    /// Rank 5. The Final (stage 5) has no exit rank; its athletes hold P1..P32.
    /// </summary>
    public static int ChaosExitRankForStage(int stageIndex) => stageIndex switch
    {
        4 => 1,
        3 => 2,
        2 => 3,
        1 => 4,
        0 => 5,
        _ => throw new InvalidOperationException($"Chaos stage {stageIndex} has no exit rank; only stages 0..4 eliminate athletes."),
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
    /// weight × placement × achievement × participation / 1e9, floored to a
    /// minimum of one point so every finalized finish stays positive even when
    /// integer truncation would otherwise erase a tail placement in a very
    /// large field (e.g. synthetic selection scaffolding with hundreds of
    /// athletes in one league). Qualification (non-final Type Cup) results
    /// carry the reduced credit. Chaos Finals use the standard formula over 32
    /// places; Chaos eliminations use the fixed 10/7/4/2/1 exit curve over the
    /// synthetic 5-tier encoding (Rank 1..5, FieldSize 5) so no global
    /// 33..1024 rank is fabricated.
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

        if (scored.EventCategory == Category.ChaosCupIndividual && !scored.IsFinal)
        {
            if (scored.FieldSize != ChaosExitFieldSize)
            {
                throw new InvalidOperationException($"Chaos exit field must be {ChaosExitFieldSize}, was {scored.FieldSize}.");
            }

            return ChaosExitPointsForRank(scored.Rank);
        }

        if (scored.EventCategory == Category.ChaosCupIndividual && scored.IsFinal)
        {
            if (scored.FieldSize != 32)
            {
                throw new InvalidOperationException($"Chaos Final field must be 32, was {scored.FieldSize}.");
            }
        }

        int weight = WeightFor(scored.EventCategory);
        int factor = PlacementFactorThousandths(scored.Rank, scored.FieldSize);
        int premium = AchievementPremiumPermille(scored.Rank);
        int share = ParticipationShare(scored);
        long points = (long)weight * factor * premium * share;
        int truncated = checked((int)(points / ((long)PlacementScale * PremiumScale * ShareScale)));
        return Math.Max(1, truncated);
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
            case Category.ChaosCupIndividual:
                mutable.ChaosIndividual = checked(mutable.ChaosIndividual + points);
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
            case Category.ChaosCupIndividual when scored.IsFinal:
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
                mutable.ChaosIndividual,
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
        public int ChaosIndividual;
        public int LeagueTitles;
        public int SuperleagueTitles;
        public int MajorWins;
        public int ScoredEvents;
    }
}
