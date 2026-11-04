using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Careers;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// MSS-118 opt-in Chaos Cup calibration diagnostic.
/// Simulates a configurable multi-season tiered save through the same
/// <c>SimulateSeasonsHandler</c> lifecycle as manual play (Chaos runs each
/// eligible season) and aggregates persisted Chaos editions by source tier/Pool
/// and sporting color plus the attributable effect of the 5% Chaos form
/// component on Color/Type team selection. Read-only aggregation: Chaos rows
/// and selection reports are decoded, never recomputed and never rewritten;
/// no sporting state is mutated and production behavior is untouched when
/// disabled. Approved numbers are recorded, never auto-tuned: outer weights
/// 35/30/20/5/10, Chaos career weight 450 and the six stage sizes
/// 32/16/8/4/2/1.
/// </summary>
public static class ChaosCalibrationHarness
{
    public sealed record Options(
        int Seasons,
        ulong Seed,
        ulong Stream,
        string SaveName);

    /// <summary>Tier keys in competitive order: Super, F1, F2, F3, Pool.</summary>
    public static readonly IReadOnlyList<string> TierOrder = ["Super", "F1", "F2", "F3", "Pool"];

    public sealed record TierCounts(
        string Tier,
        int Entrants,
        int Finalists,
        int Medals);

    public sealed record ColorCounts(
        string Color,
        int Entrants,
        int Finalists,
        int Medals);

    public sealed record CareerReference(
        int ChaosChampion,
        int ChaosFinalLast,
        int ChaosBestExit,
        int SuperleagueChampion,
        int ColorIndividualChampion,
        int Feeder1Champion,
        int FiveSuperP6Total);

    public sealed record SelectionFlip(
        int SourceSeasonNumber,
        string Cup,
        string TeamName,
        string SelectedName,
        string MissedName,
        int Gap,
        int ChaosDelta);

    public sealed record SelectionImpact(
        int ColorTeams,
        int ColorFlippedTeams,
        int TypeTeams,
        int TypeFlippedTeams,
        IReadOnlyList<SelectionFlip> Examples);

    public sealed record EditionSummary(
        int SourceSeasonNumber,
        int RulesVersion,
        int ChaosFormatVersion,
        int EntrantCount,
        IReadOnlyList<int> StageGroupCounts,
        IReadOnlyList<TierCounts> ByTier,
        IReadOnlyList<ColorCounts> ByColor,
        int FinalistCount,
        int MedalCount);

    public sealed record Report(
        Guid SaveId,
        int SeasonsRequested,
        int SeasonsCompleted,
        ulong Seed,
        ulong Stream,
        int RulesVersion,
        int ChaosFormatVersion,
        int BonusWeightPermille,
        int PerformanceWeightPermille,
        int FormWeightPermille,
        int ChaosWeightPermille,
        int PrestigeWeightPermille,
        int ChaosCupWeight,
        IReadOnlyList<EditionSummary> Editions,
        IReadOnlyList<TierCounts> AggregateByTier,
        IReadOnlyList<ColorCounts> AggregateByColor,
        CareerReference Careers,
        SelectionImpact Selection,
        string Checksum);

    public static Options ReadFromEnvironmentOrDefault(int defaultSeasons, ulong seed, ulong stream)
    {
        int seasons = defaultSeasons;
        string? raw = Environment.GetEnvironmentVariable("MTG_CHAOS_CALIBRATION_SEASONS");
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= 1 && parsed <= 500)
        {
            seasons = parsed;
        }

        string? seedRaw = Environment.GetEnvironmentVariable("MTG_CHAOS_CALIBRATION_SEED");
        if (ulong.TryParse(seedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsedSeed))
        {
            seed = parsedSeed;
        }

        string? streamRaw = Environment.GetEnvironmentVariable("MTG_CHAOS_CALIBRATION_STREAM");
        if (ulong.TryParse(streamRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsedStream))
        {
            stream = parsedStream;
        }

