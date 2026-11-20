using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Legacy win-only calculator for Cup career prestige (MSS-065, v1-v3 selections).
/// v4 selections (MSS-096) use the shared Career Leaders career-performance
/// points through the selected source season instead; this component remains
/// for reading historical v1-v3 selections and for regression coverage.
/// New Color/Type Cup selections must use
/// <c>Shared.Careers.CareerPerformanceLoader</c> plus
/// <c>SimulationKernel.Careers.CareerPerformanceScorer</c>; no duplicated
/// feeder/Super/stage counting may live in the feature slices.
/// Prestige is cumulative with no time decay: historical achievements keep
/// their original league context permanently, so an athlete relegated to F3
/// retains higher prestige from earlier Superleague results.
/// v1/v2 snapshots use the legacy integer model (feeder title 100,
/// Superleague title 300, appearance 20, stage 10/5/2 regardless of league,
/// major Cup title 150). v3 tiered saves use quarter-prestige-points
/// (scale 4): Super/F1/F2/F3 titles 1200/400/200/100, Super appearance 80,
/// major Cup title 600, Super stage 80/40/16, F1 40/20/8, F2 20/10/4,
/// F3 10/5/2. No floating-point or decimal sporting math.
/// Feeder titles resolve via the persisted league level of the honour's own
/// competition (historical v1 division None maps to Feeder 1); stage podiums
/// resolve via each stage row's own LeagueId, never current membership.
/// Superleague appearances count completed Superleague seasons only; feeder
/// and pool participation add nothing. Cup runner-up/third-place honours and
/// Type Cup qualification-group success (which persists no honour) add nothing.
/// Prestige resolution dispatches through the virtual tiered-prestige
/// accessors on the rules hierarchy (MSS-152), so a future RulesV4 subclass
/// inherits the v3 tiered model instead of falling into a legacy cast path;
/// v1/v2 snapshots keep their legacy values via the base accessors. Known
/// prestige-neutral honour kinds (Features.Records.HonourKind 5..17: league
/// and Cup runner-up/third plus the MSS-104 Chaos podium) are ignored, and
/// any other kind int aborts loudly as corruption.
/// Unknown or corrupt league references for achievements that require a level
/// abort loudly; Cup majors carry sentinel LeagueIds and never resolve levels.
/// SimulationKernel pure: no HTTP, EF Core, filesystem, clock or RNG.
/// </summary>
public static class CupPrestigeCalculator
{
    /// <summary>Exact integer prestige unit scale: four scaled points per semantic point.</summary>
    public const int PrestigeScale = 4;

    // Persisted honour kinds (Features.Records.HonourKind values, stored as ints).
    // The calculator takes raw ints so SimulationKernel never references feature code.
    private const int FeederTitleKind = 0;
    private const int SuperleagueTitleKind = 1;
    private const int ColorCupIndividualChampionKind = 2;
    private const int ColorCupTeamChampionKind = 3;
    private const int TypeCupTeamChampionKind = 4;

    // Known prestige-neutral kinds (Features.Records.HonourKind values 5..17):
    // league/Cup runner-up and third places (5..14) plus the MSS-104 Chaos Cup
    // individual podium (15..17). Anything outside 0..17 is a corrupt kind int
    // and aborts loudly instead of being silently swallowed (MSS-152).
    private const int FirstNeutralHonourKind = 5;

    private const int LastNeutralHonourKind = 17;

    public sealed record PrestigeHonour(int AthleteId, int Kind, int LeagueId);

    public sealed record PrestigeStage(int AthleteId, int LeagueId, int Rank);

    public sealed record PrestigeMembership(int AthleteId, int? LeagueId);

