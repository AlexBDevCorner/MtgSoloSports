using System.Text.Json.Serialization;

namespace MtgSoloSports.Features.Stories;

/// <summary>
/// Structured story-event context. Serialized as <c>ContextJson</c> alongside
/// the stable event type; rendered prose is derived deterministically by
/// <see cref="StoryEventRenderer"/> and never stored as the source of truth.
/// All new fields must be optional (nullable) so old events keep decoding
/// after future Cup/record tasks extend the model.
/// </summary>
public sealed record StoryEventPayload(
    [property: JsonPropertyName("athleteName")] string AthleteName,
    [property: JsonPropertyName("seasonNumber")] int SeasonNumber,
    [property: JsonPropertyName("leagueName")] string? LeagueName = null,
    [property: JsonPropertyName("stageNumber")] int? StageNumber = null,
    [property: JsonPropertyName("stageRank")] int? StageRank = null,
    [property: JsonPropertyName("seasonRank")] int? SeasonRank = null,
    [property: JsonPropertyName("titleCount")] int? TitleCount = null,
    [property: JsonPropertyName("fromLeagueName")] string? FromLeagueName = null,
    [property: JsonPropertyName("toLeagueName")] string? ToLeagueName = null,
    [property: JsonPropertyName("fromSeasonNumber")] int? FromSeasonNumber = null,
    [property: JsonPropertyName("toSeasonNumber")] int? ToSeasonNumber = null,
    [property: JsonPropertyName("fromSeasonRank")] int? FromSeasonRank = null,
    [property: JsonPropertyName("viaQualifier")] bool? ViaQualifier = null,
    [property: JsonPropertyName("sportingColor")] string? SportingColor = null,
    [property: JsonPropertyName("recordKey")] string? RecordKey = null,
    [property: JsonPropertyName("recordValue")] int? RecordValue = null,
    [property: JsonPropertyName("priorRecordValue")] int? PriorRecordValue = null);
