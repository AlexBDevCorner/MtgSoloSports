using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Cups.GetTypeCupSelection;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted Type Cup
/// team allocation for a completed even source season (never resimulates):
/// one four-athlete team per participating creature type with final ratings
/// plus raw/normalized components plus selection/type ranks. Without
/// <paramref name="sourceSeasonNumber"/> returns the latest resolved allocation.
/// Throws <see cref="TypeCupSelectionNotFoundException"/> (404) when the
/// allocation has not been resolved yet, and aborts on corrupt counts.
/// </summary>
public sealed class GetTypeCupSelectionHandler
{
    private readonly SaveStore _store;

    public GetTypeCupSelectionHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetTypeCupSelectionResponse> HandleAsync(
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
                throw new TypeCupSelectionNotFoundException(
                    $"Type Cup allocation for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureSelectedAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
            return explicitSeason;
        }

        List<TypeCupSelectionEntity> any = await context.TypeCupSelections
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new TypeCupSelectionNotFoundException("Type Cup allocation has not been resolved yet.");
        }

        int latestSeasonId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latestSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new InvalidOperationException("Type Cup allocation references an unknown season.");
        }

        return latest;
    }

    internal static async Task EnsureSelectedAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.TypeCupSelections
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            throw new TypeCupSelectionNotFoundException(
                $"Type Cup allocation for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<GetTypeCupSelectionResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
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
        return MapResponse(saveId, source, rules.Version, rows, names);
    }

    internal static GetTypeCupSelectionResponse MapResponse(
        Guid saveId,
        SeasonEntity source,
        int rulesVersion,
        List<TypeCupSelectionEntity> rows,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);
        List<GetTypeCupTeam> teams = rows
            .GroupBy(r => r.CreatureType, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => MapTeam(g.Key, g.OrderBy(r => r.SelectionRank).ToList(), names))
            .ToList();
        return new GetTypeCupSelectionResponse(
            saveId, source.SeasonNumber, source.Id, rulesVersion, rows.Count, teams.Count, teams);
    }

    internal static GetTypeCupTeam MapTeam(
        string creatureType,
        List<TypeCupSelectionEntity> teamRows,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(teamRows);
        ArgumentNullException.ThrowIfNull(names);
        List<GetTypeCupMember> members = new(teamRows.Count);
        foreach (TypeCupSelectionEntity row in teamRows)
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            members.Add(new GetTypeCupMember(
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

        return new GetTypeCupTeam(creatureType, members);
    }
}
