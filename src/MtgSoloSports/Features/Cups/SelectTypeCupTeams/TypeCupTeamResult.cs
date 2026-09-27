namespace MtgSoloSports.Features.Cups.SelectTypeCupTeams;

/// <summary>
/// One creature type's allocated four representatives ordered #1..#4.
/// Only types that actually receive four distinct athletes participate.
/// </summary>
public sealed record TypeCupTeamResult(
    string CreatureType,
    IReadOnlyList<TypeCupTeamMember> Members);