    /// <summary>
    /// Explainable prestige contributions in the same scaled units as
    /// <see cref="PrestigeRaw"/>. The ten category raws sum exactly to
    /// <see cref="PrestigeBreakdown.TotalRaw"/>; reports persist these so
    /// selection prestige is auditable without resimulation.
    /// </summary>
    public sealed record PrestigeBreakdown(
        int SuperTitleRaw,
        int Feeder1TitleRaw,
        int Feeder2TitleRaw,
        int Feeder3TitleRaw,
        int AppearanceRaw,
        int SuperStageRaw,
        int Feeder1StageRaw,
        int Feeder2StageRaw,
        int Feeder3StageRaw,
        int MajorCupRaw,
        int TotalRaw)
    {
        public int TitlesRaw => checked(SuperTitleRaw + Feeder1TitleRaw + Feeder2TitleRaw + Feeder3TitleRaw);

        public int StagesRaw => checked(SuperStageRaw + Feeder1StageRaw + Feeder2StageRaw + Feeder3StageRaw);

        public void ValidateSum()
        {
            checked
            {
                int sum = SuperTitleRaw + Feeder1TitleRaw + Feeder2TitleRaw + Feeder3TitleRaw
                    + AppearanceRaw
                    + SuperStageRaw + Feeder1StageRaw + Feeder2StageRaw + Feeder3StageRaw
                    + MajorCupRaw;
                if (sum != TotalRaw)
                {
                    throw new InvalidOperationException(
                        $"Prestige breakdown sums to {sum} but total raw is {TotalRaw}.");
                }
            }
        }
    }

    /// <summary>
    /// Computes league-aware prestige for every athlete in one shared pass.
    /// Returns one breakdown per athlete id present in any input; athletes with
    /// no achievements map to an all-zero breakdown. Prestige resolution uses
    /// the virtual tiered-prestige accessors on the rules hierarchy
    /// (<see cref="RulesV1.GetLeagueTitlePrestige"/> and siblings, MSS-152),
    /// so RulesV4+ subclasses resolve through inheritance rather than version
    /// casts; v1/v2 snapshots keep the legacy level-blind values.
    /// </summary>
    public static IReadOnlyDictionary<int, PrestigeBreakdown> ComputeAll(
        IReadOnlyList<PrestigeHonour> honours,
        IReadOnlyList<PrestigeStage> stages,
        IReadOnlyList<PrestigeMembership> memberships,
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(honours);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(levelsByLeagueId);
        ArgumentNullException.ThrowIfNull(rules);

        Dictionary<int, Mutable> accumulators = new();
        ApplyAllHonours(accumulators, honours, levelsByLeagueId, rules);
        ApplyAllStages(accumulators, stages, levelsByLeagueId, rules);
        ApplyAllAppearances(accumulators, memberships, levelsByLeagueId, rules);
        return Finalize(accumulators);
    }

    private static void ApplyAllHonours(
        Dictionary<int, Mutable> accumulators,
        IReadOnlyList<PrestigeHonour> honours,
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        RulesV1 rules)
    {
        foreach (PrestigeHonour honour in honours)
        {
            if (honour is null)
            {
                throw new InvalidOperationException("Prestige honour input is null.");
            }

            if (honour.AthleteId <= 0)
            {
                throw new InvalidOperationException($"Prestige honour has corrupt athlete {honour.AthleteId}.");
            }

            Mutable acc = GetOrCreate(accumulators, honour.AthleteId);
            ApplyHonour(acc, honour, levelsByLeagueId, rules);
        }
    }

    private static void ApplyAllStages(
        Dictionary<int, Mutable> accumulators,
        IReadOnlyList<PrestigeStage> stages,
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        RulesV1 rules)
    {
        foreach (PrestigeStage stage in stages)
        {
            ApplySingleStage(accumulators, stage, levelsByLeagueId, rules);
        }
    }

    private static void ApplySingleStage(
        Dictionary<int, Mutable> accumulators,
        PrestigeStage stage,
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        RulesV1 rules)
    {
        if (stage is null)
        {
            throw new InvalidOperationException("Prestige stage input is null.");
        }

        if (stage.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Prestige stage has corrupt athlete {stage.AthleteId}.");
        }

        if (stage.Rank is not (1 or 2 or 3))
        {
            return;
        }

        LeagueLevel level = ResolveLevel(levelsByLeagueId, stage.LeagueId, "stage standing");
        Mutable acc = GetOrCreate(accumulators, stage.AthleteId);
        checked
        {
            acc.AddStage(level, rules.GetStagePrestige(level, stage.Rank));
        }
    }

