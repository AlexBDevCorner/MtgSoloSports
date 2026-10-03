using System.Globalization;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Athletes.SearchAthletes;

public static class SearchAthletesEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        MapSearch(app);
        MapOptions(app);
    }

    private static void MapSearch(WebApplication app)
    {
        app.MapGet("/api/saves/{saveId:guid}/athletes/search", async (
            Guid saveId,
            string? q,
            int? minNonPool,
            int? maxNonPool,
            string? colours,
            string? types,
            string? current,
            int? minHonours,
            int? maxHonours,
            int? minTitles,
            string? hasTitle,
            string? highest,
            int? bestFinishMax,
            int? minSuperSeasons,
            int? maxSuperSeasons,
            string? cup,
            string? sort,
            string? dir,
            int? skip,
            int? take,
            SearchAthletesHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                SearchAthletesQuery query = BuildQuery(
                    q, minNonPool, maxNonPool, colours, types, current,
                    minHonours, maxHonours, minTitles, hasTitle, highest,
                    bestFinishMax, minSuperSeasons, maxSuperSeasons, cup,
                    sort, dir, skip, take);
                SearchAthletesResponse response = await handler.HandleAsync(saveId, query, cancellationToken).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SaveNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static void MapOptions(WebApplication app)
    {
        app.MapGet("/api/saves/{saveId:guid}/athletes/search/options", async (
            Guid saveId,
            SearchAthletesHandler handler,
            CancellationToken cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(handler);
            try
            {
                SearchAthletesOptionsResponse response = await handler.HandleOptionsAsync(saveId, cancellationToken).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (SaveNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    internal static SearchAthletesQuery BuildQuery(
        string? q,
        int? minNonPool,
        int? maxNonPool,
        string? colours,
        string? types,
        string? current,
        int? minHonours,
        int? maxHonours,
        int? minTitles,
        string? hasTitle,
        string? highest,
        int? bestFinishMax,
        int? minSuperSeasons,
        int? maxSuperSeasons,
        string? cup,
        string? sort,
        string? dir,
        int? skip,
        int? take)
    {
        return new SearchAthletesQuery(
            string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            minNonPool,
            maxNonPool,
            ParseColors(colours),
            ParseStrings(types),
            ParseStrings(current),
            minHonours,
            maxHonours,
            minTitles,
            Normalize(hasTitle),
            Normalize(highest),
            bestFinishMax,
            minSuperSeasons,
            maxSuperSeasons,
            Normalize(cup),
            Normalize(sort),
            Normalize(dir),
            skip,
            take);
    }

    internal static IReadOnlyList<int>? ParseColors(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        List<int> colors = [];
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();
            if (!int.TryParse(trimmed, CultureInfo.InvariantCulture, out int color))
            {
                throw new ArgumentException($"Unknown sporting color '{trimmed}'.", nameof(raw));
            }

            colors.Add(color);
        }

        return colors.Count == 0 ? null : colors;
    }

    internal static IReadOnlyList<string>? ParseStrings(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        List<string> values = [];
        foreach (string part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                values.Add(trimmed);
            }
        }

        return values.Count == 0 ? null : values;
    }

    internal static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant();
    }
}
