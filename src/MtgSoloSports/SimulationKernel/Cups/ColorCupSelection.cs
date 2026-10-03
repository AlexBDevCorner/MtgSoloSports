using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Cups;

/// <summary>
/// Pure deterministic Color Cup team selection (Game Rules §14, Technical Design §16).
/// Four normalized fixed-point components per sporting color: 35% effective bonus,
/// 30% completed-season performance, 25% recent form, 10% career prestige.
/// Recent form uses the athlete's most recent ten league stages with recency
/// weights 1..10 oldest-to-newest, newest-aligned (fewer than ten stages occupy
/// the newest weight slots; missing slots contribute zero).
/// Normalization within each color is (raw - min) * 1000 / (max - min); a tied
/// component (max == min) normalizes to 1000 for every candidate so it cannot
/// distort ranking. Final rating uses <see cref="SelectionScore.Combine"/> with
/// the snapshot permille weights. Ordering is fully deterministic with no RNG:
/// final desc, bonus norm desc, performance norm desc, form norm desc, prestige
/// norm desc, name ordinal ascending, athlete id ascending.
/// All arithmetic is checked integer-only; no double/float, clock or RNG.
/// </summary>
public static class ColorCupSelection
{
    public sealed record StageFormEntry(int SeasonNumber, int StageNumber, int ChampionshipPointsThousandths);

    public sealed record CandidateRaw(
        int AthleteId,
        string Name,
        int SportingColor,
        int BonusRawThousandths,
        int PerformanceRawThousandths,
        int FormRaw,
        int PrestigeRaw);

    public sealed record ScoredCandidate(
        int AthleteId,
        string Name,
        int SportingColor,
        int BonusRawThousandths,
        int PerformanceRawThousandths,
        int FormRaw,
        int PrestigeRaw,
        int BonusNormThousandths,
        int PerformanceNormThousandths,
        int FormNormThousandths,
        int PrestigeNormThousandths,
        int FinalRatingThousandths,
        int SelectionRank);

    /// <summary>
    /// Computes newest-aligned weighted recent form from league stage championship
    /// points. Entries need not be ordered; the newest ten by (season, stage) win.
    /// Missing slots contribute zero, so inactive athletes are penalized.
    /// </summary>
    public static int ComputeFormRaw(IEnumerable<StageFormEntry> stages, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(rules);
        List<StageFormEntry> ordered = OrderStages(stages, rules);
        List<StageFormEntry> recent = TakeRecent(ordered, rules);
        return WeightRecent(recent, rules);
    }

    internal static List<StageFormEntry> OrderStages(IEnumerable<StageFormEntry> stages, RulesV1 rules)
    {
        List<StageFormEntry> ordered = stages.ToList();
        foreach (StageFormEntry entry in ordered)
        {
            ValidateStageEntry(entry, rules);
        }

        ordered.Sort(static (left, right) =>
        {
            int season = left.SeasonNumber.CompareTo(right.SeasonNumber);
            return season != 0 ? season : left.StageNumber.CompareTo(right.StageNumber);
        });
        return ordered;
    }

    internal static void ValidateStageEntry(StageFormEntry entry, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(rules);
        if (entry.SeasonNumber < 1)
        {
            throw new InvalidOperationException($"Recent-form stage for athlete has corrupt season {entry.SeasonNumber}.");
        }

        if (entry.StageNumber < 1 || entry.StageNumber > rules.StagesPerSeason)
        {
            throw new InvalidOperationException($"Recent-form stage has corrupt stage {entry.StageNumber}.");
        }

        if (entry.ChampionshipPointsThousandths < 0)
        {
            throw new InvalidOperationException("Recent-form championship points cannot be negative.");
        }
    }

    internal static List<StageFormEntry> TakeRecent(List<StageFormEntry> ordered, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentNullException.ThrowIfNull(rules);
        if (ordered.Count <= rules.RecentFormStageCount)
        {
            return ordered;
        }

        return ordered.GetRange(ordered.Count - rules.RecentFormStageCount, rules.RecentFormStageCount);
    }

