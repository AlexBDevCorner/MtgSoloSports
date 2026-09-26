using System.Text.Json.Serialization;

namespace MtgSoloSports.Features.Simulation.AdvanceRound;

/// <summary>
/// One athlete placement inside the compact immutable round payload.
/// All sporting values are fixed-point thousandths integers; no floating point.
/// </summary>
public sealed record RoundPayloadEntry(
    [property: JsonPropertyName("athleteId")] int AthleteId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("baseThousandths")] int BaseThousandths,
    [property: JsonPropertyName("activeBonusThousandths")] int ActiveBonusThousandths,
    [property: JsonPropertyName("finalThousandths")] int FinalThousandths,
    [property: JsonPropertyName("cumulativeBeforeThousandths")] int CumulativeBeforeThousandths,
    [property: JsonPropertyName("cumulativeAfterThousandths")] int CumulativeAfterThousandths,
    [property: JsonPropertyName("rankBefore")] int RankBefore,
    [property: JsonPropertyName("rankAfter")] int RankAfter,
    [property: JsonPropertyName("rankMovement")] int RankMovement);
