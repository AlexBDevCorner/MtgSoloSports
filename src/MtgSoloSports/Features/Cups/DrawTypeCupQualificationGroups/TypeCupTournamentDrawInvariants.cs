using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;

/// <summary>
/// Structural invariants for the persisted Type Cup qualification draw (MSS-061, MSS-071).
/// Fundamental failures throw and abort the mutation; corrupted sporting state is
/// never silently repaired. The draw occurs after squad allocation and before
/// qualification rounds; squad membership must not change with group assignment.
/// Format v1 draws use fixed quotas totalling exactly 32; format v2 draws use
/// equal guaranteed quotas plus global wildcards totalling exactly 32. Both
/// versions remain readable; old v1 editions are never reinterpreted as v2.
/// </summary>
public static class TypeCupTournamentDrawInvariants
{
    /// <summary>
    /// Validates persisted draw rows for one source season: every selected team
    /// appears exactly once, group sizes are balanced and at most 32, quotas
    /// follow the persisted per-edition policy version (v1 fixed quotas total
    /// exactly 32; v2 guaranteed plus wildcards total exactly 32),
    /// checksums/RNG/rules linkage are consistent, and the draw teams exactly
    /// match the allocation teams.
    /// </summary>
    public static void ValidatePersisted(
        SeasonEntity source,
        IReadOnlyList<TypeCupSelectionEntity> selection,
        IReadOnlyList<TypeCupTournamentDrawEntity> draws,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(rules);
        if (draws.Count == 0)
        {
            throw new InvalidOperationException($"Type Cup draw for Season {source.SeasonNumber} has no rows.");
        }

        List<string> selectedTypes = selection
            .Select(e => e.CreatureType)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        List<TypeCupTournamentFormat.QualificationAssignment> assignments = draws
            .Select(d => new TypeCupTournamentFormat.QualificationAssignment(d.CreatureType, d.QualificationGroup))
            .ToList();

        TypeCupTournamentDrawEntity first = draws[0];
        CheckDrawLinkage(draws, source, first);
        CheckDrawMetadata(draws, first, rules);

        IReadOnlyList<int> groupSizes = ReadGroupSizes(draws, first);
        IReadOnlyList<int> finalPlaces = ReadFinalPlaces(draws, first);
        TypeCupTournamentFormat.ValidateTournamentDrawVersioned(
            selectedTypes, assignments, groupSizes, finalPlaces, first.TournamentFormatVersion, rules);

        string expectedChecksum = TypeCupTournamentFormat.ComputeDrawChecksum(assignments, selectedTypes.Count, groupSizes);
        foreach (TypeCupTournamentDrawEntity row in draws)
        {
            if (!string.Equals(row.DrawChecksum, expectedChecksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Type Cup draw for Season {source.SeasonNumber} has a corrupt checksum.");
            }

            if (!string.Equals(row.DrawChecksum, first.DrawChecksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Type Cup draw for Season {source.SeasonNumber} has inconsistent checksums.");
            }
        }

        if (!string.Equals(first.DrawChecksum, expectedChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Type Cup draw for Season {source.SeasonNumber} checksum does not match its assignments.");
        }
    }

    private static void CheckDrawLinkage(
        IReadOnlyList<TypeCupTournamentDrawEntity> draws,
        SeasonEntity source,
        TypeCupTournamentDrawEntity first)
    {
        foreach (TypeCupTournamentDrawEntity row in draws)
        {
            if (row.SourceSeasonId != source.Id || row.SourceSeasonNumber != source.SeasonNumber)
            {
                throw new InvalidOperationException($"Type Cup draw row {row.Id} has corrupt source linkage.");
            }

            if (row.FieldTeamCount != first.FieldTeamCount
                || row.QualificationGroupCount != first.QualificationGroupCount
                || row.RulesVersion != first.RulesVersion
                || row.TournamentFormatVersion != first.TournamentFormatVersion
                || row.RngBeforeState != first.RngBeforeState
                || row.RngBeforeStream != first.RngBeforeStream
                || row.RngAfterState != first.RngAfterState
                || row.RngAfterStream != first.RngAfterStream)
            {
                throw new InvalidOperationException($"Type Cup draw for Season {source.SeasonNumber} has inconsistent field metadata.");
            }

            if (string.IsNullOrWhiteSpace(row.CreatureType))
            {
                throw new InvalidOperationException($"Type Cup draw row {row.Id} has an empty creature type.");
            }

            if (row.QualificationGroup < 1 || row.QualificationGroup > row.QualificationGroupCount)
            {
                throw new InvalidOperationException($"Type Cup draw row {row.Id} group {row.QualificationGroup} is out of range.");
            }

            if (row.RngAfterState == row.RngBeforeState && row.RngAfterStream == row.RngBeforeStream)
            {
                throw new InvalidOperationException($"Type Cup draw for Season {source.SeasonNumber} must advance the save RNG.");
            }
        }
    }

    private static void CheckDrawMetadata(
        IReadOnlyList<TypeCupTournamentDrawEntity> draws,
        TypeCupTournamentDrawEntity first,
        RulesV1 rules)
    {
        if (first.RulesVersion != rules.Version)
        {
            throw new InvalidOperationException($"Type Cup draw rules version must be {rules.Version}, was {first.RulesVersion}.");
        }

        if (first.TournamentFormatVersion != RulesV1.FixedQuotaTypeCupTournamentFormatVersion
            && first.TournamentFormatVersion != RulesV1.WildcardTypeCupTournamentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Type Cup draw format version must be v{RulesV1.FixedQuotaTypeCupTournamentFormatVersion} (fixed quotas) or v{RulesV1.WildcardTypeCupTournamentFormatVersion} (wildcards), was {first.TournamentFormatVersion}.");
        }

        if (first.FieldTeamCount != draws.Select(d => d.CreatureType).Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidOperationException("Type Cup draw field count does not match its distinct teams.");
        }
    }

    private static IReadOnlyList<int> ReadGroupSizes(
        IReadOnlyList<TypeCupTournamentDrawEntity> draws,
        TypeCupTournamentDrawEntity first)
    {
        List<int> sizes = new(first.QualificationGroupCount);
        for (int group = 1; group <= first.QualificationGroupCount; group++)
        {
            List<TypeCupTournamentDrawEntity> members = draws.Where(d => d.QualificationGroup == group).ToList();
            if (members.Count == 0)
            {
                throw new InvalidOperationException($"Type Cup draw group {group} has no teams.");
            }

            int size = members[0].GroupSize;
            if (members.Any(m => m.GroupSize != size) || size != members.Count)
            {
                throw new InvalidOperationException($"Type Cup draw group {group} has inconsistent size metadata.");
            }

            sizes.Add(size);
        }

        return sizes;
    }

    private static IReadOnlyList<int> ReadFinalPlaces(
        IReadOnlyList<TypeCupTournamentDrawEntity> draws,
        TypeCupTournamentDrawEntity first)
    {
        List<int> quotas = new(first.QualificationGroupCount);
        for (int group = 1; group <= first.QualificationGroupCount; group++)
        {
            List<TypeCupTournamentDrawEntity> members = draws.Where(d => d.QualificationGroup == group).ToList();
            int quota = members[0].FinalPlacesForGroup;
            if (members.Any(m => m.FinalPlacesForGroup != quota))
            {
                throw new InvalidOperationException($"Type Cup draw group {group} has inconsistent quota metadata.");
            }

            quotas.Add(quota);
        }

        return quotas;
    }
}
