using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Structural invariants for feeder automatic movement (MSS-058).
/// Fundamental failures throw and abort the mutation; corrupted sporting state
/// is never silently repaired. Enforces exact per-color rank bands, unique
/// athlete per transition, no overlapping qualifier fields, adjacent-tier only
/// (one level per postseason), and no cross-color movement.
/// </summary>
public static class FeederMovementInvariants
{
    public static void ValidateSelection(
        FeederMovementSelection.FeederPlan plan,
        IReadOnlyDictionary<int, LeagueEntity> f1ByColor,
        IReadOnlyDictionary<int, LeagueEntity> f2ByColor,
        IReadOnlyDictionary<int, LeagueEntity> f3ByColor,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> standingsByLeague,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(f1ByColor);
        ArgumentNullException.ThrowIfNull(f2ByColor);
        ArgumentNullException.ThrowIfNull(f3ByColor);
        ArgumentNullException.ThrowIfNull(standingsByLeague);
        ArgumentNullException.ThrowIfNull(rules);

        if (plan.PerColor.Count != rules.SportingColorCount)
        {
            throw new InvalidOperationException(
                $"Feeder movement must cover exactly {rules.SportingColorCount} colors, was {plan.PerColor.Count}.");
        }

        int expectedPerKindPerColor = 8;
        int expectedTotal = rules.SportingColorCount * 8 * 8;
        if (plan.All.Count != expectedTotal)
        {
            throw new InvalidOperationException(
                $"Feeder movement must persist exactly {expectedTotal} records (64 per color), was {plan.All.Count}.");
        }

        HashSet<int> athleteIds = new();
        foreach (FeederMovementSelection.FeederPick pick in plan.All)
        {
            if (pick.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Feeder movement contains invalid athlete id {pick.SaveAthleteId}.");
            }

            if (!athleteIds.Add(pick.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Feeder movement contains duplicate athlete id {pick.SaveAthleteId}; no athlete may appear in two qualifier boundaries.");
            }
        }

        foreach (FeederMovementSelection.ColorBoundaryPlan colorPlan in plan.PerColor)
        {
            ValidateColorPlan(colorPlan, f1ByColor, f2ByColor, f3ByColor, expectedPerKindPerColor);
        }

        ValidateRanksMatchStandings(plan, standingsByLeague);
        ValidateNoCrossColor(plan, f1ByColor, f2ByColor, f3ByColor);
    }

    internal static void ValidateColorPlan(
        FeederMovementSelection.ColorBoundaryPlan colorPlan,
        IReadOnlyDictionary<int, LeagueEntity> f1ByColor,
        IReadOnlyDictionary<int, LeagueEntity> f2ByColor,
        IReadOnlyDictionary<int, LeagueEntity> f3ByColor,
        int expected)
    {
        CheckColorCounts(colorPlan, expected);

        if (!f1ByColor.TryGetValue(colorPlan.SportingColor, out LeagueEntity? f1)
            || !f2ByColor.TryGetValue(colorPlan.SportingColor, out LeagueEntity? f2)
            || !f3ByColor.TryGetValue(colorPlan.SportingColor, out LeagueEntity? f3))
        {
            throw new InvalidOperationException($"Color {colorPlan.SportingColor} is missing a tiered league.");
        }

        CheckF1Bands(colorPlan, f1);
        CheckF2Bands(colorPlan, f2);
        CheckF3Bands(colorPlan, f3);
    }

    private static void CheckColorCounts(FeederMovementSelection.ColorBoundaryPlan colorPlan, int expected)
    {
        if (colorPlan.F1Incumbents.Count != expected
            || colorPlan.F1Relegated.Count != expected
            || colorPlan.F2PromotedToF1.Count != expected
            || colorPlan.F2Challengers.Count != expected
            || colorPlan.F2Incumbents.Count != expected
            || colorPlan.F2Relegated.Count != expected
            || colorPlan.F3PromotedToF2.Count != expected
            || colorPlan.F3Challengers.Count != expected)
        {
            throw new InvalidOperationException(
                $"Color {colorPlan.SportingColor} feeder movement must hold 8 per band, was " +
                $"{colorPlan.F1Incumbents.Count}/{colorPlan.F1Relegated.Count}/" +
                $"{colorPlan.F2PromotedToF1.Count}/{colorPlan.F2Challengers.Count}/" +
                $"{colorPlan.F2Incumbents.Count}/{colorPlan.F2Relegated.Count}/" +
                $"{colorPlan.F3PromotedToF2.Count}/{colorPlan.F3Challengers.Count}.");
        }
    }

    private static void CheckF1Bands(FeederMovementSelection.ColorBoundaryPlan colorPlan, LeagueEntity f1)
    {
        foreach (FeederMovementSelection.FeederPick pick in colorPlan.F1Incumbents)
        {
            CheckBand(pick, f1.Id, 17, 24, MovementKind.FeederQualifierIncumbent);
        }

        foreach (FeederMovementSelection.FeederPick pick in colorPlan.F1Relegated)
        {
            CheckBand(pick, f1.Id, 25, 32, MovementKind.FeederAutomaticRelegation);
        }
    }

    private static void CheckF2Bands(FeederMovementSelection.ColorBoundaryPlan colorPlan, LeagueEntity f2)
    {
        foreach (FeederMovementSelection.FeederPick pick in colorPlan.F2PromotedToF1)
        {
            CheckBand(pick, f2.Id, 1, 8, MovementKind.FeederAutomaticPromotion);
        }

        foreach (FeederMovementSelection.FeederPick pick in colorPlan.F2Challengers)
        {
            CheckBand(pick, f2.Id, 9, 16, MovementKind.FeederQualifierChallenger);
        }

        foreach (FeederMovementSelection.FeederPick pick in colorPlan.F2Incumbents)
        {
            CheckBand(pick, f2.Id, 17, 24, MovementKind.FeederQualifierIncumbent);
        }

        foreach (FeederMovementSelection.FeederPick pick in colorPlan.F2Relegated)
        {
            CheckBand(pick, f2.Id, 25, 32, MovementKind.FeederAutomaticRelegation);
        }
    }

    private static void CheckF3Bands(FeederMovementSelection.ColorBoundaryPlan colorPlan, LeagueEntity f3)
    {
        foreach (FeederMovementSelection.FeederPick pick in colorPlan.F3PromotedToF2)
        {
            CheckBand(pick, f3.Id, 1, 8, MovementKind.FeederAutomaticPromotion);
        }

        foreach (FeederMovementSelection.FeederPick pick in colorPlan.F3Challengers)
        {
            CheckBand(pick, f3.Id, 9, 16, MovementKind.FeederQualifierChallenger);
        }
    }

    internal static void CheckBand(
        FeederMovementSelection.FeederPick pick,
        int expectedLeague,
        int firstRank,
        int lastRank,
        MovementKind expectedKind)
    {
        if (pick.FromLeagueId != expectedLeague)
        {
            throw new InvalidOperationException(
                $"Feeder pick {pick.SaveAthleteId} must come from league {expectedLeague}, was {pick.FromLeagueId}.");
        }

        if (pick.FromSeasonRank < firstRank || pick.FromSeasonRank > lastRank)
        {
            throw new InvalidOperationException(
                $"Feeder pick rank {pick.FromSeasonRank} is outside places {firstRank}-{lastRank}.");
        }

        if (pick.Kind != expectedKind)
        {
            throw new InvalidOperationException($"Feeder pick {pick.SaveAthleteId} has corrupt kind {pick.Kind}.");
        }
    }

    internal static void ValidateRanksMatchStandings(
        FeederMovementSelection.FeederPlan plan,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> standingsByLeague)
    {
        Dictionary<int, int> rankByAthlete = new();
        foreach ((int _, IReadOnlyList<SeasonStandingEntity> rows) in standingsByLeague)
        {
            foreach (SeasonStandingEntity row in rows)
            {
                rankByAthlete[row.SaveAthleteId] = row.SeasonRank;
            }
        }

        foreach (FeederMovementSelection.FeederPick pick in plan.All)
        {
            if (!rankByAthlete.TryGetValue(pick.SaveAthleteId, out int actual))
            {
                throw new InvalidOperationException($"Feeder movement athlete {pick.SaveAthleteId} has no final standing.");
            }

            if (actual != pick.FromSeasonRank)
            {
                throw new InvalidOperationException(
                    $"Athlete {pick.SaveAthleteId} rank {pick.FromSeasonRank} does not match final standing rank {actual}.");
            }
        }
    }

    internal static void ValidateNoCrossColor(
        FeederMovementSelection.FeederPlan plan,
        IReadOnlyDictionary<int, LeagueEntity> f1ByColor,
        IReadOnlyDictionary<int, LeagueEntity> f2ByColor,
        IReadOnlyDictionary<int, LeagueEntity> f3ByColor)
    {
        Dictionary<int, int> colorByLeague = new();
        foreach ((int color, LeagueEntity league) in f1ByColor)
        {
            colorByLeague[league.Id] = color;
        }

        foreach ((int color, LeagueEntity league) in f2ByColor)
        {
            colorByLeague[league.Id] = color;
        }

        foreach ((int color, LeagueEntity league) in f3ByColor)
        {
            colorByLeague[league.Id] = color;
        }

        foreach (FeederMovementSelection.FeederPick pick in plan.All)
        {
            if (!colorByLeague.TryGetValue(pick.FromLeagueId, out int leagueColor))
            {
                throw new InvalidOperationException($"Feeder pick {pick.SaveAthleteId} references unknown league {pick.FromLeagueId}.");
            }

            if (leagueColor != pick.SportingColor)
            {
                throw new InvalidOperationException(
                    $"Feeder pick {pick.SaveAthleteId} color {pick.SportingColor} does not match league color {leagueColor}; no cross-color movement.");
            }
        }
    }

    /// <summary>
    /// Validates that every persisted feeder movement is between adjacent tiers.
    /// Uses league levels: F1(1)↔F2(2) and F2(2)↔F3(3) only. Throws on any skip.
    /// </summary>
    public static void ValidateAdjacentTiers(
        IReadOnlyList<MovementEntity> movements,
        IReadOnlyDictionary<int, LeagueEntity> leaguesById)
    {
        ArgumentNullException.ThrowIfNull(movements);
        ArgumentNullException.ThrowIfNull(leaguesById);

        foreach (MovementEntity movement in movements)
        {
            if (movement.Kind != (int)MovementKind.FeederAutomaticPromotion
                && movement.Kind != (int)MovementKind.FeederAutomaticRelegation
                && movement.Kind != (int)MovementKind.FeederQualifierIncumbent
                && movement.Kind != (int)MovementKind.FeederQualifierChallenger)
            {
                continue;
            }

            if (!leaguesById.TryGetValue(movement.FromLeagueId, out LeagueEntity? from)
                || !leaguesById.TryGetValue(movement.ToLeagueId, out LeagueEntity? to))
            {
                // Pool sentinel is never used for normal feeder movement.
                throw new InvalidOperationException($"Feeder movement {movement.Id} has corrupt league linkage.");
            }

            LeagueLevel fromLevel = LeagueEntityLevels.GetLevel(from);
            LeagueLevel toLevel = LeagueEntityLevels.GetLevel(to);
            int distance = Math.Abs(LeagueHierarchy.Order(fromLevel) - LeagueHierarchy.Order(toLevel));
            bool provisionalSameTier = movement.Kind is (int)MovementKind.FeederQualifierIncumbent
                or (int)MovementKind.FeederQualifierChallenger;
            if (provisionalSameTier)
            {
                // Qualifier-candidate markers provisionally remain in their source
                // tier; the qualifier decides the final adjacent move.
                if (distance != 0)
                {
                    throw new InvalidOperationException(
                        $"Feeder qualifier marker {movement.Id} must provisionally remain in its tier.");
                }

                continue;
            }

            if (distance != 1)
            {
                throw new InvalidOperationException(
                    $"Feeder movement {movement.Id} must be between adjacent tiers, was {fromLevel} → {toLevel}.");
            }
        }
    }
}
