using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetTypeCupSelection;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.GetTypeCupSelectionReport;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the stored Type Cup
/// selection report for a source season (latest resolved allocation when no
/// season is given): each team's type ranking with the four members flagged,
/// why each member represents the type and where the other ranked athletes
/// went. Never recomputes the allocation. An allocation resolved before
/// reports were stored falls back to the persisted selection rows (members
/// only, no reasons). Throws <see cref="TypeCupSelectionNotFoundException"/>
/// (404) when no allocation exists; aborts when the report disagrees with the
/// selection rows.
/// </summary>
public sealed class GetTypeCupSelectionReportHandler
{
    private readonly SaveStore _store;

    public GetTypeCupSelectionReportHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetTypeCupSelectionReportResponse> HandleAsync(
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
        SeasonEntity source = await GetTypeCupSelectionHandler
            .LoadSourceAsync(context, sourceSeasonNumber, cancellationToken)
            .ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        List<TypeCupSelectionEntity> rows = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        TypeCupSelectionInvariants.ValidatePersisted(source, rows, rules);
        CupSelectionReportEntity? stored = await context.CupSelectionReports
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        TypeCupSelectionReportDocument report = stored is null
            ? FallbackReport(rows, rules)
            : TypeCupSelectionReportDocument.FromStored(stored.PayloadJson);
        EnsureReportMatchesSelection(report, rows);

        HashSet<int> ids = report.Teams.SelectMany(t => t.Ranking).Select(c => c.AthleteId).ToHashSet();
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
        return MapResponse(saveId, source, rules, report, stored is not null, athletes);
    }

    /// <summary>Allocations that predate stored reports: the four members per team only.</summary>
    internal static TypeCupSelectionReportDocument FallbackReport(List<TypeCupSelectionEntity> rows, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rules);
        List<TypeCupSelectionReportDocument.Team> teams = rows
            .GroupBy(r => r.CreatureType, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TypeCupSelectionReportDocument.Team(
                g.Key,
                0,
                g.OrderBy(r => r.TypeRank)
                    .Select(r => new TypeCupSelectionReportDocument.Candidate(
                        r.SaveAthleteId,
                        r.TypeRank,
                        r.SelectionRank,
                        r.CreatureType,
                        Capped: false,
                        [],
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
        return new TypeCupSelectionReportDocument(
            TypeCupSelectionReportDocument.PayloadVersion,
            0,
            rules.CupBonusWeightPermille,
            rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille,
            rules.CupPrestigeWeightPermille,
            teams,
            []);
    }

    internal static void EnsureReportMatchesSelection(
        TypeCupSelectionReportDocument report,
        List<TypeCupSelectionEntity> rows)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(rows);
        int members = report.Teams.Sum(t => t.Ranking.Count(c => c.SelectionRank > 0));
        if (members != rows.Count)
        {
            throw new InvalidOperationException(
                $"Type Cup selection report lists {members} members but {rows.Count} athletes were selected.");
        }

        foreach (TypeCupSelectionEntity row in rows)
        {
            TypeCupSelectionReportDocument.Candidate? match = report.Teams
                .SingleOrDefault(t => string.Equals(t.CreatureType, row.CreatureType, StringComparison.Ordinal))?
                .Ranking.SingleOrDefault(c => c.AthleteId == row.SaveAthleteId);
            if (match is null || match.SelectionRank != row.SelectionRank || match.TypeRank != row.TypeRank
                || match.FinalRatingThousandths != row.FinalRatingThousandths
                || !string.Equals(match.AssignedType, row.CreatureType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Type Cup selection report for Season {row.SourceSeasonNumber} disagrees with the selected athlete {row.SaveAthleteId}.");
            }
        }
    }

    internal static GetTypeCupSelectionReportResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        TypeCupSelectionReportDocument report,
        bool hasFullRanking,
        Dictionary<int, SaveAthleteEntity> athletes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(athletes);
        List<GetTypeCupSelectionReportResponse.Team> teams = report.Teams
            .OrderBy(t => t.CreatureType, StringComparer.Ordinal)
            .Select(t => new GetTypeCupSelectionReportResponse.Team(
                t.CreatureType,
                t.CreatureType,
                t.EligibleCount,
                t.Ranking.OrderBy(c => c.TypeRank).Select(c => MapCandidate(c, hasFullRanking, athletes)).ToList()))
            .ToList();
        List<GetTypeCupSelectionReportResponse.MissedTeam> missed = report.MissedTypes
            .OrderBy(m => m.CreatureType, StringComparer.Ordinal)
            .Select(m => new GetTypeCupSelectionReportResponse.MissedTeam(m.CreatureType, m.EligibleCount))
            .ToList();
        return new GetTypeCupSelectionReportResponse(
            saveId,
            source.SeasonNumber,
            rules.Version,
            hasFullRanking,
            rules.TypeCupMinTeamSize,
            report.CandidateCount,
            report.BonusWeightPermille,
            report.PerformanceWeightPermille,
            report.FormWeightPermille,
            report.PrestigeWeightPermille,
            teams,
            missed);
    }

    internal static GetTypeCupSelectionReportResponse.Candidate MapCandidate(
        TypeCupSelectionReportDocument.Candidate candidate,
        bool hasFullRanking,
        Dictionary<int, SaveAthleteEntity> athletes)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(athletes);
        if (!athletes.TryGetValue(candidate.AthleteId, out SaveAthleteEntity? athlete))
        {
            throw new InvalidOperationException($"Type Cup selection report references unknown athlete {candidate.AthleteId}.");
        }

        bool selected = candidate.SelectionRank > 0;
        return new GetTypeCupSelectionReportResponse.Candidate(
            candidate.AthleteId,
            athlete.Name,
            athlete.ImageUrl,
            candidate.TypeRank,
            selected,
            selected ? candidate.SelectionRank : null,
            candidate.AssignedType,
            candidate.Capped,
            selected && hasFullRanking ? ReasonFor(candidate) : null,
            candidate.Alternatives
                .Select(a => new GetTypeCupSelectionReportResponse.Alternative(a.CreatureType, a.TypeRank, a.FieldsTeam))
                .ToList(),
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
            candidate.UnadjustedFormAggregate);
    }

    /// <summary>
    /// Why a member represents its type: capped nationality, the only type able
    /// to field a team, the type where it ranks best, or a placement away from
    /// its best-ranked type so that the maximum number of teams could be fielded.
    /// </summary>
    internal static string ReasonFor(TypeCupSelectionReportDocument.Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Capped)
        {
            return GetTypeCupSelectionReportResponse.ReasonCapped;
        }

        if (candidate.Alternatives.Count == 0)
        {
            return GetTypeCupSelectionReportResponse.ReasonOnlyType;
        }

        return candidate.Alternatives.All(a => a.TypeRank >= candidate.TypeRank)
            ? GetTypeCupSelectionReportResponse.ReasonBestRank
            : GetTypeCupSelectionReportResponse.ReasonBalanced;
    }
}