    private static void ApplyAllAppearances(
        Dictionary<int, Mutable> accumulators,
        IReadOnlyList<PrestigeMembership> memberships,
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        RulesV1 rules)
    {
        foreach (PrestigeMembership membership in memberships)
        {
            ApplySingleAppearance(accumulators, membership, levelsByLeagueId, rules);
        }
    }

    private static void ApplySingleAppearance(
        Dictionary<int, Mutable> accumulators,
        PrestigeMembership membership,
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        RulesV1 rules)
    {
        if (membership is null)
        {
            throw new InvalidOperationException("Prestige membership input is null.");
        }

        if (membership.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Prestige membership has corrupt athlete {membership.AthleteId}.");
        }

        if (membership.LeagueId is null)
        {
            return;
        }

        LeagueLevel level = ResolveLevel(levelsByLeagueId, membership.LeagueId.Value, "season membership");
        if (level != LeagueLevel.Superleague)
        {
            return;
        }

        Mutable acc = GetOrCreate(accumulators, membership.AthleteId);
        checked
        {
            acc.AppearanceRaw += rules.GetSuperleagueAppearancePrestige();
        }
    }

    private static Dictionary<int, PrestigeBreakdown> Finalize(Dictionary<int, Mutable> accumulators)
    {
        Dictionary<int, PrestigeBreakdown> results = new(accumulators.Count);
        foreach (KeyValuePair<int, Mutable> entry in accumulators)
        {
            PrestigeBreakdown breakdown = entry.Value.ToBreakdown();
            breakdown.ValidateSum();
            results[entry.Key] = breakdown;
        }

        return results;
    }

    /// <summary>
    /// Convenience for a single athlete; missing athletes yield zero prestige.
    /// </summary>
    public static PrestigeBreakdown ComputeForAthlete(
        int athleteId,
        IReadOnlyList<PrestigeHonour> honours,
        IReadOnlyList<PrestigeStage> stages,
        IReadOnlyList<PrestigeMembership> memberships,
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        RulesV1 rules)
    {
        if (athleteId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(athleteId));
        }

