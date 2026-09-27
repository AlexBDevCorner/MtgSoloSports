using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Cups.GetColorCupSelection;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted Color Cup
/// team selection for a completed source season (never resimulates): 32 ranked
/// athletes (4 per color) with final ratings plus raw/normalized components.
/// Without <paramref name="sourceSeasonNumber"/> returns the latest resolved
/// selection. Throws <see cref="ColorCupSelectionNotFoundException"/> (404) when
/// the selection has not been resolved yet, and aborts on corrupt counts.
/// </summary>
public sealed class GetColorCupSelectionHandler
{
    private readonly SaveStore _store;

    public GetColorCupSelectionHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetColorCupSelectionResponse> HandleAsync(
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

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null)
            {
                throw new ColorCupSelectionNotFoundException(
                    $"Color Cup selection for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureSelectedAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
            return explicitSeason;
        }

        List<ColorCupSelectionEntity> any = await context.ColorCupSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new ColorCupSelectionNotFoundException("Color Cup selection has not been resolved yet.");
        }

        int latestSeasonId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latestSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new InvalidOperationException("Color Cup selection references an unknown season.");
        }

        return latest;
    }

    internal static async Task EnsureSelectedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.ColorCupSelections
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            throw new ColorCupSelectionNotFoundException(
                $"Color Cup selection for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<GetColorCupSelectionResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
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
        return MapResponse(saveId, source, rules.Version, rows, names);
    }

    internal static GetColorCupSelectionResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        int rulesVersion,
        List<ColorCupSelectionEntity> rows,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);
        List<GetColorCupTeam> teams = new(8);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            teams.Add(MapTeam(color, rows, names));
        }

        return new GetColorCupSelectionResponse(saveId, source.SeasonNumber, source.Id, rulesVersion, rows.Count, teams);
    }

    internal static GetColorCupTeam MapTeam(
        SportingColor color,
        List<ColorCupSelectionEntity> rows,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);
        List<GetColorCupMember> members = rows
            .Where(r => r.SportingColor == (int)color)
            .OrderBy(r => r.SelectionRank)
            .Select(r => MapMember(r, color, names))
            .ToList();
        return new GetColorCupTeam((int)color, color.ToString(), members);
    }

    internal static GetColorCupMember MapMember(
        ColorCupSelectionEntity row,
        SportingColor color,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(names);
        names.TryGetValue(row.SaveAthleteId, out string? name);
        return new GetColorCupMember(
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
            row.PrestigeRaw);
    }
}
