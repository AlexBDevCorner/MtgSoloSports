using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Pure deterministic tier-cascade feeder-rebalancing selection (MSS-059).
/// After all automatic movement and all qualifiers are resolved, each sporting
/// color is repaired independently through F1 → F2 → F3 → Pool:
///
/// Shortage: F1 below 32 pulls the best eligible retained F2 upward; the
/// resulting F2 vacancy pulls the best eligible retained F3 upward; the
/// resulting F3 vacancy draws equal-probability from the same-color pool.
///
/// Overflow: F1 above 32 pushes the worst eligible retained F1 down to F2;
/// any F2 overflow pushes its worst eligible retained athlete to F3; only F3
/// overflow displaces to the common pool.
///
/// Pool only ever connects directly to F3 (Pool→F3 draws, F3→Pool
/// displacements). Upward structural refill chooses the best eligible retained
/// source-season athletes deterministically (lowest source rank first);
/// downward overflow chooses the worst (highest source rank first). Existing
/// deterministic tie conventions apply (name ordinal, then athlete id).
/// Retained athletes (same source/destination division) are used before
/// protected athletes (just changed tiers via automatic/qualifier/Superleague
/// movement); protected are used only when no retained alternative exists, and
/// the plan fails loudly when even the combined pool cannot repair a corrupt
/// roster. Only pool draws consume RNG (same-color candidates, canonical color
/// order, name-sorted shuffle prefix). Each athlete moves at most once
/// structurally per cascade (overflow arrivals are excluded from further
/// downstream selection). Former league athletes may return with no cooldown;
/// bonus aging is handled by the caller, never as selection weighting.
/// </summary>
public static class RebalanceFeedersSelection
{
    public sealed record PoolCandidate(int SaveAthleteId, string Name, SportingColor SportingColor);

    public sealed record RetainedCandidate(int SaveAthleteId, string Name, int SourceRank);

    public sealed record ProvisionalMember(int SaveAthleteId, string Name, bool IsRetained, int SourceRank);

    /// <summary>
    /// One athlete provisionally occupying a feeder division after all
    /// competitive movement/qualifiers. <c>IsProtected</c> marks athletes who
    /// just changed tiers through automatic movement, qualifier outcome,
    /// inaugural creation or Superleague return and must not be immediately
    /// reversed when retained alternatives exist. <c>SourceRank</c> is the
    /// previous/source-season sporting rank (1..32) used for deterministic
    /// best/worst ordering.
    /// </summary>
    public sealed record TierMember(int SaveAthleteId, string Name, int SourceRank, bool IsProtected);

    /// <summary>
    /// Per-color cascade input: provisional F1/F2/F3 rosters plus same-color pool.
    /// </summary>
    public sealed record TierColorInput(
        SportingColor Color,
        IReadOnlyList<TierMember> F1,
        IReadOnlyList<TierMember> F2,
        IReadOnlyList<TierMember> F3,
        IReadOnlyList<PoolCandidate> Pool);

    /// <summary>
    /// Legacy single-feeder input (F1 only). Retained for unit-test
    /// compatibility; the handler always uses <see cref="TierColorInput"/>.
    /// </summary>
    public sealed record ColorInput(
        SportingColor Color,
        IReadOnlyList<ProvisionalMember> Provisional,
        IReadOnlyList<PoolCandidate> Pool);

    /// <summary>
    /// One structural move between adjacent feeder divisions.
    /// </summary>
    public sealed record StructuralMove(
        int SaveAthleteId,
        string Name,
        FeederDivision FromDivision,
        FeederDivision ToDivision,
        int SourceRank,
        SportingColor Color);

    public sealed record ColorPlan(
        SportingColor Color,
        int ProvisionalCount,
        IReadOnlyList<PoolCandidate> Draws,
        IReadOnlyList<RetainedCandidate> Displaced)
    {
        public int F1ProvisionalCount { get; init; } = ProvisionalCount;

        public int F2ProvisionalCount { get; init; } = 32;

        public int F3ProvisionalCount { get; init; } = 32;

        public IReadOnlyList<StructuralMove> UpMoves { get; init; } = [];

        public IReadOnlyList<StructuralMove> DownMoves { get; init; } = [];
    }

