using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtgSoloSports.SaveSnapshot;

/// <summary>Versioned JSON envelope for one Superleague stage.</summary>
public sealed record StageEvidenceDocument(
    int SchemaVersion,
    string EvidenceType,
    string Outcome,
    DateTimeOffset GeneratedUtc,
    long ElapsedMs,
    EvidenceSource Source,
    EvidenceTarget Target,
    EvidenceUnits Units,
    IReadOnlyList<StageTableRow> StageTable,
    IReadOnlyList<EvidenceRound> Rounds,
    IReadOnlyList<RoundWinner> RoundWinners,
    ChampionshipSnapshot ChampionshipBefore,
    ChampionshipSnapshot ChampionshipAfter,
    AthleteFocusView? AthleteFocus,
    StageEvidenceBuilder.HistoryContext History,
    IReadOnlyList<string> Limitations,
    string Summary,
    EvidenceCoverage Coverage)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
