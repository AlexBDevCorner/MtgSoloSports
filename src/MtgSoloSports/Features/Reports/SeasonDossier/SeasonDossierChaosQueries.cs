using MtgSoloSports.Features.Reports.CupEditionReport;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal static class SeasonDossierChaosQueries
{
    internal static async Task<ChaosSummaryEntry?> TryLoadChaosAsync(
        SaveDbContext context,
        SeasonEntity season,
        SimulationKernel.Rules.RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ChaosDossierData data;
        try
        {
            data = await ChaosDossierData.LoadAsync(context, season, rules, cancellationToken).ConfigureAwait(false);
        }
        catch (CupEditionReportNotFoundException)
        {
            return ChaosNotHeld();
        }

        ChaosChampionEntry? champion = data.Champion is null
            ? null
            : new ChaosChampionEntry(
                data.Champion.AthleteId,
                data.Champion.Name,
                data.Champion.FinalPlace,
                data.Champion.FinalScoreThousandths,
                data.Champion.RoundWins);

        List<CupPodiumEntry> topThree = data.FinalStandings
            .Where(r => r.GroupRank is >= 1 and <= 3)
            .OrderBy(r => r.GroupRank)
            .Select(r => new CupPodiumEntry(
                r.GroupRank,
                r.AthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                r.Name,
                r.GroupScoreThousandths,
                r.GroupRank == 1 ? "Gold" : r.GroupRank == 2 ? "Silver" : "Bronze",
                r.AthleteId,
                r.Name))
            .ToList();

        return new ChaosSummaryEntry(
            data.State,
            true,
            data.IsComplete,
            data.FieldSize,
            data.RoundsPlayed,
            data.TotalRounds,
            data.GroupsCompleted,
            data.TotalGroups,
            data.EditionChecksum,
            data.RulesVersion,
            data.ChaosFormatVersion,
            champion,
            topThree,
            data.ByColor.Select(b => new ChaosBreakdownEntry(b.Key, b.Label, b.Count)).ToList(),
            data.ByDivision.Select(b => new ChaosBreakdownEntry(b.Key, b.Label, b.Count)).ToList(),
            BuildNote(data));
    }

    internal static ChaosSummaryEntry ChaosNotHeld()
    {
        return new ChaosSummaryEntry(
            "NotHeld",
            false,
            false,
            0,
            0,
            0,
            0,
            0,
            string.Empty,
            0,
            0,
            null,
            [],
            [],
            [],
            "Chaos Cup not held for this season; no edition persisted. Pending/incomplete results are omitted, never inferred.");
    }

    internal static string BuildNote(ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.IsComplete)
        {
            return "Chaos Cup complete: Final P1..P3 hold official individual medals (Gold/Silver/Bronze); group scores are isolated per 32-athlete group, reset every stage, and award no league championship points, promotion or new bonus.";
        }

        return $"Chaos Cup {data.State}: field {data.FieldSize} athletes, {data.RoundsPlayed}/{data.TotalRounds} rounds, {data.GroupsCompleted}/{data.TotalGroups} groups finished; pending results omitted, never inferred. Group scores never substitute season totals or weighted career points.";
    }
}