    /// <summary>
    /// Per-color tier cascade outcome. Provisional counts are post-competitive,
    /// pre-structural sizes; final sizes are always 32 after applying
    /// UpMoves/DownMoves/Draws/Displaced.
    /// </summary>
    public sealed record TierColorPlan(
        SportingColor Color,
        int F1ProvisionalCount,
        int F2ProvisionalCount,
        int F3ProvisionalCount,
        IReadOnlyList<StructuralMove> UpMoves,
        IReadOnlyList<StructuralMove> DownMoves,
        IReadOnlyList<PoolCandidate> Draws,
        IReadOnlyList<RetainedCandidate> Displaced);

    public sealed record RebalancePlan(
        IReadOnlyList<ColorPlan> PerColor,
        IReadOnlyList<PoolCandidate> AllDraws,
        IReadOnlyList<RetainedCandidate> AllDisplaced)
    {
        public IReadOnlyList<TierColorPlan> TierPerColor { get; init; } = [];

        public IReadOnlyList<StructuralMove> AllUpMoves { get; init; } = [];

        public IReadOnlyList<StructuralMove> AllDownMoves { get; init; } = [];
    }

    /// <summary>
    /// Selects the tier cascade for all colors, advancing <paramref name="rng"/>
    /// once per color that needs pool draws (colors in enum order). The caller
    /// persists <c>rng.Snapshot()</c> in the same transaction as memberships
    /// and movements.
    /// </summary>
    public static RebalancePlan Select(
        IReadOnlyList<TierColorInput> inputs,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        if (inputs.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Rebalancing requires exactly {rules.RegularLeagueCount} color inputs, was {inputs.Count}.");
        }

        List<TierColorPlan> tierPlans = new(inputs.Count);
        foreach (TierColorInput input in inputs.OrderBy(i => i.Color))
        {
            tierPlans.Add(SelectSingleTier(input, rng, rules));
        }

        List<PoolCandidate> draws = tierPlans.SelectMany(p => p.Draws).ToList();
        List<RetainedCandidate> displaced = tierPlans.SelectMany(p => p.Displaced).ToList();
        List<StructuralMove> up = tierPlans.SelectMany(p => p.UpMoves).ToList();
        List<StructuralMove> down = tierPlans.SelectMany(p => p.DownMoves).ToList();
        EnsureTierGlobalUniqueness(tierPlans, up, down, draws, displaced);

        List<ColorPlan> legacy = tierPlans.Select(ToLegacyPlan).ToList();
        return new RebalancePlan(legacy, draws, displaced)
        {
            TierPerColor = tierPlans,
            AllUpMoves = up,
            AllDownMoves = down,
        };
    }

