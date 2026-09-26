using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Catalog.GetCatalogStats;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Features.Leagues.CurrentStandings;
using MtgSoloSports.Features.Leagues.GetSeason1Leagues;
using MtgSoloSports.Features.Leagues.SeasonProgress;
using MtgSoloSports.Features.Leagues.SeasonTable;
using MtgSoloSports.Features.Saves.CreateSave;
using MtgSoloSports.Features.Saves.DeleteSave;
using MtgSoloSports.Features.Saves.ListSaves;
using MtgSoloSports.Features.Saves.OpenSave;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Simulation.GetStageRounds;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.Configure<SaveStorageOptions>(builder.Configuration.GetSection(SaveStorageOptions.SectionName));
builder.Services.Configure<CatalogStorageOptions>(builder.Configuration.GetSection(CatalogStorageOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SaveSqliteConnectionInterceptor>();
builder.Services.AddSingleton<SaveDbContextFactory>();
builder.Services.AddSingleton<SaveStore>();
builder.Services.AddSingleton<CatalogDbContextFactory>();
builder.Services.AddSingleton<CatalogStore>();
builder.Services.AddScoped<CreateSaveHandler>();
builder.Services.AddScoped<ListSavesHandler>();
builder.Services.AddScoped<OpenSaveHandler>();
builder.Services.AddScoped<DeleteSaveHandler>();
builder.Services.AddScoped<ImportCatalogHandler>();
builder.Services.AddScoped<GetCatalogStatsHandler>();
builder.Services.AddScoped<GetSeason1LeaguesHandler>();
builder.Services.AddScoped<AdvanceRoundHandler>();
builder.Services.AddScoped<CompleteStageHandler>();
builder.Services.AddScoped<CompleteStageForAllLeaguesHandler>();
builder.Services.AddScoped<GetStageRoundsHandler>();
builder.Services.AddScoped<GetCurrentStandingsHandler>();
builder.Services.AddScoped<GetSeasonTableHandler>();
builder.Services.AddScoped<GetSeasonProgressHandler>();
builder.Services.AddScoped<GetAthleteProfileHandler>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

CreateSaveEndpoint.Map(app);
ListSavesEndpoint.Map(app);
OpenSaveEndpoint.Map(app);
DeleteSaveEndpoint.Map(app);
ImportCatalogEndpoint.Map(app);
GetCatalogStatsEndpoint.Map(app);
GetSeason1LeaguesEndpoint.Map(app);
AdvanceRoundEndpoint.Map(app);
CompleteStageEndpoint.Map(app);
CompleteStageForAllLeaguesEndpoint.Map(app);
GetStageRoundsEndpoint.Map(app);
GetCurrentStandingsEndpoint.Map(app);
GetSeasonTableEndpoint.Map(app);
GetSeasonProgressEndpoint.Map(app);
GetAthleteProfileEndpoint.Map(app);

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