        IReadOnlyDictionary<int, PrestigeBreakdown> all = ComputeAll(honours, stages, memberships, levelsByLeagueId, rules);
        return all.TryGetValue(athleteId, out PrestigeBreakdown? breakdown)
            ? breakdown
            : new PrestigeBreakdown(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static void ApplyHonour(
        Mutable acc,
        PrestigeHonour honour,
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        RulesV1 rules)
    {
        switch (honour.Kind)
        {
            case FeederTitleKind:
                ApplyFeederTitle(acc, honour, levelsByLeagueId, rules);
                break;

            case SuperleagueTitleKind:
                ApplySuperTitle(acc, honour, levelsByLeagueId, rules);
                break;

            case ColorCupIndividualChampionKind:
            case ColorCupTeamChampionKind:
            case TypeCupTeamChampionKind:
                ApplyMajorTitle(acc, rules);
                break;

            case >= FirstNeutralHonourKind and <= LastNeutralHonourKind:
                // League/Cup runner-up and third-place honours (5..14) and the
                // MSS-104 Chaos Cup individual podium (15..17) are honours but
                // prestige-neutral by win-only prestige semantics; ignore.
                break;

            default:
                throw new InvalidOperationException(
                    $"Prestige honour has unknown kind {honour.Kind}; sporting state is corrupt.");
        }
    }

    private static void ApplyFeederTitle(
        Mutable acc,
        PrestigeHonour honour,
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        RulesV1 rules)
    {
        LeagueLevel level = ResolveLevel(levelsByLeagueId, honour.LeagueId, "feeder title honour");
        if (level == LeagueLevel.Superleague)
        {
            throw new InvalidOperationException(
                $"Feeder title honour for athlete {honour.AthleteId} references Superleague competition; sporting state is corrupt.");
        }

        checked
        {
            acc.AddTitle(level, rules.GetLeagueTitlePrestige(level));
        }
    }

    private static void ApplySuperTitle(
        Mutable acc,
        PrestigeHonour honour,
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        RulesV1 rules)
    {
        LeagueLevel level = ResolveLevel(levelsByLeagueId, honour.LeagueId, "Superleague title honour");
        if (level != LeagueLevel.Superleague)
        {
            throw new InvalidOperationException(
                $"Superleague title honour for athlete {honour.AthleteId} references {LeagueHierarchy.DisplayName(level)} competition; sporting state is corrupt.");
        }

        checked
        {
            acc.SuperTitleRaw += rules.GetLeagueTitlePrestige(LeagueLevel.Superleague);
        }
    }

    private static void ApplyMajorTitle(Mutable acc, RulesV1 rules)
    {
        checked
        {
            acc.MajorCupRaw += rules.GetMajorCupTitlePrestige();
        }
    }

    private static LeagueLevel ResolveLevel(
        IReadOnlyDictionary<int, LeagueLevel> levelsByLeagueId,
        int leagueId,
        string context)
    {
        if (!levelsByLeagueId.TryGetValue(leagueId, out LeagueLevel level))
        {
            throw new InvalidOperationException(
                $"Prestige {context} references unknown league {leagueId}; sporting state is corrupt.");
        }

        return level;
    }

    private static Mutable GetOrCreate(Dictionary<int, Mutable> map, int athleteId)
    {
        if (!map.TryGetValue(athleteId, out Mutable? acc))
        {
            acc = new Mutable();
            map[athleteId] = acc;
        }

        return acc;
    }

    private sealed class Mutable
    {
        public int SuperTitleRaw;
        public int Feeder1TitleRaw;
        public int Feeder2TitleRaw;
        public int Feeder3TitleRaw;
        public int AppearanceRaw;
        public int SuperStageRaw;
        public int Feeder1StageRaw;
        public int Feeder2StageRaw;
        public int Feeder3StageRaw;
        public int MajorCupRaw;

        public void AddTitle(LeagueLevel level, int points)
        {
            switch (level)
            {
                case LeagueLevel.Superleague:
                    // Feeder-title path never passes Superleague (validated upstream);
                    // kept for exhaustiveness if legacy callers collapse levels.
                    SuperTitleRaw = checked(SuperTitleRaw + points);
                    break;
                case LeagueLevel.Feeder1:
                    Feeder1TitleRaw = checked(Feeder1TitleRaw + points);
                    break;
                case LeagueLevel.Feeder2:
                    Feeder2TitleRaw = checked(Feeder2TitleRaw + points);
                    break;
                case LeagueLevel.Feeder3:
                    Feeder3TitleRaw = checked(Feeder3TitleRaw + points);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}.");
            }
        }

        public void AddStage(LeagueLevel level, int points)
        {
            switch (level)
            {
                case LeagueLevel.Superleague:
                    SuperStageRaw = checked(SuperStageRaw + points);
                    break;
                case LeagueLevel.Feeder1:
                    Feeder1StageRaw = checked(Feeder1StageRaw + points);
                    break;
                case LeagueLevel.Feeder2:
                    Feeder2StageRaw = checked(Feeder2StageRaw + points);
                    break;
                case LeagueLevel.Feeder3:
                    Feeder3StageRaw = checked(Feeder3StageRaw + points);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}.");
            }
        }

        public PrestigeBreakdown ToBreakdown()
        {
            checked
            {
                int total = SuperTitleRaw + Feeder1TitleRaw + Feeder2TitleRaw + Feeder3TitleRaw
                    + AppearanceRaw
                    + SuperStageRaw + Feeder1StageRaw + Feeder2StageRaw + Feeder3StageRaw
                    + MajorCupRaw;
                return new PrestigeBreakdown(
                    SuperTitleRaw,
                    Feeder1TitleRaw,
                    Feeder2TitleRaw,
                    Feeder3TitleRaw,
                    AppearanceRaw,
                    SuperStageRaw,
                    Feeder1StageRaw,
                    Feeder2StageRaw,
                    Feeder3StageRaw,
                    MajorCupRaw,
                    total);
            }
        }
    }
}
