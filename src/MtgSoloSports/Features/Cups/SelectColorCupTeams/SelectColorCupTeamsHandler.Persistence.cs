using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

public sealed partial class SelectColorCupTeamsHandler
{
    internal static async Task PersistSelectionsAsync(
        SaveDbContext context,
        SeasonEntity source,
        IReadOnlyList<ColorCupSelection.ScoredCandidate> all,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(rules);
        foreach (ColorCupSelection.ScoredCandidate member in all)
        {
            context.ColorCupSelections.Add(new ColorCupSelectionEntity
            {
                SourceSeasonId = source.Id,
                SourceSeasonNumber = source.SeasonNumber,
                SportingColor = member.SportingColor,
                SaveAthleteId = member.AthleteId,
                SelectionRank = member.SelectionRank,
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

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<List<ColorCupSelectionEntity>> LoadPersistedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        return await context.ColorCupSelections
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<SelectColorCupTeamsResponse> BuildResponseAsync(
        SaveStore store,
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<ColorCupSelectionEntity> rows = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ColorCupSelectionInvariants.ValidatePersisted(source, rows, rules);
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        return MapResponse(saveId, source, rules, rows, names);
    }

    internal static SelectColorCupTeamsResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        RulesV1 rules,
        List<ColorCupSelectionEntity> rows,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);
        List<ColorCupTeamResult> teams = new(rules.ColorCupColorCount);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            teams.Add(MapColorTeam(color, rows, names));
        }

        return new SelectColorCupTeamsResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            rules.Version,
            rows.Count,
            rules.CupBonusWeightPermille,
            rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille,
            rules.CupPrestigeWeightPermille,
            teams);
    }

    internal static ColorCupTeamResult MapColorTeam(
        SportingColor color,
        List<ColorCupSelectionEntity> rows,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);
        List<ColorCupSelectionEntity> colorRows = rows
            .Where(r => r.SportingColor == (int)color)
            .OrderBy(r => r.SelectionRank)
            .ToList();
        List<ColorCupTeamMember> members = new(colorRows.Count);
        foreach (ColorCupSelectionEntity row in colorRows)
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            members.Add(new ColorCupTeamMember(
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}",
                color.ToString(),
                row.SelectionRank,
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

        return new ColorCupTeamResult((int)color, color.ToString(), members);
    }
}
