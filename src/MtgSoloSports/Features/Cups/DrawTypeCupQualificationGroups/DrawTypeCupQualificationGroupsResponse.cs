namespace MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;

/// <summary>
/// Persisted Type Cup tournament draw for one completed even source season.
/// For at most 32 selected teams <see cref="IsDirectFinal"/> is true, no
/// qualification stage exists, and <see cref="Groups"/> is empty. For larger
/// fields every selected team appears exactly once across balanced random
/// qualification groups, each holding at most 32 teams, with Final-place quotas
/// totalling exactly 32. The draw is random via the versioned save RNG, never
/// strength-seeded, and persisted before competition so reload/history never
/// redraws it.
/// </summary>
public sealed record DrawTypeCupQualificationGroupsResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int RulesVersion,
    int TournamentFormatVersion,
    bool IsDirectFinal,
    int TeamCount,
    int QualificationGroupCount,
    IReadOnlyList<int> GroupSizes,
    IReadOnlyList<int> FinalPlacesPerGroup,
    string DrawChecksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    IReadOnlyList<TypeCupQualificationGroupResult> Groups);
