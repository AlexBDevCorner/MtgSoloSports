using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

public sealed partial class SelectTypeCupTeamsHandler
{
    internal static async Task PersistSelectionsAsync(
        SaveDbContext context,
        SeasonEntity source,
        TypeCupAllocation.AllocationResult allocation,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (TypeCupAllocation.AllocatedTeam team in allocation.Teams)
        {
            foreach (TypeCupAllocation.AllocatedMember member in team.Members)
            {
                context.TypeCupSelections.Add(new TypeCupSelectionEntity
                {
                    SourceSeasonId = source.Id,
                    SourceSeasonNumber = source.SeasonNumber,
                    CreatureType = team.CreatureType,
                    SaveAthleteId = member.AthleteId,
                    SelectionRank = member.SelectionRank,
                    TypeRank = member.TypeRank,
                    FinalRatingThousandths = member.FinalRatingThousandths,
                    BonusNormThousandths = member.BonusNormThousandths,
                    PerformanceNormThousandths = member.PerformanceNormThousandths,
                    FormNormThousandths = member.FormNormThousandths,
                    PrestigeNormThousandths = member.PrestigeNormThousandths,
                    BonusRawThousandths = member.BonusRawThousandths,
                    PerformanceRawThousandths = member.PerformanceRawThousandths,
                    FormRaw = member.FormRaw,
                    PrestigeRaw = member.PrestigeRaw,
                    RulesVersion = rules.Version,
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<List<TypeCupSelectionEntity>> LoadPersistedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        return await context.TypeCupSelections
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<SelectTypeCupTeamsResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<TypeCupSelectionEntity> rows = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        TypeCupSelectionInvariants.ValidatePersisted(source, rows, rules);
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        return MapPersistedResponse(saveId, source, rules, rows, names);
    }

    internal static SelectTypeCupTeamsResponse MapPersistedResponse(
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        List<TypeCupSelectionEntity> rows,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);
        List<TypeCupTeamResult> teams = rows
            .GroupBy(r => r.CreatureType, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => MapPersistedTeam(g.Key, g.OrderBy(r => r.SelectionRank).ToList(), names))
            .ToList();
        return new SelectTypeCupTeamsResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            rules.Version,
            rows.Count,
            teams.Count,
            rules.CupBonusWeightPermille,
            rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille,
            rules.CupPrestigeWeightPermille,
            teams);
    }

    internal static TypeCupTeamResult MapPersistedTeam(
        string creatureType,
        List<TypeCupSelectionEntity> teamRows,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(teamRows);
        ArgumentNullException.ThrowIfNull(names);
        List<TypeCupTeamMember> members = new(teamRows.Count);
        foreach (TypeCupSelectionEntity row in teamRows)
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            members.Add(new TypeCupTeamMember(
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}",
                row.CreatureType,
                row.SelectionRank,
                row.TypeRank,
                row.FinalRatingThousandths,
                row.BonusNormThousandths,
                row.PerformanceNormThousandths,
                row.FormNormThousandths,
                row.PrestigeNormThousandths,
                row.BonusRawThousandths,
                row.PerformanceRawThousandths,
                row.FormRaw,
                row.PrestigeRaw));
        }

        return new TypeCupTeamResult(creatureType, members);
    }
}
