namespace MtgSoloSports.Features.Cups.GetTypeCupSelection;

/// <summary>
/// One creature type's allocated four representatives ordered #1..#4.
/// </summary>
public sealed record GetTypeCupTeam(
    string CreatureType,
    IReadOnlyList<GetTypeCupMember> Members);
