using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// MSS-066 opt-in Cup-selection calibration diagnostic.
/// Simulates a configurable multi-season tiered save with a creature-type
/// diverse catalog (both Color Cup and Type Cup are exercised) and aggregates
/// persisted <c>CupSelectionReports</c> by source-season league level.
/// Read-only aggregation: reports are decoded, never recomputed and never
/// rewritten; no sporting state is mutated. Production behavior is untouched
/// when disabled: the harness only runs under the opt-in
/// <c>MTG_CUP_CALIBRATION=1</c> flag, outside normal CI.
/// Outer weights (35/30/25/10) and strength factors (1000/800/600/400) are
/// recorded, never auto-tuned. No tier quota or absolute tier-priority rule
/// exists anywhere in this path: lower-tier selections are reported as
/// inspectable examples, never enforced or forbidden.
/// </summary>
public static class CupSelectionCalibrationHarness
{
    public sealed record Options(
        int Seasons,
        ulong Seed,
        ulong Stream,
        string SaveName);

    /// <summary>Tier keys in competitive order: Super, F1, F2, F3, Pool.</summary>
    public static readonly IReadOnlyList<string> TierOrder = ["Super", "F1", "F2", "F3", "Pool"];

    public sealed record TierStats(
        string Tier,
        int Candidates,
        int Selected,
        double SelectionRate,
        double AvgFinalRating,
        double MedianFinalRating,
        double AvgBonusNorm,
        double AvgPerformanceNorm,
        double AvgFormNorm,
        double AvgPrestigeNorm);

    public sealed record NearestMissComparison(
        int SourceSeasonNumber,
        string Cup,
        string TeamName,
        string SelectedName,
        string SelectedTier,
        int SelectedFinal,
        string MissedName,
        string MissedTier,
        int MissedFinal,
        int Gap,
        int BonusDelta,
        int PerformanceDelta,
        int FormDelta,
        int PrestigeDelta);

    public sealed record CrossTierExample(
        int SourceSeasonNumber,
        string Cup,
        string TeamName,
        string SelectedName,
        string SelectedTier,
        int SelectedFinal,
        string SelectedDominant,
        string MissedName,
        string MissedTier,
        int MissedFinal,
        int Gap,
        int TierDistance);

    public sealed record EditionSummary(
        int SourceSeasonNumber,
        string Cup,
        int RulesVersion,
        IReadOnlyList<TierStats> ByTier,
        int CrossTierSelections,
        int CrossTierOpportunities);

    public sealed record Report(
        Guid SaveId,
        int SeasonsRequested,
        int SeasonsCompleted,
        ulong Seed,
        ulong Stream,
        int RulesVersion,
        int BonusWeightPermille,
        int PerformanceWeightPermille,
        int FormWeightPermille,
        int PrestigeWeightPermille,
        int SuperFactorPermille,
        int Feeder1FactorPermille,
        int Feeder2FactorPermille,
        int Feeder3FactorPermille,
        IReadOnlyList<EditionSummary> Editions,
        IReadOnlyList<TierStats> AggregateByTier,
        IReadOnlyList<NearestMissComparison> NearestMisses,
        IReadOnlyList<CrossTierExample> CrossTierExamples,
        string Checksum);

    public static Options ReadFromEnvironmentOrDefault(int defaultSeasons, ulong seed, ulong stream)
    {
        int seasons = defaultSeasons;
        string? raw = Environment.GetEnvironmentVariable("MTG_CUP_CALIBRATION_SEASONS");
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= 1 && parsed <= 500)
        {
            seasons = parsed;
        }

        string? seedRaw = Environment.GetEnvironmentVariable("MTG_CUP_CALIBRATION_SEED");
        if (ulong.TryParse(seedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsedSeed))
        {
            seed = parsedSeed;
        }

        string? streamRaw = Environment.GetEnvironmentVariable("MTG_CUP_CALIBRATION_STREAM");
        if (ulong.TryParse(streamRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsedStream))
        {
            stream = parsedStream;
        }

