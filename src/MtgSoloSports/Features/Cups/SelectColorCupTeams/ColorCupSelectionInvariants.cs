using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

/// <summary>
/// Structural validation for persisted Color Cup team selections.
/// Fundamental invariant failures abort; never silently repair.
/// </summary>
public static class ColorCupSelectionInvariants
{
    public static void ValidatePersisted(
        SeasonEntity source,
        IReadOnlyList<ColorCupSelectionEntity> rows,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rules);
        if (rows.Count != rules.ColorCupColorCount * rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException(
                $"Color Cup selection for Season {source.SeasonNumber} must hold exactly {rules.ColorCupColorCount * rules.ColorCupTeamSize} rows, was {rows.Count}.");
        }

        ValidatePerColor(source, rows, rules);
    }

    internal static void ValidatePerColor(
        SeasonEntity source,
        IReadOnlyList<ColorCupSelectionEntity> rows,
        RulesV1 rules)
    {
        foreach (IGrouping<int, ColorCupSelectionEntity> group in rows.GroupBy(r => r.SportingColor))
        {
            ValidateSingleColor(source, group.ToList(), rules);
        }

        HashSet<int> colors = rows.Select(r => r.SportingColor).ToHashSet();
        if (colors.Count != rules.ColorCupColorCount)
        {
            throw new InvalidOperationException(
                $"Color Cup selection for Season {source.SeasonNumber} must cover exactly {rules.ColorCupColorCount} sporting colors, was {colors.Count}.");
        }
    }

    internal static void ValidateSingleColor(
        SeasonEntity source,
        List<ColorCupSelectionEntity> colorRows,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(colorRows);
        ArgumentNullException.ThrowIfNull(rules);
        if (colorRows.Count != rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException(
                $"Color Cup selection for Season {source.SeasonNumber} color {colorRows[0].SportingColor} must hold exactly {rules.ColorCupTeamSize} athletes, was {colorRows.Count}.");
        }

        HashSet<int> ranks = colorRows.Select(r => r.SelectionRank).ToHashSet();
        if (!ranks.SetEquals(Enumerable.Range(1, rules.ColorCupTeamSize)))
        {
            throw new InvalidOperationException(
                $"Color Cup selection for Season {source.SeasonNumber} must cover ranks 1..{rules.ColorCupTeamSize} exactly once per color.");
        }

        HashSet<int> athletes = colorRows.Select(r => r.SaveAthleteId).ToHashSet();
        if (athletes.Count != colorRows.Count)
        {
            throw new InvalidOperationException(
                $"Color Cup selection for Season {source.SeasonNumber} contains duplicate athletes.");
        }

        foreach (ColorCupSelectionEntity row in colorRows)
        {
            ValidateSingleRow(source, row, rules);
        }

        ValidateRankOrdering(colorRows);
    }

    internal static void ValidateSingleRow(SeasonEntity source, ColorCupSelectionEntity row, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rules);
        if (row.SourceSeasonId != source.Id || row.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException(
                $"Color Cup selection row {row.Id} references the wrong source season.");
        }

        if (row.SelectionRank < 1 || row.SelectionRank > rules.ColorCupTeamSize)
        {
            throw new InvalidOperationException($"Color Cup selection row {row.Id} has corrupt rank {row.SelectionRank}.");
        }

        if (row.FinalRatingThousandths < 0 || row.BonusNormThousandths < 0 || row.PerformanceNormThousandths < 0
            || row.FormNormThousandths < 0 || row.PrestigeNormThousandths < 0)
        {
            throw new InvalidOperationException($"Color Cup selection row {row.Id} has corrupt negative scores.");
        }

        if (row.BonusNormThousandths > 1000 || row.PerformanceNormThousandths > 1000
            || row.FormNormThousandths > 1000 || row.PrestigeNormThousandths > 1000)
        {
            throw new InvalidOperationException($"Color Cup selection row {row.Id} has corrupt normalized component above 1000.");
        }

        if (row.BonusRawThousandths < 0 || row.PerformanceRawThousandths < 0 || row.FormRaw < 0 || row.PrestigeRaw < 0)
        {
            throw new InvalidOperationException($"Color Cup selection row {row.Id} has corrupt negative raw inputs.");
        }

        if (row.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Color Cup selection row {row.Id} has rules version {row.RulesVersion}, expected {rules.Version}.");
        }
    }

    internal static void ValidateRankOrdering(List<ColorCupSelectionEntity> colorRows)
    {
        ArgumentNullException.ThrowIfNull(colorRows);
        List<ColorCupSelectionEntity> ordered = colorRows.OrderBy(r => r.SelectionRank).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].FinalRatingThousandths > ordered[i - 1].FinalRatingThousandths)
            {
                throw new InvalidOperationException(
                    $"Color Cup selection rank {ordered[i].SelectionRank} outranks #{ordered[i - 1].SelectionRank} by final rating; ranks must follow selection order.");
            }
        }
    }
}
