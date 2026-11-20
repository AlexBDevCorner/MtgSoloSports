using System.Text.Json;
using System.Text.Json.Serialization;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Shared;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Qualifiers.Engine;

/// <summary>
/// Compact immutable payload for one persisted qualifier round (MSS-150): the
/// single payload type shared by the Superleague promotion qualifier and the
/// feeder boundary qualifiers. One <c>QualifierRound</c> row holds exactly one
/// payload; detailed replay consumes this payload and never resimulates.
/// Placements reuse <see cref="RoundPayloadEntry"/> so scoring validation
/// stays identical to normal league rounds; identity is the postseason
/// transition (from/to season) plus boundary/color plus round number, never a
/// league stage cursor. Serialization is parameterized by boundary so each
/// path keeps its exact persisted bytes: Superleague rounds persist the
/// legacy ten-field shape (identity lives on the row's boundary/color
/// columns), feeder rounds embed <c>boundary</c>/<c>sportingColor</c> between
/// the season numbers and the round number.
/// </summary>
internal sealed record QualifierRoundPayload(
    int Version,
    int RulesVersion,
    int FromSeasonNumber,
    int ToSeasonNumber,
    QualifierBoundary Boundary,
    int SportingColor,
    int RoundNumber,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    string Checksum,
    IReadOnlyList<RoundPayloadEntry> Placements)
{
    public const int PayloadVersion = 1;

    /// <summary>
    /// Persists the round exactly as its boundary always has. Field order and
    /// encoding are byte-identical to the two pre-unification payloads.
    /// </summary>
    public string ToJson()
    {
        if (Boundary == QualifierBoundary.Superleague)
        {
            return JsonSerializer.Serialize(new
            {
                version = Version,
                rulesVersion = RulesVersion,
                fromSeasonNumber = FromSeasonNumber,
                toSeasonNumber = ToSeasonNumber,
                roundNumber = RoundNumber,
                rngBeforeState = RngBeforeState,
                rngBeforeStream = RngBeforeStream,
                rngAfterState = RngAfterState,
                rngAfterStream = RngAfterStream,
                checksum = Checksum,
                placements = Placements,
            });
        }

        return JsonSerializer.Serialize(new
        {
            version = Version,
            rulesVersion = RulesVersion,
            fromSeasonNumber = FromSeasonNumber,
            toSeasonNumber = ToSeasonNumber,
            boundary = (int)Boundary,
            sportingColor = SportingColor,
            roundNumber = RoundNumber,
            rngBeforeState = RngBeforeState,
            rngBeforeStream = RngBeforeStream,
            rngAfterState = RngAfterState,
            rngAfterStream = RngAfterStream,
            checksum = Checksum,
            placements = Placements,
        });
    }

    /// <summary>
    /// Decodes one stored payload (plain JSON or <c>br1:</c>-compressed) into
    /// the shared type. Both persisted shapes parse: Superleague payloads
    /// carry no boundary/color keys and therefore resolve to the Superleague
    /// boundary with storage color 0, which that path never inspects.
    /// </summary>
    public static QualifierRoundPayload FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        string decoded = RoundPayloadCodec.DecodeToJson(json);
        PayloadDocument? document = JsonSerializer.Deserialize<PayloadDocument>(decoded);
        if (document is null)
        {
            throw new SaveCorruptionException("Qualifier round payload is empty.");
        }

        return new QualifierRoundPayload(
            document.Version,
            document.RulesVersion,
            document.FromSeasonNumber,
            document.ToSeasonNumber,
            (QualifierBoundary)document.Boundary,
            document.SportingColor,
            document.RoundNumber,
            document.RngBeforeState,
            document.RngBeforeStream,
            document.RngAfterState,
            document.RngAfterStream,
            document.Checksum,
            document.Placements);
    }

    private sealed record PayloadDocument(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("rulesVersion")] int RulesVersion,
        [property: JsonPropertyName("fromSeasonNumber")] int FromSeasonNumber,
        [property: JsonPropertyName("toSeasonNumber")] int ToSeasonNumber,
        [property: JsonPropertyName("boundary")] int Boundary,
        [property: JsonPropertyName("sportingColor")] int SportingColor,
        [property: JsonPropertyName("roundNumber")] int RoundNumber,
        [property: JsonPropertyName("rngBeforeState")] ulong RngBeforeState,
        [property: JsonPropertyName("rngBeforeStream")] ulong RngBeforeStream,
        [property: JsonPropertyName("rngAfterState")] ulong RngAfterState,
        [property: JsonPropertyName("rngAfterStream")] ulong RngAfterStream,
        [property: JsonPropertyName("checksum")] string Checksum,
        [property: JsonPropertyName("placements")] IReadOnlyList<RoundPayloadEntry> Placements);
}
