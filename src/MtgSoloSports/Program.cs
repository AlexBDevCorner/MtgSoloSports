using MtgSoloSports.Features.Saves.CreateSave;
using MtgSoloSports.Features.Saves.DeleteSave;
using MtgSoloSports.Features.Saves.ListSaves;
using MtgSoloSports.Features.Saves.OpenSave;
using MtgSoloSports.Persistence.Saves;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.Configure<SaveStorageOptions>(builder.Configuration.GetSection(SaveStorageOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SaveSqliteConnectionInterceptor>();
builder.Services.AddSingleton<SaveDbContextFactory>();
builder.Services.AddSingleton<SaveStore>();
builder.Services.AddScoped<CreateSaveHandler>();
builder.Services.AddScoped<ListSavesHandler>();
builder.Services.AddScoped<OpenSaveHandler>();
builder.Services.AddScoped<DeleteSaveHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

CreateSaveEndpoint.Map(app);
ListSavesEndpoint.Map(app);
OpenSaveEndpoint.Map(app);
DeleteSaveEndpoint.Map(app);

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
