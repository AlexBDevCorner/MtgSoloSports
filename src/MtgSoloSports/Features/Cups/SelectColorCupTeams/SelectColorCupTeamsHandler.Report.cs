using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

public sealed partial class SelectColorCupTeamsHandler
{
    /// <summary>
    /// Ranks every candidate of every sporting color. The first four of each
    /// ranking are the selected team; the rest feed the selection report.
    /// Returns rankings plus the strength-aware metrics behind the adjusted raws.
    /// </summary>
    internal static (Dictionary<int, IReadOnlyList<ColorCupSelection.ScoredCandidate>> Rankings, Dictionary<int, CupSelectionMetrics.CupMetrics> Metrics) RankAllColors(
        SelectionInputs inputs,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<int, List<ColorCupSelection.CandidateRaw>> byColor = BuildCandidates(inputs, rules);
        Dictionary<int, List<ColorCupSelection.StageFormEntry>> stagesByAthlete = GroupStages(inputs);
        Dictionary<int, CupSelectionMetrics.CupMetrics> metrics = BuildMetricsMap(inputs, rules, stagesByAthlete);
        ValidateEightColors(byColor, rules);
        Dictionary<int, IReadOnlyList<ColorCupSelection.ScoredCandidate>> rankings = new(byColor.Count);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            rankings[(int)color] = ColorCupSelection.RankAll(byColor[(int)color], rules);
        }

        return (rankings, metrics);
    }

    internal static IReadOnlyList<ColorCupSelection.ScoredCandidate> TakeTeams(
        Dictionary<int, IReadOnlyList<ColorCupSelection.ScoredCandidate>> rankings,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rankings);
        ArgumentNullException.ThrowIfNull(rules);
        List<ColorCupSelection.ScoredCandidate> all = new(rules.ColorCupColorCount * rules.ColorCupTeamSize);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            all.AddRange(rankings[(int)color].Take(rules.ColorCupTeamSize));
        }

        return all;
    }

    internal static ColorCupSelectionReportDocument BuildReport(
        Dictionary<int, IReadOnlyList<ColorCupSelection.ScoredCandidate>> rankings,
        Dictionary<int, CupSelectionMetrics.CupMetrics> metricsByAthlete,
        SelectionInputs inputs,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rankings);
        ArgumentNullException.ThrowIfNull(metricsByAthlete);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        List<ColorCupSelectionReportDocument.Team> teams = new(rankings.Count);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            IReadOnlyList<ColorCupSelection.ScoredCandidate> ranking = rankings[(int)color];
            List<ColorCupSelectionReportDocument.Candidate> shortlist = ranking
                .Take(ColorCupSelectionReportDocument.ShortlistSize)
                .Select(c => MapReportCandidate(c, metricsByAthlete, inputs))
                .ToList();
            teams.Add(new ColorCupSelectionReportDocument.Team((int)color, ranking.Count, shortlist));
        }

        return new ColorCupSelectionReportDocument(
            ColorCupSelectionReportDocument.PayloadVersion,
            rules.CupBonusWeightPermille,
            rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille,
            rules.CupPrestigeWeightPermille,
            teams);
    }

    internal static ColorCupSelectionReportDocument.Candidate MapReportCandidate(
        ColorCupSelection.ScoredCandidate scored,
        Dictionary<int, CupSelectionMetrics.CupMetrics> metricsByAthlete,
        SelectionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(scored);
        ArgumentNullException.ThrowIfNull(metricsByAthlete);
        ArgumentNullException.ThrowIfNull(inputs);
        metricsByAthlete.TryGetValue(scored.AthleteId, out CupSelectionMetrics.CupMetrics? metrics);
        string? leagueName = null;
        int? leagueLevel = null;
        int factor = 0;
        int unadjustedPerformance = 0;
        int unadjustedForm = 0;
        if (metrics is not null)
        {
            leagueLevel = metrics.Level.HasValue ? (int)metrics.Level.Value : null;
            factor = metrics.StrengthFactorPermille;
            unadjustedPerformance = metrics.UnadjustedPerformanceThousandths;
            unadjustedForm = metrics.UnadjustedFormAggregate;
            if (metrics.Level.HasValue
                && inputs.SourceLeagueIdByAthlete.TryGetValue(scored.AthleteId, out int leagueId)
                && inputs.SourceLeagueNames.TryGetValue(leagueId, out string? name))
            {
                leagueName = name;
            }
            else
            {
                leagueName = "Pool";
            }
        }
        else
        {
            leagueName = "Pool";
        }

        return new ColorCupSelectionReportDocument.Candidate(
            scored.AthleteId,
            scored.SelectionRank,
            scored.FinalRatingThousandths,
            scored.BonusNormThousandths,
            scored.PerformanceNormThousandths,
            scored.FormNormThousandths,
            scored.PrestigeNormThousandths,
            scored.BonusRawThousandths,
            scored.PerformanceRawThousandths,
            scored.FormRaw,
            scored.PrestigeRaw,
            leagueName,
            leagueLevel,
            factor,
            unadjustedPerformance,
            unadjustedForm);
    }

    internal static void AddReport(
        SaveDbContext context,
        SeasonEntity source,
        ColorCupSelectionReportDocument report,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(rules);
        context.CupSelectionReports.Add(new CupSelectionReportEntity
        {
            SourceSeasonId = source.Id,
            SourceSeasonNumber = source.SeasonNumber,
            Cup = CupExtensionPoint.ColorCup,
            RulesVersion = rules.Version,
            PayloadJson = report.ToStored(),
        });
    }
}