        return new Options(seasons, seed, stream, $"ChaosCalibration {seasons}");
    }

    public static string TierForEntrant(ChaosCupEntrantEntity entrant)
    {
        ArgumentNullException.ThrowIfNull(entrant);
        if (entrant.IsPoolInvite)
        {
            return "Pool";
        }

        if (entrant.SourceLeagueKind == (int)LeagueKind.Superleague)
        {
            return "Super";
        }

        return (FeederDivision)entrant.SourceFeederDivision switch
        {
            FeederDivision.Second => "F2",
            FeederDivision.Third => "F3",
            _ => "F1",
        };
    }

    public static async Task<Report> RunAsync(
        SaveStore store,
        IReadOnlyList<MtgSoloSports.Features.Catalog.ImportCatalog.CatalogAthlete> catalog,
        Options options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Seasons < 1 || options.Seasons > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Seasons must be 1..500.");
        }

        SaveStore.CreationRecord created = await store.CreateAsync(
            options.SaveName, options.Seed, options.Stream, catalog, cancellationToken).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;

        SimulateSeasonsHandler sim = new(store);
        int completed = 0;
        int remaining = options.Seasons;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, SimulateSeasonsHandler.MaxSeasonsPerRequest);
            for (int i = 0; i < chunk; i++)
            {
                SimulateSeasonsResponse response = await sim.HandleAsync(
                    saveId, new SimulateSeasonsRequest(1), cancellationToken).ConfigureAwait(false);
                completed = checked(completed + response.SeasonsCompleted);
            }

            remaining -= chunk;
        }

        return await AggregateAsync(store, saveId, options, completed, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Report> AggregateAsync(
        SaveStore store,
        Guid saveId,
        Options options,
        int seasonsCompleted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        await store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = store.OpenDbContext(saveId);

        RulesSnapshotEntity rulesRow = await context.RulesSnapshots
            .AsNoTracking()
            .SingleAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        RulesV1 rules = RulesSnapshotCodec.Decode(rulesRow.RulesJson);

        List<ChaosCupEditionEntity> editions = await context.ChaosCupEditions
            .AsNoTracking()
            .OrderBy(e => e.SourceSeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> standings = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<CupSelectionReportEntity> selections = await context.CupSelectionReports
            .AsNoTracking()
            .OrderBy(e => e.SourceSeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return BuildReport(saveId, options, seasonsCompleted, rules, editions, entrants, standings, selections);
    }

    internal static Report BuildReport(
        Guid saveId,
        Options options,
        int seasonsCompleted,
        RulesV1 rules,
        List<ChaosCupEditionEntity> editions,
        List<ChaosCupEntrantEntity> entrants,
        List<ChaosCupGroupStandingEntity> standings,
        List<CupSelectionReportEntity> selections)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(editions);
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(selections);

        List<EditionSummary> summaries = [];
        foreach (ChaosCupEditionEntity edition in editions.OrderBy(e => e.SourceSeasonNumber))
        {
            summaries.Add(SummarizeEdition(edition, entrants, standings));
        }

        IReadOnlyList<TierCounts> aggregateTier = AggregateTiers(summaries);
        IReadOnlyList<ColorCounts> aggregateColor = AggregateColors(summaries);
        CareerReference careers = BuildCareerReference();
        SelectionImpact impact = BuildSelectionImpact(selections, rules);
        string checksum = ComputeChecksum(summaries, impact);

        return new Report(
            saveId, options.Seasons, seasonsCompleted, options.Seed, options.Stream,
            rules.Version, ChaosCupFormat.ChaosFormatVersion,
            rules.CupBonusWeightPermille, rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille, rules.CupChaosWeightPermille, rules.CupPrestigeWeightPermille,
            CareerPerformanceScorer.ChaosCupIndividualWeight,
            summaries, aggregateTier, aggregateColor, careers, impact, checksum);
    }

    internal static EditionSummary SummarizeEdition(
        ChaosCupEditionEntity edition,
        List<ChaosCupEntrantEntity> entrants,
        List<ChaosCupGroupStandingEntity> standings)
    {
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(entrants);
        ArgumentNullException.ThrowIfNull(standings);

        List<ChaosCupEntrantEntity> field = RequireField(edition, entrants);
        List<ChaosCupGroupStandingEntity> finals = RequireFinals(edition, standings);
        CountEntrants(field, out Dictionary<string, int> entrantsByTier, out Dictionary<string, int> entrantsByColor);
        CountFinals(field, finals, out Dictionary<string, int> finalistsByTier, out Dictionary<string, int> finalistsByColor,
            out Dictionary<string, int> medalsByTier, out Dictionary<string, int> medalsByColor);

        List<TierCounts> byTier = TierOrder
            .Select(t => new TierCounts(t, entrantsByTier[t], finalistsByTier[t], medalsByTier[t]))
            .ToList();
        List<ColorCounts> byColor = Enum.GetValues<SportingColor>()
            .Select(c => new ColorCounts(c.ToString(), entrantsByColor[c.ToString()], finalistsByColor[c.ToString()], medalsByColor[c.ToString()]))
            .ToList();

        return new EditionSummary(
            edition.SourceSeasonNumber, edition.RulesVersion, edition.ChaosFormatVersion,
            field.Count, ChaosCupFormat.StageGroupCounts.ToList(),
            byTier, byColor, finals.Count, byTier.Sum(t => t.Medals));
    }

    private static List<ChaosCupEntrantEntity> RequireField(
        ChaosCupEditionEntity edition, List<ChaosCupEntrantEntity> entrants)
    {
        List<ChaosCupEntrantEntity> field = entrants
            .Where(e => e.SourceSeasonId == edition.SourceSeasonId)
            .ToList();
        if (field.Count != ChaosCupFormat.FieldSize)
        {
            throw new InvalidOperationException(
                $"Chaos edition for Season {edition.SourceSeasonNumber} holds {field.Count} entrants, expected {ChaosCupFormat.FieldSize}.");
        }

        return field;
    }

    private static List<ChaosCupGroupStandingEntity> RequireFinals(
        ChaosCupEditionEntity edition, List<ChaosCupGroupStandingEntity> standings)
    {
        List<ChaosCupGroupStandingEntity> finals = standings
            .Where(e => e.SourceSeasonId == edition.SourceSeasonId && e.StageIndex == ChaosCupFormat.StageCount - 1)
            .OrderBy(e => e.GroupRank)
            .ToList();
        if (finals.Count != ChaosCupFormat.FinalFieldSize)
        {
            throw new InvalidOperationException(
                $"Chaos edition for Season {edition.SourceSeasonNumber} holds {finals.Count} finalists, expected {ChaosCupFormat.FinalFieldSize}.");
        }

        return finals;
    }

    private static void CountEntrants(
        List<ChaosCupEntrantEntity> field,
        out Dictionary<string, int> byTier,
        out Dictionary<string, int> byColor)
    {
        byTier = TierOrder.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        byColor = new(StringComparer.Ordinal);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            byColor[color.ToString()] = 0;
        }

        foreach (ChaosCupEntrantEntity entrant in field)
        {
            byTier[TierForEntrant(entrant)] = checked(byTier[TierForEntrant(entrant)] + 1);
            string color = ((SportingColor)entrant.SportingColor).ToString();
            byColor[color] = checked(byColor[color] + 1);
        }
    }

    private static void CountFinals(
        List<ChaosCupEntrantEntity> field,
        List<ChaosCupGroupStandingEntity> finals,
        out Dictionary<string, int> finalistsByTier,
        out Dictionary<string, int> finalistsByColor,
        out Dictionary<string, int> medalsByTier,
        out Dictionary<string, int> medalsByColor)
    {
        Dictionary<int, string> tierByAthlete = field.ToDictionary(e => e.SaveAthleteId, TierForEntrant);
        finalistsByTier = TierOrder.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        finalistsByColor = new(StringComparer.Ordinal);
        medalsByTier = TierOrder.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        medalsByColor = new(StringComparer.Ordinal);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            finalistsByColor[color.ToString()] = 0;
            medalsByColor[color.ToString()] = 0;
        }

        foreach (ChaosCupGroupStandingEntity final in finals)
        {
            string tier = tierByAthlete.TryGetValue(final.SaveAthleteId, out string? found) ? found : "Pool";
            finalistsByTier[tier] = checked(finalistsByTier[tier] + 1);
            string color = ((SportingColor)final.SportingColor).ToString();
            finalistsByColor[color] = checked(finalistsByColor[color] + 1);
            if (final.GroupRank is >= 1 and <= 3)
            {
                medalsByTier[tier] = checked(medalsByTier[tier] + 1);
                medalsByColor[color] = checked(medalsByColor[color] + 1);
            }
        }
    }

    internal static IReadOnlyList<TierCounts> AggregateTiers(List<EditionSummary> editions)
    {
        Dictionary<string, TierCounts> totals = TierOrder.ToDictionary(t => t, t => new TierCounts(t, 0, 0, 0), StringComparer.Ordinal);
        foreach (EditionSummary edition in editions)
        {
            foreach (TierCounts tier in edition.ByTier)
            {
                TierCounts current = totals[tier.Tier];
                totals[tier.Tier] = current with
                {
                    Entrants = checked(current.Entrants + tier.Entrants),
                    Finalists = checked(current.Finalists + tier.Finalists),
                    Medals = checked(current.Medals + tier.Medals),
                };
            }
        }

        return TierOrder.Select(t => totals[t]).ToList();
    }

    internal static IReadOnlyList<ColorCounts> AggregateColors(List<EditionSummary> editions)
    {
        Dictionary<string, ColorCounts> totals = new(StringComparer.Ordinal);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            totals[color.ToString()] = new ColorCounts(color.ToString(), 0, 0, 0);
        }

        foreach (EditionSummary edition in editions)
        {
            foreach (ColorCounts color in edition.ByColor)
            {
                ColorCounts current = totals[color.Color];
                totals[color.Color] = current with
                {
                    Entrants = checked(current.Entrants + color.Entrants),
                    Finalists = checked(current.Finalists + color.Finalists),
                    Medals = checked(current.Medals + color.Medals),
                };
            }
        }

        return Enum.GetValues<SportingColor>().Select(c => totals[c.ToString()]).ToList();
    }

    internal static CareerReference BuildCareerReference()
    {
        int champion = Score(CareerPerformanceScorer.Category.ChaosCupIndividual, 1, 32, true);
        int last = Score(CareerPerformanceScorer.Category.ChaosCupIndividual, 32, 32, true);
        int bestExit = CareerPerformanceScorer.ChaosExitPointsForRank(1);
        int super = Score(CareerPerformanceScorer.Category.Superleague, 1, 32, true);
        int color = Score(CareerPerformanceScorer.Category.ColorCupIndividual, 1, 32, true);
        int feeder1 = Score(CareerPerformanceScorer.Category.Feeder1, 1, 32, true);
        int p6 = Score(CareerPerformanceScorer.Category.Superleague, 6, 32, true);
        return new CareerReference(champion, last, bestExit, super, color, feeder1, checked(p6 * 5));
    }

    private static int Score(CareerPerformanceScorer.Category category, int rank, int fieldSize, bool isFinal) =>
        CareerPerformanceScorer.ScoreEvent(new CareerPerformanceScorer.PlacementEvent(7, 3, category, rank, fieldSize, isFinal));

    internal static SelectionImpact BuildSelectionImpact(List<CupSelectionReportEntity> selections, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(rules);
        int colorTeams = 0;
        int colorFlipped = 0;
        int typeTeams = 0;
        int typeFlipped = 0;
        List<SelectionFlip> examples = [];

        foreach (CupSelectionReportEntity stored in selections.OrderBy(e => e.SourceSeasonNumber))
        {
            if (string.Equals(stored.Cup, CupExtensionPoint.ColorCup, StringComparison.Ordinal))
            {
                ColorCupSelectionReportDocument document = ColorCupSelectionReportDocument.FromStored(stored.PayloadJson);
                foreach (ColorCupSelectionReportDocument.Team team in document.Teams)
                {
                    colorTeams++;
                    string name = ((SportingColor)team.SportingColor).ToString();
                    if (IsFlipped(
                        team.Ranking.Select(c => (c.AthleteId, c.FinalRatingThousandths, c.ChaosNormThousandths, c.Rank)).ToList(),
                        document.ChaosWeightPermille,
                        out SelectionFlip? flip))
                    {
                        colorFlipped++;
                        if (examples.Count < 20)
                        {
                            examples.Add(flip! with { SourceSeasonNumber = stored.SourceSeasonNumber, Cup = CupExtensionPoint.ColorCup, TeamName = name });
                        }
                    }
                }
            }
            else if (string.Equals(stored.Cup, CupExtensionPoint.TypeCup, StringComparison.Ordinal))
            {
                TypeCupSelectionReportDocument document = TypeCupSelectionReportDocument.FromStored(stored.PayloadJson);
                foreach (TypeCupSelectionReportDocument.Team team in document.Teams)
                {
                    typeTeams++;
                    if (IsFlipped(
                        team.Ranking.Select(c => (c.AthleteId, c.FinalRatingThousandths, c.ChaosNormThousandths, c.SelectionRank > 0 ? 1 : 5)).ToList(),
                        document.ChaosWeightPermille,
                        out SelectionFlip? flip))
                    {
                        typeFlipped++;
                        if (examples.Count < 20)
                        {
                            examples.Add(flip! with { SourceSeasonNumber = stored.SourceSeasonNumber, Cup = CupExtensionPoint.TypeCup, TeamName = team.CreatureType });
                        }
                    }
                }
            }
        }

        return new SelectionImpact(colorTeams, colorFlipped, typeTeams, typeFlipped, examples);
    }

    internal static bool IsFlipped(
        List<(int AthleteId, int Final, int ChaosNorm, int Rank)> ranking,
        int chaosWeightPermille,
        out SelectionFlip? flip)
    {
        flip = null;
        if (ranking.Count == 0)
        {
            return false;
        }

        // Counterfactual without Chaos: neutral chaos norm 1000 for everyone.
        // neutral = final + (1000 - chaosNorm) * weight / 1000.
        List<(int AthleteId, int Neutral)> neutral = ranking
            .Select(c => (c.AthleteId, checked(c.Final + ((1000 - c.ChaosNorm) * chaosWeightPermille / 1000))))
            .ToList();
        HashSet<int> actualTop = ranking
            .OrderBy(c => c.Rank)
            .Take(4)
            .Select(c => c.AthleteId)
            .ToHashSet();
        HashSet<int> neutralTop = ranking
            .Zip(neutral, (c, n) => (c.AthleteId, n.Neutral))
            .OrderByDescending(p => p.Neutral)
            .ThenBy(p => p.AthleteId)
            .Take(4)
            .Select(p => p.AthleteId)
            .ToHashSet();
        if (actualTop.SetEquals(neutralTop))
        {
            return false;
        }

        int flippedIn = actualTop.Except(neutralTop).First();
        int flippedOut = neutralTop.Except(actualTop).First();
        var inRow = ranking.Single(c => c.AthleteId == flippedIn);
        var outRow = ranking.Single(c => c.AthleteId == flippedOut);
        flip = new SelectionFlip(0, string.Empty, string.Empty, $"#{flippedIn}", $"#{flippedOut}",
            inRow.Final - outRow.Final, inRow.ChaosNorm - outRow.ChaosNorm);
        return true;
    }

    internal static string ComputeChecksum(IReadOnlyList<EditionSummary> editions, SelectionImpact impact)
    {
        StringBuilder builder = new();
        foreach (EditionSummary edition in editions.OrderBy(e => e.SourceSeasonNumber))
        {
            builder.Append(edition.SourceSeasonNumber.ToString(CultureInfo.InvariantCulture));
            builder.Append('|');
            foreach (TierCounts tier in edition.ByTier)
            {
                builder.Append(tier.Tier);
                builder.Append(':');
                builder.Append(tier.Entrants.ToString(CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(tier.Finalists.ToString(CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(tier.Medals.ToString(CultureInfo.InvariantCulture));
                builder.Append(';');
            }

            builder.Append('\n');
        }

        builder.Append(CultureInfo.InvariantCulture, $"selection|color {impact.ColorFlippedTeams}/{impact.ColorTeams}|type {impact.TypeFlippedTeams}/{impact.TypeTeams}\n");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string RenderText(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);
        StringBuilder builder = new();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Chaos calibration: seed={report.Seed} stream={report.Stream} seasons={report.SeasonsCompleted} rules=v{report.RulesVersion} chaos=v{report.ChaosFormatVersion}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Weights: bonus={report.BonusWeightPermille} performance={report.PerformanceWeightPermille} form={report.FormWeightPermille} chaos={report.ChaosWeightPermille} prestige={report.PrestigeWeightPermille} chaosCareer={report.ChaosCupWeight}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Editions: {report.Editions.Count} checksum={report.Checksum}");
        foreach (EditionSummary edition in report.Editions)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Season {edition.SourceSeasonNumber}: entrants={edition.EntrantCount} stages=[{string.Join("/", edition.StageGroupCounts)}] finalists={edition.FinalistCount} medals={edition.MedalCount}");
            foreach (TierCounts tier in edition.ByTier)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  {tier.Tier}: entrants={tier.Entrants} finalists={tier.Finalists} medals={tier.Medals}");
            }

            foreach (ColorCounts color in edition.ByColor)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"  {color.Color}: entrants={color.Entrants} finalists={color.Finalists} medals={color.Medals}");
            }
        }

        builder.AppendLine("Aggregate by tier:");
        foreach (TierCounts tier in report.AggregateByTier)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {tier.Tier}: entrants={tier.Entrants} finalists={tier.Finalists} medals={tier.Medals}");
        }

        builder.AppendLine("Aggregate by color:");
        foreach (ColorCounts color in report.AggregateByColor)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  {color.Color}: entrants={color.Entrants} finalists={color.Finalists} medals={color.Medals}");
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"Careers: chaosChampion={report.Careers.ChaosChampion} chaosLast={report.Careers.ChaosFinalLast} bestExit={report.Careers.ChaosBestExit} super={report.Careers.SuperleagueChampion} color={report.Careers.ColorIndividualChampion} f1={report.Careers.Feeder1Champion} fiveSuperP6={report.Careers.FiveSuperP6Total}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Selection impact (5% chaos): color flipped {report.Selection.ColorFlippedTeams}/{report.Selection.ColorTeams} type flipped {report.Selection.TypeFlippedTeams}/{report.Selection.TypeTeams}");
        foreach (SelectionFlip example in report.Selection.Examples.Take(10))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"  S{example.SourceSeasonNumber} {example.Cup} {example.TeamName}: {example.SelectedName} over {example.MissedName} gap={example.Gap} chaosDelta={example.ChaosDelta}");
        }

        return builder.ToString();
    }
}
