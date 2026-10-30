using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.GetSeasonPlacements;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.StandingsReport;

/// <summary>
/// Shared read-only stage-placement projection for MSS-092. Loads the exact
/// persisted <c>StageStanding</c> cells for one season/league, validates them
/// with the same semantics as the standings matrix, and maps them to stable
/// <c>SeasonPlacementCell</c> records. Stage championship points are per
/// stage; season table totals stay cumulative. Never resimulates, never uses
/// RNG, never decompresses round payloads.
/// </summary>
internal static class StagePlacementReport
{
    internal const int ProjectionVersion = 1;

    internal static async Task<IReadOnlyList<SeasonPlacementCell>> LoadValidatedAsync(
        SaveDbContext context,
        SeasonEntity season,
        LeagueEntity league,
        SimulationKernel.Rules.RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(rules);

        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.SaveAthleteId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<StageStandingEntity> standings = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.StageNumber)
            .ThenBy(e => e.StageRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return MapValidated(memberships, standings, league, rules);
    }

    internal static IReadOnlyList<SeasonPlacementCell> MapValidated(
        List<SeasonMembershipEntity> memberships,
        List<StageStandingEntity> standings,
        LeagueEntity league,
        SimulationKernel.Rules.RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(rules);

        GetSeasonPlacementsHandler.ValidateMemberships(memberships, league);
        GetSeasonPlacementsHandler.ValidateStandings(standings, league, memberships);
        ValidateReportPlacements(memberships, standings, league, rules);

        return GetSeasonPlacementsHandler.MapCells(standings);
    }

    internal static void ValidateReportPlacements(
        List<SeasonMembershipEntity> memberships,
        List<StageStandingEntity> standings,
        LeagueEntity league,
        SimulationKernel.Rules.RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(rules);

        foreach (StageStandingEntity row in standings)
        {
            if (row.SeasonId != league.SeasonId)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {row.StageNumber} references season id {row.SeasonId}; cross-season leakage is forbidden.");
            }

            if (row.LeagueId != league.Id)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {row.StageNumber} references league id {row.LeagueId}; cross-league leakage is forbidden.");
            }

            if (row.StageNumber < 1 || row.StageNumber > rules.StagesPerSeason)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' has corrupt stage {row.StageNumber}; expected 1..{rules.StagesPerSeason}.");
            }
        }

        List<int> stageNumbers = standings.Select(r => r.StageNumber).Distinct().OrderBy(n => n).ToList();
        for (int i = 0; i < stageNumbers.Count; i++)
        {
            if (stageNumbers[i] != i + 1)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' has non-contiguous stage history; expected stages 1..{stageNumbers.Count} without gaps.");
            }
        }

        int distinctPairs = standings.Select(r => (r.SaveAthleteId, r.StageNumber)).Distinct().Count();
        if (distinctPairs != standings.Count)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' has duplicate (athleteId, stageNumber) stage standings.");
        }

        HashSet<int> roster = memberships.Select(m => m.SaveAthleteId).ToHashSet();
        foreach (IGrouping<int, StageStandingEntity> group in standings.GroupBy(r => r.StageNumber))
        {
            HashSet<int> athletes = group.Select(r => r.SaveAthleteId).ToHashSet();
            if (athletes.Count != roster.Count)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {group.Key} must cover exactly {roster.Count} roster athletes, was {athletes.Count}.");
            }

            if (!athletes.SetEquals(roster))
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {group.Key} does not match the season roster.");
            }
        }
    }

    internal static IReadOnlyList<int> StageNumbers(IReadOnlyList<SeasonPlacementCell> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        return placements.Select(c => c.StageNumber).Distinct().OrderBy(n => n).ToList();
    }

    internal static IReadOnlyList<IGrouping<int, SeasonPlacementCell>> GroupByStage(IReadOnlyList<SeasonPlacementCell> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        return placements.GroupBy(c => c.StageNumber).OrderBy(g => g.Key).ToList();
    }

    internal static Dictionary<(int AthleteId, int StageNumber), int> BuildRankLookup(IReadOnlyList<SeasonPlacementCell> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        Dictionary<(int AthleteId, int StageNumber), int> ranks = new(placements.Count);
        foreach (SeasonPlacementCell cell in placements)
        {
            ranks[(cell.AthleteId, cell.StageNumber)] = cell.StageRank;
        }

        return ranks;
    }

    internal static void AppendMatrixBlocks(
        StringBuilder sb,
        IReadOnlyList<GetStandingsReportHandler.StandingsReportRow> orderedRows,
        IReadOnlyList<SeasonPlacementCell> placements,
        IReadOnlyList<int> stages)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(orderedRows);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(stages);
        Dictionary<(int AthleteId, int StageNumber), int> ranks = BuildRankLookup(placements);
        const int BlockSize = 8;
        for (int start = 0; start < stages.Count; start += BlockSize)
        {
            IReadOnlyList<int> block = stages.Skip(start).Take(BlockSize).ToList();
            AppendMatrixBlock(sb, orderedRows, ranks, block);
        }
    }

    private static void AppendMatrixBlock(
        StringBuilder sb,
        IReadOnlyList<GetStandingsReportHandler.StandingsReportRow> ordered,
        Dictionary<(int AthleteId, int StageNumber), int> ranks,
        IReadOnlyList<int> block)
    {
        if (block.Count == 1)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Stage {block[0]}");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Stages {block[0]}-{block[^1]}");
        }

        sb.AppendLine();
        StringBuilder header = new("| Rank | Athlete |");
        foreach (int stage in block)
        {
            header.Append(CultureInfo.InvariantCulture, $" Stage {stage} |");
        }

        sb.AppendLine(header.ToString());
        sb.Append("| --- | --- |");
        foreach (int _ in block)
        {
            sb.Append(" --- |");
        }

        sb.AppendLine();
        foreach (GetStandingsReportHandler.StandingsReportRow row in ordered)
        {
            StringBuilder line = new($"| {row.Rank} | {ReportMarkdown.Cell(row.Name)} (id {row.AthleteId}) |");
            foreach (int stage in block)
            {
                string cell = ranks.TryGetValue((row.AthleteId, stage), out int rank)
                    ? $"P{rank}"
                    : "—";
                line.Append(CultureInfo.InvariantCulture, $" {cell} |");
            }

            sb.AppendLine(line.ToString());
        }

        sb.AppendLine();
    }
}
