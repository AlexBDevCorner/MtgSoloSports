namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One Chaos Cup stage on an athlete's profile trajectory (MSS-113).
/// Read from persisted draws plus official group standings only (no payloads,
/// no RNG): drawn but incomplete groups stay provisional with no rank/score,
/// completed groups carry the official place/score/advancement. Group numbers
/// are the persisted 1-based group within the stage and pair with
/// <c>SourceSeasonNumber</c> to link to the persisted edition/group replay.
/// </summary>
public sealed record AthleteChaosStageDto(
    int StageIndex,
    int GroupCount,
    int GroupNumber,
    bool IsFinal,
    bool IsComplete,
    bool IsOfficial,
    int? GroupRank,
    int? GroupScoreThousandths,
    bool? IsAdvanced);
