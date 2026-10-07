using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupSelection;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.GetColorCupSelectionReport;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the stored Color Cup
/// selection report for a source season (latest resolved selection when no
/// season is given): the ranking behind each color's team with the selected
/// four flagged. Never recomputes ratings. A selection resolved before reports
/// were stored falls back to the persisted selection rows (selected athletes
/// only). Throws <see cref="ColorCupSelectionNotFoundException"/> (404) when no
/// selection exists; aborts when the report disagrees with the selection rows.
/// </summary>
public sealed class GetColorCupSelectionReportHandler
{
    private readonly SaveStore _store;

    public GetColorCupSelectionReportHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetColorCupSelectionReportResponse> HandleAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await GetColorCupSelectionHandler
            .LoadSourceAsync(context, sourceSeasonNumber, cancellationToken)
            .ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        List<ColorCupSelectionEntity> rows = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ColorCupSelectionInvariants.ValidatePersisted(source, rows, rules);
        CupSelectionReportEntity? stored = await context.CupSelectionReports
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        ColorCupSelectionReportDocument report = stored is null
            ? FallbackReport(rows, rules)
            : ColorCupSelectionReportDocument.FromStored(stored.PayloadJson);
        EnsureReportMatchesSelection(report, rows, rules);

        HashSet<int> ids = report.Teams.SelectMany(t => t.Ranking).Select(c => c.AthleteId).ToHashSet();
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        return MapResponse(saveId, source, rules, report, stored is not null, athletes);
    }

    /// <summary>Selections that predate stored reports: the selected four per color only.</summary>
    internal static ColorCupSelectionReportDocument FallbackReport(List<ColorCupSelectionEntity> rows, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rules);
        List<ColorCupSelectionReportDocument.Team> teams = rows
            .GroupBy(r => r.SportingColor)
            .OrderBy(g => g.Key)
            .Select(g => new ColorCupSelectionReportDocument.Team(
                g.Key,
                0,
                g.OrderBy(r => r.SelectionRank)
                    .Select(r => new ColorCupSelectionReportDocument.Candidate(
                        r.SaveAthleteId,
                        r.SelectionRank,
                        r.FinalRatingThousandths,
                        r.BonusNormThousandths,
                        r.PerformanceNormThousandths,
                        r.FormNormThousandths,
                        r.PrestigeNormThousandths,
                        r.BonusRawThousandths,
                        r.PerformanceRawThousandths,
                        r.FormRaw,
                        r.PrestigeRaw))
                    .ToList()))
            .ToList();
        return new ColorCupSelectionReportDocument(
            ColorCupSelectionReportDocument.PayloadVersion,
            rules.CupBonusWeightPermille,
            rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille,
            rules.CupPrestigeWeightPermille,
            teams);
    }

    internal static void EnsureReportMatchesSelection(
        ColorCupSelectionReportDocument report,
        List<ColorCupSelectionEntity> rows,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (ColorCupSelectionEntity row in rows)
        {
            ColorCupSelectionReportDocument.Candidate? match = report.Teams
                .SingleOrDefault(t => t.SportingColor == row.SportingColor)?
                .Ranking.SingleOrDefault(c => c.AthleteId == row.SaveAthleteId);
            if (match is null || match.Rank != row.SelectionRank || match.Rank > rules.ColorCupTeamSize
                || match.FinalRatingThousandths != row.FinalRatingThousandths)
            {
                throw new InvalidOperationException(
                    $"Color Cup selection report for Season {row.SourceSeasonNumber} disagrees with the selected athlete {row.SaveAthleteId}.");
            }
        }
    }

    internal static GetColorCupSelectionReportResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        ColorCupSelectionReportDocument report,
        bool hasFullRanking,
        Dictionary<int, SaveAthleteEntity> athletes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(athletes);
        List<GetColorCupSelectionReportResponse.Team> teams = report.Teams
            .OrderBy(t => t.SportingColor)
            .Select(t => new GetColorCupSelectionReportResponse.Team(
                ((SportingColor)t.SportingColor).ToString(),
                ((SportingColor)t.SportingColor).ToString(),
                t.CandidateCount,
                t.Ranking.OrderBy(c => c.Rank).Select(c => MapCandidate(c, rules, athletes)).ToList()))
            .ToList();
        return new GetColorCupSelectionReportResponse(
            saveId,
            source.SeasonNumber,
            rules.Version,
            hasFullRanking,
            rules.ColorCupTeamSize,
            report.BonusWeightPermille,
            report.PerformanceWeightPermille,
            report.FormWeightPermille,
            report.PrestigeWeightPermille,
            teams);
    }

    internal static GetColorCupSelectionReportResponse.Candidate MapCandidate(
        ColorCupSelectionReportDocument.Candidate candidate,
        RulesV1 rules,
        Dictionary<int, SaveAthleteEntity> athletes)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(athletes);
        if (!athletes.TryGetValue(candidate.AthleteId, out SaveAthleteEntity? athlete))
        {
            throw new InvalidOperationException($"Color Cup selection report references unknown athlete {candidate.AthleteId}.");
        }

        bool selected = candidate.Rank <= rules.ColorCupTeamSize;
        return new GetColorCupSelectionReportResponse.Candidate(
            candidate.AthleteId,
            athlete.Name,
            athlete.ImageUrl,
            candidate.Rank,
            selected,
            selected ? candidate.Rank : null,
            candidate.FinalRatingThousandths,
            candidate.BonusNormThousandths,
            candidate.PerformanceNormThousandths,
            candidate.FormNormThousandths,
            candidate.PrestigeNormThousandths,
            candidate.BonusRawThousandths,
            candidate.PerformanceRawThousandths,
            candidate.FormRaw,
            candidate.PrestigeRaw,
            candidate.SourceLeagueName,
            candidate.SourceLeagueLevel,
            candidate.StrengthFactorPermille,
            candidate.UnadjustedPerformanceThousandths,
            candidate.UnadjustedFormAggregate,
            candidate.PrestigeSuperTitleRaw,
            candidate.PrestigeFeeder1TitleRaw,
            candidate.PrestigeFeeder2TitleRaw,
            candidate.PrestigeFeeder3TitleRaw,
            candidate.PrestigeAppearanceRaw,
            candidate.PrestigeSuperStageRaw,
            candidate.PrestigeFeeder1StageRaw,
            candidate.PrestigeFeeder2StageRaw,
            candidate.PrestigeFeeder3StageRaw,
            candidate.PrestigeMajorCupRaw);
    }
}