    /// <summary>
    /// Legacy single-feeder selection (pre-tier). Retained so existing unit
    /// coverage keeps compiling; new code must use the tier overload.
    /// </summary>
    public static RebalancePlan Select(
        IReadOnlyList<ColorInput> inputs,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        if (inputs.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Rebalancing requires exactly {rules.RegularLeagueCount} color inputs, was {inputs.Count}.");
        }

        // Adapt legacy F1-only inputs into tier inputs with balanced F2/F3 so
        // the cascade reduces to the historical F1↔pool behaviour. F2/F3 carry
        // synthetic retained members that are never touched when F1 is already
        // the only imbalance; tests exercising pure F1 under/overflow keep
        // their historical expectations.
        List<TierColorInput> tierInputs = new(inputs.Count);
        foreach (ColorInput input in inputs)
        {
            List<TierMember> f1 = input.Provisional
                .Select(m => new TierMember(m.SaveAthleteId, m.Name, m.SourceRank, !m.IsRetained))
                .ToList();
            List<TierMember> f2 = Enumerable.Range(1, rules.LeagueSize)
                .Select(rank => new TierMember(
                    -(900000 + ((int)input.Color * 1000) + rank),
                    $"Tier Placeholder {input.Color} F2 {rank:D2}",
                    rank,
                    false))
                .ToList();
            List<TierMember> f3 = Enumerable.Range(1, rules.LeagueSize)
                .Select(rank => new TierMember(
                    -(800000 + ((int)input.Color * 1000) + rank),
                    $"Tier Placeholder {input.Color} F3 {rank:D2}",
                    rank,
                    false))
                .ToList();
            tierInputs.Add(new TierColorInput(input.Color, f1, f2, f3, input.Pool));
        }

        // When every F2/F3 is a synthetic placeholder, bypass structural moves
        // and apply the historical direct F1↔pool repair so legacy selection
        // tests (overflow displaces lowest retained, underflow draws) keep
        // passing without fabricating cross-tier athlete ids.
        if (tierInputs.All(t => t.F2.All(m => m.SaveAthleteId < 0) && t.F3.All(m => m.SaveAthleteId < 0)))
        {
            List<ColorPlan> plans = new(inputs.Count);
            foreach (ColorInput input in inputs.OrderBy(i => i.Color))
            {
                plans.Add(SelectSingle(input, rng, rules));
            }

            List<PoolCandidate> draws = plans.SelectMany(p => p.Draws).ToList();
            List<RetainedCandidate> displaced = plans.SelectMany(p => p.Displaced).ToList();
            EnsureGlobalUniqueness(plans, draws, displaced);
            return new RebalancePlan(plans, draws, displaced);
        }

        return Select(tierInputs, rng, rules);
    }

    internal static TierColorPlan SelectSingleTier(TierColorInput input, Pcg32V1 rng, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);

        ValidateTierInput(input, rules);

        List<TierMember> f1 = [.. input.F1];
        List<TierMember> f2 = [.. input.F2];
        List<TierMember> f3 = [.. input.F3];
        int f1Provisional = f1.Count;
        int f2Provisional = f2.Count;
        int f3Provisional = f3.Count;

        List<StructuralMove> up = [];
        List<StructuralMove> down = [];
        HashSet<int> structurallyMoved = new();
        CascadeF1(input, f1, f2, up, down, structurallyMoved, rules);
        CascadeF2(input, f2, f3, up, down, structurallyMoved, rules);
        (List<PoolCandidate> draws, List<RetainedCandidate> displaced) =
            CascadeF3Pool(input, f3, up, down, structurallyMoved, rng, rules);

