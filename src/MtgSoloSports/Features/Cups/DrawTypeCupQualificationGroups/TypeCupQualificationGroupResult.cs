namespace MtgSoloSports.Features.Cups.DrawTypeCupQualificationGroups;

/// <summary>
/// One persisted qualification group: balanced team set with its Final-place quota.
/// </summary>
public sealed record TypeCupQualificationGroupResult(
    int QualificationGroup,
    int GroupSize,
    int FinalPlaces,
    IReadOnlyList<string> CreatureTypes);