        return new Options(seasons, seed, stream, $"CupCalibration {seasons}");
    }

    public static string TierKey(int? level) => level switch
    {
        0 => "Super",
        1 => "F1",
        2 => "F2",
        3 => "F3",
        _ => "Pool",
    };

    public static int TierRank(string tier) => tier switch
    {
        "Super" => 0,
        "F1" => 1,
        "F2" => 2,
        "F3" => 3,
        _ => 4,
    };

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

    internal sealed record CalibrationInputs(
        RulesV1 Rules,
        List<CupSelectionReportEntity> Reports,
        Dictionary<int, SeasonEntity> SeasonsById,
        Dictionary<int, SeasonEntity> SeasonsByNumber,
        Dictionary<int, LeagueLevel> LevelsByLeagueId,
        List<SeasonMembershipEntity> Memberships,
        Dictionary<int, string> NamesByAthlete);

    public static async Task<Report> AggregateAsync(
        SaveStore store,
        Guid saveId,
        Options options,
        int seasonsCompleted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        CalibrationInputs inputs = await LoadInputsAsync(store, saveId, cancellationToken).ConfigureAwait(false);
        return BuildReport(saveId, options, seasonsCompleted, inputs);
    }

    internal static async Task<CalibrationInputs> LoadInputsAsync(
        SaveStore store,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = store.OpenDbContext(saveId);

        RulesSnapshotEntity rulesRow = await context.RulesSnapshots
            .AsNoTracking()
            .SingleAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        RulesV1 rules = RulesSnapshotCodec.Decode(rulesRow.RulesJson);

        List<CupSelectionReportEntity> reports = await context.CupSelectionReports
            .AsNoTracking()
            .OrderBy(e => e.SourceSeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<SeasonEntity> seasons = await context.Seasons
            .AsNoTracking()
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, SeasonEntity> seasonsById = seasons.ToDictionary(e => e.Id);
        Dictionary<int, SeasonEntity> seasonsByNumber = seasons.ToDictionary(e => e.SeasonNumber);

        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, LeagueLevel> levelsByLeagueId = new(leagues.Count);
        foreach (LeagueEntity league in leagues)
        {
            levelsByLeagueId[league.Id] = LeagueEntityLevels.GetLevel(league);
        }

        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, string> namesByAthlete = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);

        return new CalibrationInputs(
            rules, reports, seasonsById, seasonsByNumber,
            levelsByLeagueId, memberships, namesByAthlete);
    }

    internal static Report BuildReport(
        Guid saveId,
        Options options,
        int seasonsCompleted,
        CalibrationInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inputs);
        List<EditionSummary> editions = SummarizeEditions(inputs, out List<NearestMissComparison> nearestMisses, out List<CrossTierExample> crossTier);
        SortFindings(nearestMisses, crossTier);
        IReadOnlyList<TierStats> aggregate = AggregateTiers(
            editions, inputs.SeasonsByNumber, inputs.Memberships, inputs.LevelsByLeagueId, inputs.NamesByAthlete.Count);
        string checksum = ComputeChecksum(editions);
        RulesV1 rules = inputs.Rules;

        return new Report(
            saveId, options.Seasons, seasonsCompleted, options.Seed, options.Stream,
            rules.Version,
            rules.CupBonusWeightPermille, rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille, rules.CupPrestigeWeightPermille,
            rules.GetCupStrengthFactor(LeagueLevel.Superleague),
            rules.GetCupStrengthFactor(LeagueLevel.Feeder1),
            rules.GetCupStrengthFactor(LeagueLevel.Feeder2),
            rules.GetCupStrengthFactor(LeagueLevel.Feeder3),
            editions, aggregate, nearestMisses, crossTier.Take(20).ToList(), checksum);
    }

    internal static List<EditionSummary> SummarizeEditions(
        CalibrationInputs inputs,
        out List<NearestMissComparison> nearestMisses,
        out List<CrossTierExample> crossTier)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        List<EditionSummary> editions = [];
        nearestMisses = [];
        crossTier = [];
        int totalAthletes = inputs.NamesByAthlete.Count;
        foreach (CupSelectionReportEntity stored in inputs.Reports)
        {
            if (!inputs.SeasonsById.TryGetValue(stored.SourceSeasonId, out SeasonEntity? season))
            {
                throw new InvalidOperationException($"Calibration references unknown season {stored.SourceSeasonId}.");
            }

            Dictionary<int, int?> tierByAthlete = ResolveSourceTiers(
                season, inputs.Memberships, inputs.LevelsByLeagueId, totalAthletes, inputs.NamesByAthlete);
            if (string.Equals(stored.Cup, CupExtensionPoint.ColorCup, StringComparison.Ordinal))
            {
                ColorCupSelectionReportDocument document = ColorCupSelectionReportDocument.FromStored(stored.PayloadJson);
                editions.Add(SummarizeColorEdition(
                    season.SeasonNumber, stored.RulesVersion, document, tierByAthlete, inputs.NamesByAthlete,
                    nearestMisses, crossTier, inputs.Rules));
            }
            else if (string.Equals(stored.Cup, CupExtensionPoint.TypeCup, StringComparison.Ordinal))
            {
                TypeCupSelectionReportDocument document = TypeCupSelectionReportDocument.FromStored(stored.PayloadJson);
                editions.Add(SummarizeTypeEdition(
                    season.SeasonNumber, stored.RulesVersion, document, tierByAthlete, inputs.NamesByAthlete,
                    nearestMisses, crossTier, inputs.Rules));
            }
            else
            {
                throw new InvalidOperationException($"Calibration found unknown cup '{stored.Cup}'.");
            }
        }

        return editions;
    }

    internal static void SortFindings(
        List<NearestMissComparison> nearestMisses,
        List<CrossTierExample> crossTier)
    {
        ArgumentNullException.ThrowIfNull(nearestMisses);
        ArgumentNullException.ThrowIfNull(crossTier);
        nearestMisses.Sort(static (left, right) =>
        {
            int season = left.SourceSeasonNumber.CompareTo(right.SourceSeasonNumber);
            if (season != 0)
            {
                return season;
            }

            int cup = string.Compare(left.Cup, right.Cup, StringComparison.Ordinal);
            return cup != 0 ? cup : string.Compare(left.TeamName, right.TeamName, StringComparison.Ordinal);
        });
        crossTier.Sort(static (left, right) =>
        {
            int distance = right.TierDistance.CompareTo(left.TierDistance);
            if (distance != 0)
            {
                return distance;
            }

            int gap = right.Gap.CompareTo(left.Gap);
            if (gap != 0)
            {
                return gap;
            }

            int season = left.SourceSeasonNumber.CompareTo(right.SourceSeasonNumber);
            return season != 0 ? season : string.Compare(left.TeamName, right.TeamName, StringComparison.Ordinal);
        });
    }

    internal static Dictionary<int, int?> ResolveSourceTiers(
        SeasonEntity season,
        List<SeasonMembershipEntity> memberships,
        Dictionary<int, LeagueLevel> levelsByLeagueId,
        int totalAthletes,
        Dictionary<int, string> namesByAthlete)
    {
        Dictionary<int, int?> tiers = new(totalAthletes);
        foreach (int athleteId in namesByAthlete.Keys)
        {
            tiers[athleteId] = null;
        }

        foreach (SeasonMembershipEntity membership in memberships)
        {
            if (membership.SeasonId != season.Id || membership.LeagueId is null)
            {
                continue;
            }

            if (!levelsByLeagueId.TryGetValue(membership.LeagueId.Value, out LeagueLevel level))
            {
                throw new InvalidOperationException($"Calibration found unknown league {membership.LeagueId.Value}.");
            }

            tiers[membership.SaveAthleteId] = (int)level;
        }

        return tiers;
    }

    internal sealed record ShortEntry(
        int AthleteId,
        string Name,
        string Tier,
        bool Selected,
        int Final,
        int BonusNorm,
        int PerfNorm,
        int FormNorm,
        int PrestigeNorm);

    internal static EditionSummary SummarizeColorEdition(
        int seasonNumber,
        int rulesVersion,
        ColorCupSelectionReportDocument document,
        Dictionary<int, int?> tierByAthlete,
        Dictionary<int, string> namesByAthlete,
        List<NearestMissComparison> nearestMisses,
        List<CrossTierExample> crossTier,
        RulesV1 rules)
    {
        List<ShortEntry> all = [];
        foreach (ColorCupSelectionReportDocument.Team team in document.Teams.OrderBy(t => t.SportingColor))
        {
            string teamName = ((MtgSoloSports.SimulationKernel.Catalog.SportingColor)team.SportingColor).ToString();
            List<ShortEntry> ranking = team.Ranking
                .OrderBy(c => c.Rank)
                .Select(c => new ShortEntry(
                    c.AthleteId,
                    namesByAthlete.TryGetValue(c.AthleteId, out string? name) ? name : $"#{c.AthleteId}",
                    TierKey(c.SourceLeagueLevel ?? (tierByAthlete.TryGetValue(c.AthleteId, out int? t) ? t : null)),
                    c.Rank <= rules.ColorCupTeamSize,
                    c.FinalRatingThousandths,
                    c.BonusNormThousandths, c.PerformanceNormThousandths,
                    c.FormNormThousandths, c.PrestigeNormThousandths))
                .ToList();
            all.AddRange(ranking);

            ShortEntry? lastIn = ranking.LastOrDefault(e => e.Selected);
            ShortEntry? firstOut = ranking.FirstOrDefault(e => !e.Selected);
            if (lastIn is not null && firstOut is not null)
            {
                nearestMisses.Add(new NearestMissComparison(
                    seasonNumber, CupExtensionPoint.ColorCup, teamName,
                    lastIn.Name, lastIn.Tier, lastIn.Final,
                    firstOut.Name, firstOut.Tier, firstOut.Final,
                    lastIn.Final - firstOut.Final,
                    lastIn.BonusNorm - firstOut.BonusNorm,
                    lastIn.PerfNorm - firstOut.PerfNorm,
                    lastIn.FormNorm - firstOut.FormNorm,
                    lastIn.PrestigeNorm - firstOut.PrestigeNorm));
            }

            foreach (ShortEntry selected in ranking.Where(e => e.Selected))
            {
                foreach (ShortEntry missed in ranking.Where(e => !e.Selected))
                {
                    if (TierRank(selected.Tier) > TierRank(missed.Tier) && selected.Final >= missed.Final)
                    {
                        crossTier.Add(new CrossTierExample(
                            seasonNumber, CupExtensionPoint.ColorCup, teamName,
                            selected.Name, selected.Tier, selected.Final,
                            DominantComponent(selected, missed, rules),
                            missed.Name, missed.Tier, missed.Final,
                            selected.Final - missed.Final,
                            TierRank(selected.Tier) - TierRank(missed.Tier)));
                    }
                }
            }
        }

        return BuildEdition(seasonNumber, CupExtensionPoint.ColorCup, rulesVersion, all, tierByAthlete, crossTier, seasonNumber);
    }

    internal static EditionSummary SummarizeTypeEdition(
        int seasonNumber,
        int rulesVersion,
        TypeCupSelectionReportDocument document,
        Dictionary<int, int?> tierByAthlete,
        Dictionary<int, string> namesByAthlete,
        List<NearestMissComparison> nearestMisses,
        List<CrossTierExample> crossTier,
        RulesV1 rules)
    {
        List<ShortEntry> all = [];
        foreach (TypeCupSelectionReportDocument.Team team in document.Teams.OrderBy(t => t.CreatureType, StringComparer.Ordinal))
        {
            List<ShortEntry> ranking = team.Ranking
                .OrderBy(c => c.TypeRank)
                .Select(c => new ShortEntry(
                    c.AthleteId,
                    namesByAthlete.TryGetValue(c.AthleteId, out string? name) ? name : $"#{c.AthleteId}",
                    TierKey(c.SourceLeagueLevel ?? (tierByAthlete.TryGetValue(c.AthleteId, out int? t) ? t : null)),
                    c.SelectionRank > 0,
                    c.FinalRatingThousandths,
                    c.BonusNormThousandths, c.PerformanceNormThousandths,
                    c.FormNormThousandths, c.PrestigeNormThousandths))
                .ToList();
            all.AddRange(ranking);

            ShortEntry? weakestMember = ranking.Where(e => e.Selected).OrderByDescending(e => e.Final).LastOrDefault();
            ShortEntry? bestMiss = ranking.Where(e => !e.Selected).OrderBy(e => e.Final).LastOrDefault();
            if (weakestMember is not null && bestMiss is not null)
            {
                nearestMisses.Add(new NearestMissComparison(
                    seasonNumber, CupExtensionPoint.TypeCup, team.CreatureType,
                    weakestMember.Name, weakestMember.Tier, weakestMember.Final,
                    bestMiss.Name, bestMiss.Tier, bestMiss.Final,
                    weakestMember.Final - bestMiss.Final,
                    weakestMember.BonusNorm - bestMiss.BonusNorm,
                    weakestMember.PerfNorm - bestMiss.PerfNorm,
                    weakestMember.FormNorm - bestMiss.FormNorm,
                    weakestMember.PrestigeNorm - bestMiss.PrestigeNorm));
            }

            foreach (ShortEntry selected in ranking.Where(e => e.Selected))
            {
                foreach (ShortEntry missed in ranking.Where(e => !e.Selected))
                {
                    if (TierRank(selected.Tier) > TierRank(missed.Tier) && selected.Final >= missed.Final)
                    {
                        crossTier.Add(new CrossTierExample(
                            seasonNumber, CupExtensionPoint.TypeCup, team.CreatureType,
                            selected.Name, selected.Tier, selected.Final,
                            DominantComponent(selected, missed, rules),
                            missed.Name, missed.Tier, missed.Final,
                            selected.Final - missed.Final,
                            TierRank(selected.Tier) - TierRank(missed.Tier)));
                    }
                }
            }
        }

        return BuildEdition(seasonNumber, CupExtensionPoint.TypeCup, rulesVersion, all, tierByAthlete, crossTier, seasonNumber);
    }

    internal static string DominantComponent(
        ShortEntry selected,
        ShortEntry missed,
        RulesV1 rules)
    {
        long bonus = (long)(selected.BonusNorm - missed.BonusNorm) * rules.CupBonusWeightPermille;
        long perf = (long)(selected.PerfNorm - missed.PerfNorm) * rules.CupPerformanceWeightPermille;
        long form = (long)(selected.FormNorm - missed.FormNorm) * rules.CupFormWeightPermille;
        long prestige = (long)(selected.PrestigeNorm - missed.PrestigeNorm) * rules.CupPrestigeWeightPermille;
        long best = Math.Max(Math.Max(bonus, perf), Math.Max(form, prestige));
        if (best == bonus)
        {
            return "bonus";
        }

        if (best == perf)
        {
            return "performance";
        }

        if (best == form)
        {
            return "form";
        }

        return "prestige";
    }

    internal static EditionSummary BuildEdition(
        int seasonNumber,
        string cup,
        int rulesVersion,
        List<ShortEntry> shortlist,
        Dictionary<int, int?> tierByAthlete,
        List<CrossTierExample> crossTier,
        int currentSeason)
    {
        Dictionary<string, int> candidatesByTier = TierOrder.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        foreach (KeyValuePair<int, int?> entry in tierByAthlete)
        {
            // Type Cup candidates are active athletes only; Color Cup candidates
            // are every save athlete. The per-edition candidate denominator is
            // resolved by the caller through tierByAthlete: for Type editions the
            // caller passes active-only tiers. Here we count what we were given.
            string key = TierKey(entry.Value);
            candidatesByTier[key] = checked(candidatesByTier[key] + 1);
        }

        // The caller for Type editions passes the same full map; restrict the
        // denominator to active athletes when the cup is the Type Cup by
        // dropping Pool (pool never participates in Type Cup allocation).
        // Color editions keep Pool: pool athletes are real Color candidates
        // with zero performance/form.
        if (string.Equals(cup, CupExtensionPoint.TypeCup, StringComparison.Ordinal))
        {
            candidatesByTier["Pool"] = 0;
        }

        List<TierStats> byTier = [];
        foreach (string tier in TierOrder)
        {
            List<ShortEntry> selected = shortlist.Where(e => string.Equals(e.Tier, tier, StringComparison.Ordinal) && e.Selected).ToList();
            List<int> finals = selected.Select(e => e.Final).OrderBy(v => v).ToList();
            double avgFinal = finals.Count == 0 ? 0 : finals.Average();
            double median = Median(finals);
            byTier.Add(new TierStats(
                tier,
                candidatesByTier[tier],
                selected.Count,
                candidatesByTier[tier] == 0 ? 0 : (double)selected.Count / candidatesByTier[tier],
                avgFinal,
                median,
                selected.Count == 0 ? 0 : selected.Average(e => (double)e.BonusNorm),
                selected.Count == 0 ? 0 : selected.Average(e => (double)e.PerfNorm),
                selected.Count == 0 ? 0 : selected.Average(e => (double)e.FormNorm),
                selected.Count == 0 ? 0 : selected.Average(e => (double)e.PrestigeNorm)));
        }

        int editionCross = crossTier.Count(e =>
            e.SourceSeasonNumber == currentSeason && string.Equals(e.Cup, cup, StringComparison.Ordinal));
        int opportunities = shortlist.Count(e => !e.Selected);
        return new EditionSummary(seasonNumber, cup, rulesVersion, byTier, editionCross, opportunities);
    }

    internal static double Median(List<int> ordered)
    {
        if (ordered.Count == 0)
        {
            return 0;
        }

        int mid = ordered.Count / 2;
        if (ordered.Count % 2 == 1)
        {
            return ordered[mid];
        }

        return (ordered[mid - 1] + ordered[mid]) / 2.0;
    }

    internal static IReadOnlyList<TierStats> AggregateTiers(
        List<EditionSummary> editions,
        Dictionary<int, SeasonEntity> seasonsByNumber,
        List<SeasonMembershipEntity> memberships,
        Dictionary<int, LeagueLevel> levelsByLeagueId,
        int totalAthletes)
    {
        // Aggregate candidate denominators from memberships per observed source
        // season, and rating/component averages from the per-edition selected
        // means weighted by selected count. Medians are reported per edition;
        // the aggregate reports the mean of edition medians where practical.
        Dictionary<string, int> candidates = TierOrder.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        Dictionary<string, int> selected = TierOrder.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        Dictionary<string, double> finalSum = TierOrder.ToDictionary(t => t, _ => 0.0, StringComparer.Ordinal);
        Dictionary<string, double> bonusSum = TierOrder.ToDictionary(t => t, _ => 0.0, StringComparer.Ordinal);
        Dictionary<string, double> perfSum = TierOrder.ToDictionary(t => t, _ => 0.0, StringComparer.Ordinal);
        Dictionary<string, double> formSum = TierOrder.ToDictionary(t => t, _ => 0.0, StringComparer.Ordinal);
        Dictionary<string, double> prestigeSum = TierOrder.ToDictionary(t => t, _ => 0.0, StringComparer.Ordinal);

        foreach (EditionSummary edition in editions)
        {
            bool isType = string.Equals(edition.Cup, CupExtensionPoint.TypeCup, StringComparison.Ordinal);
            if (seasonsByNumber.TryGetValue(edition.SourceSeasonNumber, out SeasonEntity? season))
            {
                Dictionary<string, int> perSeason = CountTiersForSeason(season, memberships, levelsByLeagueId, totalAthletes, isType);
                foreach (string tier in TierOrder)
                {
                    candidates[tier] = checked(candidates[tier] + perSeason[tier]);
                }
            }

            foreach (TierStats stats in edition.ByTier)
            {
                selected[stats.Tier] = checked(selected[stats.Tier] + stats.Selected);
                finalSum[stats.Tier] += stats.AvgFinalRating * stats.Selected;
                bonusSum[stats.Tier] += stats.AvgBonusNorm * stats.Selected;
                perfSum[stats.Tier] += stats.AvgPerformanceNorm * stats.Selected;
                formSum[stats.Tier] += stats.AvgFormNorm * stats.Selected;
                prestigeSum[stats.Tier] += stats.AvgPrestigeNorm * stats.Selected;
            }
        }

        List<TierStats> aggregate = [];
        foreach (string tier in TierOrder)
        {
            int n = selected[tier];
            aggregate.Add(new TierStats(
                tier,
                candidates[tier],
                n,
                candidates[tier] == 0 ? 0 : (double)n / candidates[tier],
                n == 0 ? 0 : finalSum[tier] / n,
                n == 0 ? 0 : finalSum[tier] / n,
                n == 0 ? 0 : bonusSum[tier] / n,
                n == 0 ? 0 : perfSum[tier] / n,
                n == 0 ? 0 : formSum[tier] / n,
                n == 0 ? 0 : prestigeSum[tier] / n));
        }

        return aggregate;
    }

    internal static Dictionary<string, int> CountTiersForSeason(
        SeasonEntity season,
        List<SeasonMembershipEntity> memberships,
        Dictionary<int, LeagueLevel> levelsByLeagueId,
        int totalAthletes,
        bool activeOnly)
    {
        Dictionary<string, int> counts = TierOrder.ToDictionary(t => t, _ => 0, StringComparer.Ordinal);
        int active = 0;
        foreach (SeasonMembershipEntity membership in memberships)
        {
            if (membership.SeasonId != season.Id || membership.LeagueId is null)
            {
                continue;
            }

            if (!levelsByLeagueId.TryGetValue(membership.LeagueId.Value, out LeagueLevel level))
            {
                throw new InvalidOperationException($"Calibration found unknown league {membership.LeagueId.Value}.");
            }

            string key = TierKey((int)level);
            counts[key] = checked(counts[key] + 1);
            active = checked(active + 1);
        }

        if (!activeOnly)
        {
            counts["Pool"] = checked(totalAthletes - active);
        }
        else
        {
            counts["Pool"] = 0;
        }

        return counts;
    }

    internal static string ComputeChecksum(IReadOnlyList<EditionSummary> editions)
    {
        StringBuilder builder = new();
        foreach (EditionSummary edition in editions.OrderBy(e => e.SourceSeasonNumber))
        {
            builder.Append(edition.SourceSeasonNumber.ToString(CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(edition.Cup);
            builder.Append('|');
            foreach (TierStats stats in edition.ByTier)
            {
                builder.Append(stats.Tier);
                builder.Append(':');
                builder.Append(stats.Selected.ToString(CultureInfo.InvariantCulture));
                builder.Append(':');
                builder.Append(((int)Math.Round(stats.AvgFinalRating)).ToString(CultureInfo.InvariantCulture));
                builder.Append(';');
            }

            builder.Append('\n');
        }

        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string RenderText(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);
        StringBuilder builder = new();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Cup selection calibration: seed={report.Seed} stream={report.Stream} seasons={report.SeasonsCompleted} rules=v{report.RulesVersion}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Weights: bonus={report.BonusWeightPermille} performance={report.PerformanceWeightPermille} form={report.FormWeightPermille} prestige={report.PrestigeWeightPermille}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Factors: super={report.SuperFactorPermille} f1={report.Feeder1FactorPermille} f2={report.Feeder2FactorPermille} f3={report.Feeder3FactorPermille}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Editions: {report.Editions.Count} checksum={report.Checksum}");
        foreach (EditionSummary edition in report.Editions)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Season {edition.SourceSeasonNumber} {edition.Cup}: cross-tier selections={edition.CrossTierSelections}");
            foreach (TierStats stats in edition.ByTier)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"  {stats.Tier}: candidates={stats.Candidates} selected={stats.Selected} rate={stats.SelectionRate:P1} avgFinal={stats.AvgFinalRating:F1} median={stats.MedianFinalRating:F1} norms(b/p/f/r)={stats.AvgBonusNorm:F0}/{stats.AvgPerformanceNorm:F0}/{stats.AvgFormNorm:F0}/{stats.AvgPrestigeNorm:F0}");
            }
        }

        builder.AppendLine("Aggregate by tier:");
        foreach (TierStats stats in report.AggregateByTier)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  {stats.Tier}: candidates={stats.Candidates} selected={stats.Selected} rate={stats.SelectionRate:P1} avgFinal={stats.AvgFinalRating:F1}");
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"Nearest-miss comparisons: {report.NearestMisses.Count}");
        foreach (NearestMissComparison miss in report.NearestMisses.Take(10))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  S{miss.SourceSeasonNumber} {miss.Cup} {miss.TeamName}: #{miss.SelectedName} ({miss.SelectedTier} {miss.SelectedFinal}) vs {miss.MissedName} ({miss.MissedTier} {miss.MissedFinal}) gap={miss.Gap}");
        }

        builder.AppendLine(CultureInfo.InvariantCulture, $"Cross-tier examples: {report.CrossTierExamples.Count} shown");
        foreach (CrossTierExample example in report.CrossTierExamples.Take(10))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  S{example.SourceSeasonNumber} {example.Cup} {example.TeamName}: {example.SelectedName} ({example.SelectedTier} {example.SelectedFinal}, {example.SelectedDominant}) ahead of {example.MissedName} ({example.MissedTier} {example.MissedFinal}) gap={example.Gap}");
        }

        return builder.ToString();
    }
}
