using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Athletes.GetRecordHoldings;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.AthleteReport;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Builds a versioned,
/// human-readable athlete report from persisted profile projections plus the
/// athlete's current record holdings. Read-only: single AsNoTracking context
/// in one committed read transaction for a consistent snapshot, no RNG access,
/// no mutation, no round-payload decompression beyond what the profile slice
/// already reads.
/// </summary>
public sealed class GetAthleteReportHandler
{
    private readonly SaveStore _store;

    public GetAthleteReportHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

    public async Task<ReportFile> HandleAsync(Guid saveId, int athleteId, string? format, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (athleteId <= 0)
        {
            throw new ArgumentException("Athlete id must be positive.", nameof(athleteId));
        }

        string normalized = ReportContract.NormalizeFormat(format);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);

        GetAthleteProfileResponse profile = await LoadProfileAsync(context, saveId, athleteId, cancellationToken).ConfigureAwait(false);
        GetAthleteRecordHoldingsHandler.AthleteValues values =
            await GetAthleteRecordHoldingsHandler.LoadAthleteValuesAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        GetAthleteRecordHoldingsHandler.GlobalMaxima maxima =
            await GetAthleteRecordHoldingsHandler.LoadGlobalMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        GetAthleteRecordHoldingsResponse holdings = GetAthleteRecordHoldingsHandler.Map(saveId, athleteId, values, maxima);

        int seasonCount = await context.Seasons.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(normalized, "md", StringComparison.Ordinal))
        {
            string markdown = AthleteReportMarkdown.Build(saveId, metadata, rules, seasonCount, profile, holdings);
            return new ReportFile(
                Encoding.UTF8.GetBytes(markdown),
                ReportContract.MarkdownContentType,
                ReportFilenames.AthleteFileName(athleteId, profile.Card.Name, "md"));
        }

        string json = BuildJson(saveId, metadata, rules, seasonCount, profile, holdings);
        return new ReportFile(
            Encoding.UTF8.GetBytes(json),
            ReportContract.JsonContentType,
            ReportFilenames.AthleteFileName(athleteId, profile.Card.Name, "json"));
    }

    internal static async Task<GetAthleteProfileResponse> LoadProfileAsync(
        SaveDbContext context,
        Guid saveId,
        int athleteId,
        CancellationToken cancellationToken)
    {
        SaveAthleteEntity athlete = await GetAthleteProfileHandler.LoadAthleteAsync(context, saveId, athleteId, cancellationToken).ConfigureAwait(false);
        AthleteCareerEntity? career = await GetAthleteProfileHandler.LoadCareerAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        List<AthleteSeasonSummaryEntity> summaries = await GetAthleteProfileHandler.LoadSummariesAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        if (career is null || await GetAthleteProfileHandler.IsIncompleteAsync(context, summaries, cancellationToken).ConfigureAwait(false))
        {
            return await GetAthleteProfileHandler.BuildFallbackAsync(context, saveId, athlete, cancellationToken).ConfigureAwait(false);
        }

        List<AthleteHonourDto> honours = await GetAthleteProfileHandler.LoadHonoursAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        List<AthleteMovementDto> movements = await GetAthleteProfileHandler.LoadMovementsAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        List<AthleteCupSelectionDto> selections = await GetAthleteProfileHandler.LoadCupSelectionsAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        List<AthleteCupHistoryDto> cupHistory = await GetAthleteProfileHandler.LoadCupHistoryAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        Dictionary<int, (string Level, int Division)> levels = await GetAthleteProfileHandler.LoadLeagueLevelsAsync(context, summaries, cancellationToken).ConfigureAwait(false);
        return GetAthleteProfileHandler.MapPersisted(saveId, athlete, career, summaries, honours, movements, selections, cupHistory, levels);
    }

    internal static string BuildJson(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetAthleteProfileResponse profile,
        GetAthleteRecordHoldingsResponse holdings)
    {
        var envelope = new
        {
            schemaVersion = ReportContract.SchemaVersion,
            reportType = ReportContract.AthleteReportType,
            saveId = saveId.ToString("D"),
            generatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            save = new
            {
                name = metadata.Name,
                currentSeason = metadata.CurrentSeason,
                phase = metadata.Phase,
                schemaVersion = metadata.SchemaVersion,
                rulesVersion = rules.Version,
            },
            scope = new { athleteId = profile.AthleteId },
            snapshot = new
            {
                seasonCount,
                currentSeason = metadata.CurrentSeason,
                phase = metadata.Phase,
                lastSeasonNumber = profile.Career.LastSeasonNumber,
                lastStageNumber = profile.Career.LastStageNumber,
            },
            units = new
            {
                points = "fixed-point thousandths: divide by 1000 for display (e.g. 77000 = 77.000 championship points)",
                bonus = "fixed-point thousandths of a percent: divide by 1000 for display (e.g. 7552 = +7.552%)",
                missing = "null means unavailable/not-applicable; 0 is an exact zero, never a placeholder",
            },
            athlete = new
            {
                id = profile.AthleteId,
                name = profile.Card.Name,
                sportingColor = profile.Card.SportingColor,
                sportingColorName = profile.Card.SportingColorName,
                creatureTypes = profile.Card.CreatureTypes,
                frontColors = profile.Card.FrontColors,
                manaCost = profile.Card.ManaCost,
                typeLine = profile.Card.TypeLine,
                imageUrl = profile.Card.ImageUrl,
                setCode = profile.Card.SetCode,
                isArtifact = profile.Card.IsArtifact,
                hasDevoid = profile.Card.HasDevoid,
                hasHybridMana = profile.Card.HasHybridMana,
                typeCupNationality = profile.Card.TypeCupNationality,
            },
            career = profile.Career,
            seasons = profile.Seasons,
            honours = profile.Honours,
            movements = profile.Movements,
            cupSelections = profile.CupSelections,
            cupHistory = profile.CupHistory,
            recordHoldings = holdings.Holdings,
        };
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
    }

    internal static string BuildMarkdown(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetAthleteProfileResponse profile,
        GetAthleteRecordHoldingsResponse holdings)
    {
        return AthleteReportMarkdown.Build(saveId, metadata, rules, seasonCount, profile, holdings);
    }
}
