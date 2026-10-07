using System.Text.Json;
using MtgSoloSports.Features.History;

namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

/// <summary>
/// Immutable explanation of one Type Cup allocation: for every fielded
/// creature-type team the top of that type's ranking (always including the four
/// members) with ratings, where each ranked athlete ended up, whether it was
/// already capped, and which other types it could have represented; plus the
/// types that had four eligible athletes but lost them to other teams. Written
/// once with the selection rows and replayed by the selection event; never
/// recomputed, because nationality and honours change afterwards. Stored
/// Brotli-compressed via <see cref="RoundPayloadCodec"/>.
/// v2 (MSS-064) adds source-league strength explanation per candidate: source league
/// name/level, competition-strength factor, unadjusted source-season championship
/// points, adjusted performance raw, unadjusted final-ten-stage form aggregate and
/// adjusted form raw. v3 (MSS-065) adds the league-aware prestige breakdown:
/// scaled contributions for Super/F1/F2/F3 titles, Superleague appearances,
/// Super/F1/F2/F3 stage podiums and major Cup titles, summing exactly to
/// prestige raw. v1/v2 payloads remain readable (missing fields decode to
/// defaults) and are never rewritten.
/// </summary>
public sealed record TypeCupSelectionReportDocument(
    int Version,
    int CandidateCount,
    int BonusWeightPermille,
    int PerformanceWeightPermille,
    int FormWeightPermille,
    int PrestigeWeightPermille,
    IReadOnlyList<TypeCupSelectionReportDocument.Team> Teams,
    IReadOnlyList<TypeCupSelectionReportDocument.MissedType> MissedTypes)
{
    public const int PayloadVersion = 3;

    public const int LegacyPayloadVersion = 1;

    /// <summary>Top type-ranked athletes kept per team, in addition to the four members.</summary>
    public const int ShortlistSize = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public sealed record Team(
        string CreatureType,
        int EligibleCount,
        IReadOnlyList<Candidate> Ranking);

    /// <summary>
    /// <see cref="SelectionRank"/> is 1..4 for members of this team and 0 otherwise;
    /// <see cref="AssignedType"/> is the team the athlete plays for, if any.
    /// </summary>
    public sealed record Candidate(
        int AthleteId,
        int TypeRank,
        int SelectionRank,
        string? AssignedType,
        bool Capped,
        IReadOnlyList<Alternative> Alternatives,
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
        int UnadjustedFormAggregate = 0,
        int PrestigeSuperTitleRaw = 0,
        int PrestigeFeeder1TitleRaw = 0,
        int PrestigeFeeder2TitleRaw = 0,
        int PrestigeFeeder3TitleRaw = 0,
        int PrestigeAppearanceRaw = 0,
        int PrestigeSuperStageRaw = 0,
        int PrestigeFeeder1StageRaw = 0,
        int PrestigeFeeder2StageRaw = 0,
        int PrestigeFeeder3StageRaw = 0,
        int PrestigeMajorCupRaw = 0);

    /// <summary>Another type with four or more eligible athletes this athlete could represent.</summary>
    public sealed record Alternative(string CreatureType, int TypeRank, bool FieldsTeam);

    public sealed record MissedType(string CreatureType, int EligibleCount);

    public string ToStored() => RoundPayloadCodec.Encode(JsonSerializer.Serialize(this, JsonOptions));

    public static TypeCupSelectionReportDocument FromStored(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        string json = RoundPayloadCodec.DecodeToJson(stored);
        try
        {
            TypeCupSelectionReportDocument? document = JsonSerializer.Deserialize<TypeCupSelectionReportDocument>(json, JsonOptions);
            return document ?? throw new InvalidOperationException("Type Cup selection report is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Type Cup selection report is corrupt.", ex);
        }
    }
}