        if (f1.Count != rules.LeagueSize || f2.Count != rules.LeagueSize || f3.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"League {input.Color} cascade failed to restore 32/32/32, was {f1.Count}/{f2.Count}/{f3.Count}.");
        }

        return new TierColorPlan(
            input.Color, f1Provisional, f2Provisional, f3Provisional,
            up, down, draws, displaced);
    }

    internal static void CascadeF1(
        TierColorInput input,
        List<TierMember> f1,
        List<TierMember> f2,
        List<StructuralMove> up,
        List<StructuralMove> down,
        HashSet<int> moved,
        RulesV1 rules)
    {
        if (f1.Count < rules.LeagueSize)
        {
            int need = rules.LeagueSize - f1.Count;
            foreach (TierMember member in PickUpCandidates(f2, need, input.Color, rules, moved))
            {
                MoveUp(member, f2, f1, up, moved, FeederDivision.Second, FeederDivision.First, input.Color);
            }

            return;
        }

        if (f1.Count > rules.LeagueSize)
        {
            int excess = f1.Count - rules.LeagueSize;
            foreach (TierMember member in PickDownCandidates(f1, excess, input.Color, rules, moved))
            {
                MoveDown(member, f1, f2, down, moved, FeederDivision.First, FeederDivision.Second, input.Color);
            }
        }
    }

    internal static void CascadeF2(
        TierColorInput input,
        List<TierMember> f2,
        List<TierMember> f3,
        List<StructuralMove> up,
        List<StructuralMove> down,
        HashSet<int> moved,
        RulesV1 rules)
    {
        if (f2.Count < rules.LeagueSize)
        {
            int need = rules.LeagueSize - f2.Count;
            foreach (TierMember member in PickUpCandidates(f3, need, input.Color, rules, moved))
            {
                MoveUp(member, f3, f2, up, moved, FeederDivision.Third, FeederDivision.Second, input.Color);
            }

            return;
        }

        if (f2.Count > rules.LeagueSize)
        {
            int excess = f2.Count - rules.LeagueSize;
            foreach (TierMember member in PickDownCandidates(f2, excess, input.Color, rules, moved, excludeMoved: true))
            {
                MoveDown(member, f2, f3, down, moved, FeederDivision.Second, FeederDivision.Third, input.Color);
            }
        }
    }

    internal static (List<PoolCandidate> Draws, List<RetainedCandidate> Displaced) CascadeF3Pool(
        TierColorInput input,
        List<TierMember> f3,
        List<StructuralMove> up,
        List<StructuralMove> down,
        HashSet<int> moved,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        _ = up;
        _ = down;
        if (f3.Count < rules.LeagueSize)
        {
            int need = rules.LeagueSize - f3.Count;
            List<PoolCandidate> draws = DrawShortfallTier(input, need, rng, rules);
            foreach (PoolCandidate draw in draws)
            {
                f3.Add(new TierMember(draw.SaveAthleteId, draw.Name, 0, false));
            }

            return (draws, []);
        }

        if (f3.Count > rules.LeagueSize)
        {
            int excess = f3.Count - rules.LeagueSize;
            List<RetainedCandidate> displaced = [];
            foreach (TierMember member in PickDownCandidates(f3, excess, input.Color, rules, moved, excludeMoved: true))
            {
                f3.RemoveAll(m => m.SaveAthleteId == member.SaveAthleteId);
                displaced.Add(new RetainedCandidate(member.SaveAthleteId, member.Name, member.SourceRank));
                moved.Add(member.SaveAthleteId);
            }

            return ([], displaced);
        }

        return ([], []);
    }

    internal static void MoveUp(
        TierMember member,
        List<TierMember> from,
        List<TierMember> to,
        List<StructuralMove> up,
        HashSet<int> moved,
        FeederDivision fromDivision,
        FeederDivision toDivision,
        SportingColor color)
    {
        from.RemoveAll(m => m.SaveAthleteId == member.SaveAthleteId);
        to.Add(member);
        up.Add(new StructuralMove(
            member.SaveAthleteId, member.Name, fromDivision, toDivision, member.SourceRank, color));
        moved.Add(member.SaveAthleteId);
    }

    internal static void MoveDown(
        TierMember member,
        List<TierMember> from,
        List<TierMember> to,
        List<StructuralMove> down,
        HashSet<int> moved,
        FeederDivision fromDivision,
        FeederDivision toDivision,
        SportingColor color)
    {
        from.RemoveAll(m => m.SaveAthleteId == member.SaveAthleteId);
        to.Add(member);
        down.Add(new StructuralMove(
            member.SaveAthleteId, member.Name, fromDivision, toDivision, member.SourceRank, color));
        moved.Add(member.SaveAthleteId);
    }

    internal static List<TierMember> PickUpCandidates(
        List<TierMember> source,
        int need,
        SportingColor color,
        RulesV1 rules,
        HashSet<int> exclude)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(exclude);
        if (need <= 0)
        {
            throw new InvalidOperationException($"Structural up-move need must be positive, was {need}.");
        }

        List<TierMember> eligible = source.Where(m => !exclude.Contains(m.SaveAthleteId)).ToList();
        foreach (TierMember member in eligible)
        {
            ValidateTierMember(member, color, rules);
        }

        // Best retained first (lowest source rank), then best protected.
        List<TierMember> ordered = eligible
            .OrderBy(m => m.IsProtected)
            .ThenBy(m => m.SourceRank)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.SaveAthleteId)
            .ToList();
        if (ordered.Count < need)
        {
            throw new InvalidOperationException(
                $"League {color} needs {need} upward structural athletes but its source division holds only {ordered.Count}.");
        }

        return ordered.Take(need).ToList();
    }

    internal static List<TierMember> PickDownCandidates(
        List<TierMember> source,
        int need,
        SportingColor color,
        RulesV1 rules,
        HashSet<int> exclude,
        bool excludeMoved = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(exclude);
        if (need <= 0)
        {
            throw new InvalidOperationException($"Structural down-move need must be positive, was {need}.");
        }

        List<TierMember> eligible = excludeMoved
            ? source.Where(m => !exclude.Contains(m.SaveAthleteId)).ToList()
            : source.ToList();
        foreach (TierMember member in eligible)
        {
            ValidateTierMember(member, color, rules);
        }

        // Worst retained first (highest source rank), then worst protected.
        // Each athlete moves at most once structurally: overflow arrivals from
        // the immediate upstream division are excluded via excludeMoved.
        List<TierMember> ordered = eligible
            .OrderBy(m => m.IsProtected)
            .ThenByDescending(m => m.SourceRank)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.SaveAthleteId)
            .ToList();
        if (ordered.Count < need)
        {
            throw new InvalidOperationException(
                $"League {color} must move {need} athletes downward but holds only {ordered.Count} eligible retained athletes; returning/protected athletes are never displaced before retained alternatives and corrupt rosters fail loudly.");
        }

        return ordered.Take(need).ToList();
    }

    internal static void ValidateTierInput(TierColorInput input, RulesV1 rules)
    {
        HashSet<int> seen = new();
        foreach (TierMember member in input.F1.Concat(input.F2).Concat(input.F3))
        {
            if (!seen.Add(member.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Rebalancing for {input.Color} contains duplicate athlete id {member.SaveAthleteId} across feeder divisions.");
            }

            ValidateTierMember(member, input.Color, rules);
        }

        HashSet<int> poolIds = new();
        foreach (PoolCandidate candidate in input.Pool)
        {
            if (candidate.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Pool contains invalid athlete id {candidate.SaveAthleteId}.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                throw new InvalidOperationException($"Pool athlete {candidate.SaveAthleteId} has an empty name.");
            }

            if (candidate.SportingColor != input.Color)
            {
                throw new InvalidOperationException(
                    $"Pool athlete {candidate.SaveAthleteId} color {candidate.SportingColor} does not match league {input.Color}.");
            }

            if (!poolIds.Add(candidate.SaveAthleteId))
            {
                throw new InvalidOperationException($"Pool for {input.Color} contains duplicate athlete id {candidate.SaveAthleteId}.");
            }

            if (seen.Contains(candidate.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Athlete {candidate.SaveAthleteId} appears in both the {input.Color} feeder pyramid and its common pool.");
            }
        }
    }

    internal static void ValidateTierMember(TierMember member, SportingColor color, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (member.SaveAthleteId == 0)
        {
            throw new InvalidOperationException($"League {color} contains invalid athlete id 0.");
        }

        if (string.IsNullOrWhiteSpace(member.Name))
        {
            throw new InvalidOperationException($"Athlete {member.SaveAthleteId} has an empty name.");
        }

        if (member.SourceRank < 1 || member.SourceRank > rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Athlete {member.SaveAthleteId} has corrupt source rank {member.SourceRank}.");
        }
    }

    internal static List<PoolCandidate> DrawShortfallTier(
        TierColorInput input, int need, Pcg32V1 rng, RulesV1 rules)
    {
        if (need <= 0)
        {
            throw new InvalidOperationException($"Draw shortfall must be positive, was {need}.");
        }

        List<PoolCandidate> ordered = input.Pool
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.SaveAthleteId)
            .ToList();
        if (ordered.Count < need)
        {
            throw new InvalidOperationException(
                $"League {input.Color} F3 needs {need} pool draws but its common pool holds only {ordered.Count}.");
        }

        List<PoolCandidate> shuffled = [.. ordered];
        DeterministicShuffle.Shuffle(shuffled, rng);
        return shuffled.Take(need).ToList();
    }

    internal static ColorPlan ToLegacyPlan(TierColorPlan tier)
    {
        int provisional = tier.F1ProvisionalCount;
        return new ColorPlan(tier.Color, provisional, tier.Draws, tier.Displaced)
        {
            F1ProvisionalCount = tier.F1ProvisionalCount,
            F2ProvisionalCount = tier.F2ProvisionalCount,
            F3ProvisionalCount = tier.F3ProvisionalCount,
            UpMoves = tier.UpMoves,
            DownMoves = tier.DownMoves,
        };
    }

    internal static ColorPlan SelectSingle(ColorInput input, Pcg32V1 rng, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);

        if (input.Provisional.Count > rules.LeagueSize)
        {
            return DisplaceOverflow(input, rules);
        }

        if (input.Provisional.Count < rules.LeagueSize)
        {
            return DrawShortfall(input, rng, rules);
        }

        return new ColorPlan(input.Color, input.Provisional.Count, [], []);
    }

    internal static ColorPlan DisplaceOverflow(ColorInput input, RulesV1 rules)
    {
        int excess = input.Provisional.Count - rules.LeagueSize;
        List<RetainedCandidate> retained = input.Provisional
            .Where(m => m.IsRetained)
            .Select(m => new RetainedCandidate(m.SaveAthleteId, m.Name, m.SourceRank))
            .OrderByDescending(c => c.SourceRank)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.SaveAthleteId)
            .ToList();

        if (retained.Count < excess)
        {
            throw new InvalidOperationException(
                $"League {input.Color} overflows by {excess} but holds only {retained.Count} displaceable retained athletes; returning athletes are protected.");
        }

        foreach (RetainedCandidate candidate in retained)
        {
            if (candidate.SourceRank < 1 || candidate.SourceRank > rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"Retained athlete {candidate.SaveAthleteId} has corrupt source rank {candidate.SourceRank}.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                throw new InvalidOperationException(
                    $"Retained athlete {candidate.SaveAthleteId} has an empty name.");
            }
        }

        List<RetainedCandidate> displaced = retained.Take(excess).ToList();
        return new ColorPlan(input.Color, input.Provisional.Count, [], displaced);
    }

    internal static ColorPlan DrawShortfall(ColorInput input, Pcg32V1 rng, RulesV1 rules)
    {
        int need = rules.LeagueSize - input.Provisional.Count;
        if (need <= 0)
        {
            throw new InvalidOperationException($"Draw shortfall must be positive, was {need}.");
        }

        List<PoolCandidate> ordered = input.Pool
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.SaveAthleteId)
            .ToList();

        if (ordered.Count < need)
        {
            throw new InvalidOperationException(
                $"League {input.Color} needs {need} pool draws but its common pool holds only {ordered.Count}.");
        }

        foreach (PoolCandidate candidate in ordered)
        {
            if (candidate.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Pool contains invalid athlete id {candidate.SaveAthleteId}.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                throw new InvalidOperationException($"Pool athlete {candidate.SaveAthleteId} has an empty name.");
            }

            if (candidate.SportingColor != input.Color)
            {
                throw new InvalidOperationException(
                    $"Pool athlete {candidate.SaveAthleteId} color {candidate.SportingColor} does not match league {input.Color}.");
            }
        }

        List<PoolCandidate> shuffled = [.. ordered];
        DeterministicShuffle.Shuffle(shuffled, rng);
        List<PoolCandidate> draws = shuffled.Take(need).ToList();
        return new ColorPlan(input.Color, input.Provisional.Count, draws, []);
    }

    private static void EnsureGlobalUniqueness(
        List<ColorPlan> plans,
        List<PoolCandidate> draws,
        List<RetainedCandidate> displaced)
    {
        HashSet<int> seen = new();
        foreach (PoolCandidate draw in draws)
        {
            if (!seen.Add(draw.SaveAthleteId))
            {
                throw new InvalidOperationException($"Rebalancing draws duplicate athlete id {draw.SaveAthleteId}.");
            }
        }

        foreach (RetainedCandidate candidate in displaced)
        {
            if (!seen.Add(candidate.SaveAthleteId))
            {
                throw new InvalidOperationException($"Rebalancing displaces duplicate athlete id {candidate.SaveAthleteId}.");
            }
        }

        foreach (ColorPlan plan in plans)
        {
            if (plan.Draws.Count > 0 && plan.Displaced.Count > 0)
            {
                throw new InvalidOperationException($"League {plan.Color} cannot both draw and displace.");
            }
        }
    }

    private static void EnsureTierGlobalUniqueness(
        List<TierColorPlan> plans,
        List<StructuralMove> up,
        List<StructuralMove> down,
        List<PoolCandidate> draws,
        List<RetainedCandidate> displaced)
    {
        HashSet<int> structural = CheckStructuralUniqueness(up, down);
        CheckPoolBoundaryUniqueness(structural, draws, displaced);

        foreach (TierColorPlan plan in plans)
        {
            if (plan.Draws.Count > 0 && plan.Displaced.Count > 0)
            {
                throw new InvalidOperationException($"League {plan.Color} F3 cannot both draw and displace.");
            }
        }
    }

    private static HashSet<int> CheckStructuralUniqueness(List<StructuralMove> up, List<StructuralMove> down)
    {
        HashSet<int> structural = new();
        foreach (StructuralMove move in up.Concat(down))
        {
            if (!structural.Add(move.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Structural cascade moves duplicate athlete id {move.SaveAthleteId}; each athlete moves at most once.");
            }

            CheckStructuralAdjacency(move);
        }

        return structural;
    }

    private static void CheckStructuralAdjacency(StructuralMove move)
    {
        bool isUp = move.ToDivision < move.FromDivision;
        bool isDown = move.ToDivision > move.FromDivision;
        if (!isUp && !isDown)
        {
            throw new InvalidOperationException(
                $"Structural move {move.SaveAthleteId} must be between adjacent feeder divisions.");
        }

        if (Math.Abs((int)move.ToDivision - (int)move.FromDivision) != 1)
        {
            throw new InvalidOperationException(
                $"Structural move {move.SaveAthleteId} must be between adjacent tiers, was {move.FromDivision} → {move.ToDivision}.");
        }

        if (move.FromDivision == FeederDivision.None || move.ToDivision == FeederDivision.None)
        {
            throw new InvalidOperationException(
                $"Structural move {move.SaveAthleteId} must never involve the Superleague or pool directly.");
        }
    }

    private static void CheckPoolBoundaryUniqueness(
        HashSet<int> structural,
        List<PoolCandidate> draws,
        List<RetainedCandidate> displaced)
    {
        HashSet<int> poolBoundary = new();
        foreach (PoolCandidate draw in draws)
        {
            if (!poolBoundary.Add(draw.SaveAthleteId))
            {
                throw new InvalidOperationException($"Rebalancing draws duplicate athlete id {draw.SaveAthleteId}.");
            }

            if (structural.Contains(draw.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Athlete {draw.SaveAthleteId} appears in both structural moves and pool draws.");
            }
        }

        foreach (RetainedCandidate candidate in displaced)
        {
            if (!poolBoundary.Add(candidate.SaveAthleteId))
            {
                throw new InvalidOperationException($"Rebalancing displaces duplicate athlete id {candidate.SaveAthleteId}.");
            }

            if (structural.Contains(candidate.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Athlete {candidate.SaveAthleteId} appears in both structural moves and pool displacement.");
            }
        }
    }
}
