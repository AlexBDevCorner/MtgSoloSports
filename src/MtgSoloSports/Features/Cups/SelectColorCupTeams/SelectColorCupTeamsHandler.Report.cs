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
    /// </summary>
    internal static Dictionary<int, IReadOnlyList<ColorCupSelection.ScoredCandidate>> RankAllColors(
        SelectionInputs inputs,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);
        Dictionary<int, List<ColorCupSelection.CandidateRaw>> byColor = BuildCandidates(inputs, rules);
        ValidateEightColors(byColor, rules);
        Dictionary<int, IReadOnlyList<ColorCupSelection.ScoredCandidate>> rankings = new(byColor.Count);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            rankings[(int)color] = ColorCupSelection.RankAll(byColor[(int)color], rules);
        }

        return rankings;
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
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rankings);
        ArgumentNullException.ThrowIfNull(rules);
        List<ColorCupSelectionReportDocument.Team> teams = new(rankings.Count);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            IReadOnlyList<ColorCupSelection.ScoredCandidate> ranking = rankings[(int)color];
            List<ColorCupSelectionReportDocument.Candidate> shortlist = ranking
                .Take(ColorCupSelectionReportDocument.ShortlistSize)
                .Select(c => new ColorCupSelectionReportDocument.Candidate(
                    c.AthleteId,
                    c.SelectionRank,
                    c.FinalRatingThousandths,
                    c.BonusNormThousandths,
                    c.PerformanceNormThousandths,
                    c.FormNormThousandths,
                    c.PrestigeNormThousandths,
                    c.BonusRawThousandths,
                    c.PerformanceRawThousandths,
                    c.FormRaw,
                    c.PrestigeRaw))
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
