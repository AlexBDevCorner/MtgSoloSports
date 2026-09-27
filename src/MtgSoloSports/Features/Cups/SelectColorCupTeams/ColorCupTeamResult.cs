namespace MtgSoloSports.Features.Cups.SelectColorCupTeams;

/// <summary>
/// One sporting color's four selected representatives ordered #1..#4.
/// </summary>
public sealed record ColorCupTeamResult(
    int SportingColor,
    string SportingColorName,
    IReadOnlyList<ColorCupTeamMember> Members);
