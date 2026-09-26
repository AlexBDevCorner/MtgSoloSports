namespace MtgSoloSports.Features.Saves.CreateSave;

public static class CreateSaveEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/api/saves", async (CreateSaveRequest request, CreateSaveHandler handler, CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return Results.BadRequest(new { error = "Request body is required." });
            }

            try
            {
                CreateSaveResponse response = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
                return Results.Created($"/api/saves/{response.SaveId:D}", response);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
