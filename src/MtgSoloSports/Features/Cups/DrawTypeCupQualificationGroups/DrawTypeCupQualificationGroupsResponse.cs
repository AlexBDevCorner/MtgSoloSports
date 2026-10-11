namespace MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;

/// <summary>
/// Persisted Type Cup tournament draw for one completed even source season.
/// For at most 32 selected teams <see cref="IsDirectFinal"/> is true, no
/// qualification stage exists, and <see cref="Groups"/> is empty. For larger
/// fields every selected team appears exactly once across balanced random
/// qualification groups, each holding at most 32 teams. Format v1 draws carry
/// fixed Final-place quotas totalling exactly 32 (extra places to lower group
/// numbers). Format v2 draws (MSS-071, current) carry equal guaranteed quotas
/// per group plus a global <see cref="WildcardCount"/> decided by performance
/// after all qualifiers complete, so no group number confers an advantage.
/// The draw is random via the versioned save RNG, never strength-seeded, and
/// persisted before competition so reload/history never redraws it.
/// <see cref="FinalPlacesPerGroup"/> is the persisted per-group quota: fixed
/// quotas for v1, guaranteed quotas for v2. <see cref="GuaranteedPlacesPerGroup"/>
/// makes the v2 meaning explicit; for v1 it equals <see cref="FinalPlacesPerGroup"/>
/// with <see cref="WildcardCount"/> zero.
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
    IReadOnlyList<TypeCupQualificationGroupResult> Groups,
    int QualificationPolicyVersion = 1,
    IReadOnlyList<int>? GuaranteedPlacesPerGroup = null,
    int WildcardCount = 0);
