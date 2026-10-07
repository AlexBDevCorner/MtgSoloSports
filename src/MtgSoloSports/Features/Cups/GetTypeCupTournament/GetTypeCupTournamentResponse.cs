namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

/// <summary>
/// Immutable tournament summary for one Type Cup edition (MSS-062).
/// Direct Finals (1-32 teams) carry no qualification stage; larger fields
/// carry every qualification group result plus the fresh 32-team Final.
/// Qualification scores never carry to the Final; only Final ranks 1-3 hold
/// official medals/honours. Built only from persisted rows, never resimulated.
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
    string TournamentChecksum);
