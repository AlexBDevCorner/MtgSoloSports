using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Reports;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-091: deterministic inventory of every supported library report.
/// Inventory comes from persisted sporting rows only (seasons, cup
/// selections): saves without Cups yield no edition/team items, never
/// invented reports. Ordering is deterministic (almanac first, then dossiers
/// and editions ascending, then teams ordinal) so dry-run, sync and retries
/// agree. MSS-093 optionally appends the athlete archive (index first, then
/// every persisted athlete by stable id ascending, including common-pool
/// athletes with no season yet); null-category full syncs exclude athletes so
/// the small Publish-now/library path never silently writes thousands of files.
/// </summary>
internal sealed class DriveLibraryInventory
{
    internal IReadOnlyList<DriveLibraryItem> Items { get; }

    private DriveLibraryInventory(IReadOnlyList<DriveLibraryItem> items)
    {
        Items = items;
    }

    internal static async Task<DriveLibraryInventory> LoadAsync(
        SaveDbContext context,
        string saveName,
        CancellationToken cancellationToken,
        bool includeAthletes = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveName);

        List<int> seasons = await LoadSeasonsAsync(context, cancellationToken).ConfigureAwait(false);
        List<int> colorSeasons = await LoadColorSeasonsAsync(context, cancellationToken).ConfigureAwait(false);
        List<int> typeSeasons = await LoadTypeSeasonsAsync(context, cancellationToken).ConfigureAwait(false);
        List<int> chaosSeasons = await LoadChaosSeasonsAsync(context, cancellationToken).ConfigureAwait(false);
        List<string> colorTeams = await LoadColorTeamsAsync(context, cancellationToken).ConfigureAwait(false);
        List<string> typeTeams = await LoadTypeTeamsAsync(context, cancellationToken).ConfigureAwait(false);

        List<DriveLibraryItem> items = [];
        items.Add(AlmanacItem(saveName));
        items.AddRange(seasons.Select(DossierItem));
        items.AddRange(colorSeasons.Select(ColorEditionItem));
        items.AddRange(typeSeasons.Select(TypeEditionItem));
        items.AddRange(chaosSeasons.Select(ChaosEditionItem));
        items.AddRange(colorTeams.Select(ColorTeamItem));
        items.AddRange(typeTeams.Select(TypeTeamItem));

        if (includeAthletes)
        {
            items.Add(AthletesIndexItem());
            List<AthleteInventoryRow> athletes = await LoadAthletesAsync(context, cancellationToken).ConfigureAwait(false);
            items.AddRange(athletes.Select(AthleteItem));
        }

        return new DriveLibraryInventory(items);
    }

    internal sealed record AthleteInventoryRow(int AthleteId, string Name, int SportingColor);

    internal static string AthleteColorName(int sportingColor) =>
        Enum.IsDefined((SportingColor)sportingColor)
            ? ((SportingColor)sportingColor).ToString()
            : $"Color{sportingColor}";

    private static DriveLibraryItem AthletesIndexItem() =>
        new(
            DriveLibraryKind.AthletesIndex,
            DriveLibraryKind.AthletesIndex,
            "athletes-index.md",
            "the Athletes folder",
            null,
            null,
            null);

    private static DriveLibraryItem AthleteItem(AthleteInventoryRow row)
    {
        string colorName = AthleteColorName(row.SportingColor);
        return new(
            DriveLibraryKind.Athlete,
            DriveLibraryKind.AthleteReport(row.AthleteId),
            ReportFilenames.AthleteFileName(row.AthleteId, row.Name, "md"),
            $"the Athletes/{colorName} folder",
            null,
            null,
            null,
            row.AthleteId,
            colorName);
    }

    private static async Task<List<AthleteInventoryRow>> LoadAthletesAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        // Bounded deterministic server-side list: every persisted athlete by
        // stable save-scoped id ascending, including pool athletes with no
        // season. No pagination, no league filtering, no UI roster.
        return await context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .Select(e => new AthleteInventoryRow(e.Id, e.Name, e.SportingColor))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static DriveLibraryItem AlmanacItem(string saveName) =>
        new(
            DriveLibraryKind.Almanac,
            DriveLibraryKind.Almanac,
            ReportFilenames.UniverseAlmanacFileName(saveName, "md"),
            "the configured mtshistory folder",
            null,
            null,
            null);

    private static DriveLibraryItem DossierItem(int season) =>
        new(
            DriveLibraryKind.SeasonDossier,
            DriveLibraryKind.DossierReport(season),
            ReportFilenames.SeasonDossierFileName(season, "md"),
            "the Seasons/ subfolder of the configured mtshistory folder",
            season,
            null,
            null);

    private static DriveLibraryItem ColorEditionItem(int season) =>
        new(
            DriveLibraryKind.ColorEdition,
            DriveLibraryKind.EditionReport("color", season),
            ReportFilenames.CupEditionFileName("color", season, "md"),
            "the Color Cup/Cups folder",
            season,
            "color",
            null);

    private static DriveLibraryItem TypeEditionItem(int season) =>
        new(
            DriveLibraryKind.TypeEdition,
            DriveLibraryKind.EditionReport("type", season),
            ReportFilenames.CupEditionFileName("type", season, "md"),
            "the Type Cup/Cups folder",
            season,
            "type",
            null);

    private static DriveLibraryItem ChaosEditionItem(int season) =>
        new(
            DriveLibraryKind.ChaosEdition,
            DriveLibraryKind.EditionReport("chaos", season),
            ReportFilenames.CupEditionFileName("chaos", season, "md"),
            "the Chaos Cup/Cups folder",
            season,
            "chaos",
            null);

    private static DriveLibraryItem ColorTeamItem(string teamKey) =>
        new(
            DriveLibraryKind.ColorTeam,
            DriveLibraryKind.TeamReport("color", teamKey),
            ReportFilenames.CupTeamFileName("color", teamKey, "md"),
            "the Color Cup/Teams Histories folder",
            null,
            "color",
            teamKey);

    private static DriveLibraryItem TypeTeamItem(string teamKey) =>
        new(
            DriveLibraryKind.TypeTeam,
            DriveLibraryKind.TeamReport("type", teamKey),
            ReportFilenames.CupTeamFileName("type", teamKey, "md"),
            "the Type Cup/Teams Histories folder",
            null,
            "type",
            teamKey);

    private static Task<List<int>> LoadSeasonsAsync(SaveDbContext context, CancellationToken cancellationToken) =>
        context.Seasons
            .AsNoTracking()
            .Select(e => e.SeasonNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);

    private static Task<List<int>> LoadColorSeasonsAsync(SaveDbContext context, CancellationToken cancellationToken) =>
        context.ColorCupSelections
            .AsNoTracking()
            .Select(e => e.SourceSeasonNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);

    private static Task<List<int>> LoadTypeSeasonsAsync(SaveDbContext context, CancellationToken cancellationToken) =>
        context.TypeCupSelections
            .AsNoTracking()
            .Select(e => e.SourceSeasonNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);

    private static Task<List<int>> LoadChaosSeasonsAsync(SaveDbContext context, CancellationToken cancellationToken) =>
        context.ChaosCupEditions
            .AsNoTracking()
            .Select(e => e.SourceSeasonNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);

    private static async Task<List<string>> LoadColorTeamsAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        List<int> values = await context.ColorCupSelections
            .AsNoTracking()
            .Select(e => e.SportingColor)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return values
            .Select(v => CupTeamKeys.ColorKey((SportingColor)v))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<List<string>> LoadTypeTeamsAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        List<string> keys = await context.TypeCupSelections
            .AsNoTracking()
            .Select(e => e.CreatureType)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}
