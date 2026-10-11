namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

/// <summary>
/// Immutable tournament summary for one Type Cup edition (MSS-062, MSS-071).
/// Direct Finals (1-32 teams) carry no qualification stage; larger fields
/// carry every qualification group result plus the fresh 32-team Final.
/// Format v1 uses fixed per-group quotas; format v2 uses equal guaranteed
/// quotas plus global performance wildcards (see <see cref="QualificationPolicyVersion"/>
/// and <see cref="WildcardCount"/>). Qualification scores never carry to the
/// Final; only Final ranks 1-3 hold official medals/honours. Built only from
/// persisted rows, never resimulated and never consuming sporting RNG.
/// </summary>
public sealed record GetTypeCupTournamentResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    bool IsDirectFinal,
    int TeamCount,
    int QualificationGroupCount,
    IReadOnlyList<int> GroupSizes,
    IReadOnlyList<int> FinalPlacesPerGroup,
    string DrawChecksum,
    IReadOnlyList<TypeCupTournamentQualificationGroup> QualificationGroups,
    IReadOnlyList<string> Finalists,
    TypeCupTournamentFinalResult? Final,
    string? ChampionCreatureType,
    string TournamentChecksum,
    int QualificationPolicyVersion = 1,
    int WildcardCount = 0,
    IReadOnlyList<int>? GuaranteedPlacesPerGroup = null,
    IReadOnlyList<TypeCupTournamentWildcard>? Wildcards = null);
