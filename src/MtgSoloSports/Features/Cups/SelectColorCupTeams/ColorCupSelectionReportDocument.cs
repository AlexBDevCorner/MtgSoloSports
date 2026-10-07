using System.Text.Json;
using MtgSoloSports.Features.History;

namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

/// <summary>
/// Immutable explanation of one Color Cup selection: for every sporting color
/// the top of the selection ranking (the four selected athletes plus the
/// nearest misses) with raw inputs, normalized components and final ratings,
/// and the weights that combined them. Written once with the selection rows
/// and replayed by the selection event; never recomputed. Stored
/// Brotli-compressed via <see cref="RoundPayloadCodec"/>.
/// v2 (MSS-064) adds source-league strength explanation: source league name/level
/// or Pool, competition-strength factor, unadjusted source-season championship
/// points, adjusted performance raw, unadjusted final-ten-stage form aggregate
/// and adjusted form raw. v1 payloads remain readable (missing fields decode to
/// Pool/zero defaults) and are never rewritten.
/// </summary>
public sealed record ColorCupSelectionReportDocument(
    int Version,
    int BonusWeightPermille,
    int PerformanceWeightPermille,
    int FormWeightPermille,
    int PrestigeWeightPermille,
    IReadOnlyList<ColorCupSelectionReportDocument.Team> Teams)
{
    public const int PayloadVersion = 2;

    public const int LegacyPayloadVersion = 1;

    /// <summary>Ranked athletes kept per color: the team of four plus eight who missed out.</summary>
    public const int ShortlistSize = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public sealed record Team(
        int SportingColor,
        int CandidateCount,
        IReadOnlyList<Candidate> Ranking);

    public sealed record Candidate(
        int AthleteId,
        int Rank,
        int FinalRatingThousandths,
        int BonusNormThousandths,
        int PerformanceNormThousandths,
        int FormNormThousandths,
        int PrestigeNormThousandths,
        int BonusRawThousandths,
        int PerformanceRawThousandths,
        int FormRaw,
        int PrestigeRaw,
        string? SourceLeagueName = null,
        int? SourceLeagueLevel = null,
        int StrengthFactorPermille = 0,
        int UnadjustedPerformanceThousandths = 0,
        int UnadjustedFormAggregate = 0);

    public string ToStored() => RoundPayloadCodec.Encode(JsonSerializer.Serialize(this, JsonOptions));

    public static ColorCupSelectionReportDocument FromStored(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        string json = RoundPayloadCodec.DecodeToJson(stored);
        try
        {
            ColorCupSelectionReportDocument? document = JsonSerializer.Deserialize<ColorCupSelectionReportDocument>(json, JsonOptions);
            return document ?? throw new InvalidOperationException("Color Cup selection report is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Color Cup selection report is corrupt.", ex);
        }
    }
}
