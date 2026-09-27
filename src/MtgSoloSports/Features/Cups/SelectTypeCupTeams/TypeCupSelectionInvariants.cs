using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

/// <summary>
/// Structural validation for persisted Type Cup team allocations.
/// Fundamental invariant failures abort; never silently repair.
/// </summary>
public static class TypeCupSelectionInvariants
{
    public static void ValidatePersisted(
        SeasonEntity source,
        IReadOnlyList<TypeCupSelectionEntity> rows,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rules);
        if (rows.Count % rules.TypeCupMinTeamSize != 0)
        {
            throw new InvalidOperationException(
                $"Type Cup allocation for Season {source.SeasonNumber} must hold whole teams of {rules.TypeCupMinTeamSize}, was {rows.Count} rows.");
        }

        HashSet<int> athletes = new();
        foreach (TypeCupSelectionEntity row in rows)
        {
            if (!athletes.Add(row.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Type Cup allocation for Season {source.SeasonNumber} contains duplicate athlete {row.SaveAthleteId}.");
            }
        }

        foreach (IGrouping<string, TypeCupSelectionEntity> group in rows.GroupBy(r => r.CreatureType, StringComparer.Ordinal))
        {
            ValidateSingleTeam(source, group.Key, group.ToList(), rules);
        }
    }

    internal static void ValidateSingleTeam(
        SeasonEntity source,
        string creatureType,
        List<TypeCupSelectionEntity> teamRows,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(teamRows);
        ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrWhiteSpace(creatureType))
        {
            throw new InvalidOperationException(
                $"Type Cup allocation for Season {source.SeasonNumber} contains an empty creature type.");
        }

        if (teamRows.Count != rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException(
                $"Type Cup team '{creatureType}' for Season {source.SeasonNumber} must hold exactly {rules.TypeCupMinTeamSize} athletes, was {teamRows.Count}.");
        }

        HashSet<int> ranks = teamRows.Select(r => r.SelectionRank).ToHashSet();
        if (!ranks.SetEquals(Enumerable.Range(1, rules.TypeCupMinTeamSize)))
        {
            throw new InvalidOperationException(
                $"Type Cup team '{creatureType}' for Season {source.SeasonNumber} must cover ranks 1..{rules.TypeCupMinTeamSize} exactly once.");
        }

        foreach (TypeCupSelectionEntity row in teamRows)
        {
            ValidateSingleRow(source, creatureType, row, rules);
        }

        ValidateRankOrdering(teamRows);
    }

    internal static void ValidateSingleRow(
        SeasonEntity source, string creatureType, TypeCupSelectionEntity row, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rules);
        if (row.SourceSeasonId != source.Id || row.SourceSeasonNumber != source.SeasonNumber)
        {
            throw new InvalidOperationException(
                $"Type Cup selection row {row.Id} references the wrong source season.");
        }

        if (!string.Equals(row.CreatureType, creatureType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Type Cup selection row {row.Id} mixes creature types within one team.");
        }

        if (row.SelectionRank < 1 || row.SelectionRank > rules.TypeCupMinTeamSize)
        {
            throw new InvalidOperationException($"Type Cup selection row {row.Id} has corrupt rank {row.SelectionRank}.");
        }

        if (row.TypeRank < 1)
        {
            throw new InvalidOperationException($"Type Cup selection row {row.Id} has corrupt type rank {row.TypeRank}.");
        }

        if (row.FinalRatingThousandths < 0 || row.BonusNormThousandths < 0 || row.PerformanceNormThousandths < 0
            || row.FormNormThousandths < 0 || row.PrestigeNormThousandths < 0)
        {
            throw new InvalidOperationException($"Type Cup selection row {row.Id} has corrupt negative scores.");
        }

        if (row.BonusNormThousandths > 1000 || row.PerformanceNormThousandths > 1000
            || row.FormNormThousandths > 1000 || row.PrestigeNormThousandths > 1000)
        {
            throw new InvalidOperationException($"Type Cup selection row {row.Id} has corrupt normalized component above 1000.");
        }

        if (row.BonusRawThousandths < 0 || row.PerformanceRawThousandths < 0 || row.FormRaw < 0 || row.PrestigeRaw < 0)
        {
            throw new InvalidOperationException($"Type Cup selection row {row.Id} has corrupt negative raw inputs.");
        }

        if (row.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Type Cup selection row {row.Id} has rules version {row.RulesVersion}, expected {rules.Version}.");
        }
    }

    internal static void ValidateRankOrdering(List<TypeCupSelectionEntity> teamRows)
    {
        ArgumentNullException.ThrowIfNull(teamRows);
        List<TypeCupSelectionEntity> ordered = teamRows.OrderBy(r => r.SelectionRank).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].FinalRatingThousandths > ordered[i - 1].FinalRatingThousandths)
            {
                throw new InvalidOperationException(
                    $"Type Cup team '{ordered[i].CreatureType}' rank {ordered[i].SelectionRank} outranks #{ordered[i - 1].SelectionRank} by final rating; ranks must follow selection order.");
            }
        }
    }
}
