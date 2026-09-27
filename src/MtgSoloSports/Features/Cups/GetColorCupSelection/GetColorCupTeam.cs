namespace MtgSoloSports.Features.Cups.GetColorCupSelection;

/// <summary>
/// One sporting color's four representatives ordered #1..#4.
/// </summary>
public sealed record GetColorCupTeam(
    int SportingColor,
    string SportingColorName,
    IReadOnlyList<GetColorCupMember> Members);