    internal static int WeightRecent(List<StageFormEntry> recent, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(recent);
        ArgumentNullException.ThrowIfNull(rules);
        if (recent.Count == 0)
        {
            return 0;
        }

        int offset = rules.RecentFormWeights.Count - recent.Count;
        long total = 0;
        checked
        {
            for (int i = 0; i < recent.Count; i++)
            {
                int weight = rules.RecentFormWeights[offset + i];
                total += (long)recent[i].ChampionshipPointsThousandths * weight;
            }

            return checked((int)total);
        }
    }

    /// <summary>
    /// Normalizes one component within a color to 0..1000. Tied inputs yield 1000.
    /// </summary>
    public static int Normalize(int raw, int min, int max)
    {
        if (raw < 0 || min < 0 || max < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(raw), "Selection raw values cannot be negative.");
        }

        if (min > max)
        {
            throw new InvalidOperationException($"Selection normalization has corrupt min {min} above max {max}.");
        }

        if (raw < min || raw > max)
        {
            throw new InvalidOperationException($"Selection raw {raw} is outside color range [{min}, {max}].");
        }

        if (max == min)
        {
            return 1000;
        }

        checked
        {
            return (int)(((long)(raw - min) * 1000L) / (max - min));
        }
    }

    /// <summary>
    /// Scores every candidate in one sporting color and returns the top four
    /// ordered #1..#4. Requires at least four candidates; duplicates abort.
    /// </summary>
    public static IReadOnlyList<ScoredCandidate> SelectTeam(IReadOnlyList<CandidateRaw> candidates, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rules);
        ValidateCandidates(candidates, rules);
        ColorRange range = ComputeRange(candidates);
        List<ScoredCandidate> scored = ScoreAll(candidates, range, rules);
        return TakeTopFour(scored);
    }

    /// <summary>
    /// Scores every candidate in one sporting color and returns the whole field
    /// ordered #1..#N by the selection ordering. The first four entries are
    /// exactly the team <see cref="SelectTeam"/> returns; the rest explain who
    /// missed the cut and by how much.
    /// </summary>
    public static IReadOnlyList<ScoredCandidate> RankAll(IReadOnlyList<CandidateRaw> candidates, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rules);
        ValidateCandidates(candidates, rules);
        ColorRange range = ComputeRange(candidates);
        List<ScoredCandidate> scored = ScoreAll(candidates, range, rules);
        List<ScoredCandidate> ranked = new(scored.Count);
        for (int i = 0; i < scored.Count; i++)
        {
            ranked.Add(scored[i] with { SelectionRank = i + 1 });
        }

        return ranked;
    }

    internal sealed record ColorRange(int BonusMin, int BonusMax, int PerformanceMin, int PerformanceMax, int FormMin, int FormMax, int PrestigeMin, int PrestigeMax);

    internal static void ValidateCandidates(IReadOnlyList<CandidateRaw> candidates, RulesV1 rules)
    {
        if (candidates.Count < rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup selection needs at least {rules.ColorCupTeamSize} candidates, was {candidates.Count}.");
        }

        HashSet<int> ids = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        int color = candidates[0].SportingColor;
        foreach (CandidateRaw candidate in candidates)
        {
            ValidateSingleCandidate(candidate, color, ids, names);
        }
    }

    internal static void ValidateSingleCandidate(CandidateRaw candidate, int color, HashSet<int> ids, HashSet<string> names)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(names);
        if (candidate.AthleteId <= 0)
        {
            throw new InvalidOperationException($"Color Cup candidate has corrupt athlete {candidate.AthleteId}.");
        }

        if (string.IsNullOrWhiteSpace(candidate.Name))
        {
            throw new InvalidOperationException($"Color Cup candidate {candidate.AthleteId} has an empty name.");
        }

        if (candidate.SportingColor != color)
        {
            throw new InvalidOperationException($"Color Cup selection mixes sporting colors {color} and {candidate.SportingColor}.");
        }

        if (candidate.BonusRawThousandths < 0 || candidate.PerformanceRawThousandths < 0 || candidate.FormRaw < 0 || candidate.PrestigeRaw < 0)
        {
            throw new InvalidOperationException($"Color Cup candidate '{candidate.Name}' has corrupt negative selection inputs.");
        }

        if (!ids.Add(candidate.AthleteId))
        {
            throw new InvalidOperationException($"Color Cup selection contains duplicate athlete {candidate.AthleteId}.");
        }

        if (!names.Add(candidate.Name))
        {
            throw new InvalidOperationException($"Color Cup selection contains duplicate athlete name '{candidate.Name}'.");
        }
    }

    internal static ColorRange ComputeRange(IReadOnlyList<CandidateRaw> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        int bonusMin = candidates.Min(c => c.BonusRawThousandths);
        int bonusMax = candidates.Max(c => c.BonusRawThousandths);
        int perfMin = candidates.Min(c => c.PerformanceRawThousandths);
        int perfMax = candidates.Max(c => c.PerformanceRawThousandths);
        int formMin = candidates.Min(c => c.FormRaw);
        int formMax = candidates.Max(c => c.FormRaw);
        int prestigeMin = candidates.Min(c => c.PrestigeRaw);
        int prestigeMax = candidates.Max(c => c.PrestigeRaw);
        return new ColorRange(bonusMin, bonusMax, perfMin, perfMax, formMin, formMax, prestigeMin, prestigeMax);
    }

    internal static List<ScoredCandidate> ScoreAll(IReadOnlyList<CandidateRaw> candidates, ColorRange range, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(rules);
        List<ScoredCandidate> scored = new(candidates.Count);
        foreach (CandidateRaw candidate in candidates)
        {
            scored.Add(ScoreSingle(candidate, range, rules));
        }

        scored.Sort(CompareScored);
        return scored;
    }

    internal static ScoredCandidate ScoreSingle(CandidateRaw candidate, ColorRange range, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(rules);
        int bonusNorm = Normalize(candidate.BonusRawThousandths, range.BonusMin, range.BonusMax);
        int perfNorm = Normalize(candidate.PerformanceRawThousandths, range.PerformanceMin, range.PerformanceMax);
        int formNorm = Normalize(candidate.FormRaw, range.FormMin, range.FormMax);
        int prestigeNorm = Normalize(candidate.PrestigeRaw, range.PrestigeMin, range.PrestigeMax);
        SelectionScore final = SelectionScore.Combine(
            bonusNorm, perfNorm, formNorm, prestigeNorm,
            rules.CupBonusWeightPermille, rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille, rules.CupPrestigeWeightPermille);
        return new ScoredCandidate(
            candidate.AthleteId, candidate.Name, candidate.SportingColor,
            candidate.BonusRawThousandths, candidate.PerformanceRawThousandths, candidate.FormRaw, candidate.PrestigeRaw,
            bonusNorm, perfNorm, formNorm, prestigeNorm, final.Thousandths, SelectionRank: 0);
    }

    internal static int CompareScored(ScoredCandidate left, ScoredCandidate right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        int final = right.FinalRatingThousandths.CompareTo(left.FinalRatingThousandths);
        if (final != 0)
        {
            return final;
        }

        int bonus = right.BonusNormThousandths.CompareTo(left.BonusNormThousandths);
        if (bonus != 0)
        {
            return bonus;
        }

        int perf = right.PerformanceNormThousandths.CompareTo(left.PerformanceNormThousandths);
        if (perf != 0)
        {
            return perf;
        }

        int form = right.FormNormThousandths.CompareTo(left.FormNormThousandths);
        if (form != 0)
        {
            return form;
        }

        int prestige = right.PrestigeNormThousandths.CompareTo(left.PrestigeNormThousandths);
        if (prestige != 0)
        {
            return prestige;
        }

        int name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        if (name != 0)
        {
            return name;
        }

        return left.AthleteId.CompareTo(right.AthleteId);
    }

    internal static IReadOnlyList<ScoredCandidate> TakeTopFour(List<ScoredCandidate> scored)
    {
        ArgumentNullException.ThrowIfNull(scored);
        if (scored.Count < 4)
        {
            throw new InvalidOperationException($"Color Cup selection needs at least 4 scored candidates, was {scored.Count}.");
        }

        List<ScoredCandidate> team = new(4);
        for (int i = 0; i < 4; i++)
        {
            ScoredCandidate entry = scored[i];
            team.Add(entry with { SelectionRank = i + 1 });
        }

        return team;
    }
}
