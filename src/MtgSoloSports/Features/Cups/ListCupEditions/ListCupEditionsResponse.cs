namespace MtgSoloSports.Features.Cups.ListCupEditions;

/// <summary>
/// Every Cup edition of a save, newest first, plus the all-time team table of
/// each Cup. Built only from persisted selections, standings and rounds.
/// <see cref="ColorIndividualMedals"/> and <see cref="ColorCombinedMedals"/>
/// are additive Color Cup medal standings by color (MSS-081); the existing
/// <see cref="ColorTeams"/> table and its calculation are unchanged.
/// </summary>
public sealed record ListCupEditionsResponse(
    Guid SaveId,
    IReadOnlyList<ListCupEditionsResponse.Edition> Editions,
    IReadOnlyList<ListCupEditionsResponse.TeamSummary> ColorTeams,
    IReadOnlyList<ListCupEditionsResponse.TeamSummary> TypeTeams,
    IReadOnlyList<ListCupEditionsResponse.ColorMedalRow> ColorIndividualMedals,
    IReadOnlyList<ListCupEditionsResponse.ColorMedalRow> ColorCombinedMedals)
{
    /// <summary>
    /// <see cref="Podium"/> is empty until the team event has finished;
    /// <see cref="IndividualChampion"/> is Color Cup only and null until that event has finished.
    /// </summary>
    public sealed record Edition(
        string Cup,
        int SourceSeasonNumber,
        string State,
        int TeamCount,
        IReadOnlyList<PodiumTeam> Podium,
        IndividualChampion? IndividualChampion);

    public sealed record PodiumTeam(
        string TeamKey,
        string TeamName,
        int TeamRank,
        string Medal,
        int TeamScoreThousandths);

    public sealed record IndividualChampion(
        int AthleteId,
        string Name,
        string? ImageUrl,
        string TeamKey);

    /// <summary><see cref="BestRank"/> is null until the team has finished a Cup.</summary>
    public sealed record TeamSummary(
        string TeamKey,
        string TeamName,
        int Editions,
        int Gold,
        int Silver,
        int Bronze,
        int? BestRank,
        int LastSeasonNumber);

    /// <summary>
    /// All-time Color Cup medals for one color. Used for the additive
    /// Individual and Combined standings: one row per color, one medal per
    /// podium award (a team podium counts once per color, never per squad
    /// member). Sorted like <see cref="TeamSummary"/>: gold, then silver,
    /// then bronze, then team name.
    /// </summary>
    public sealed record ColorMedalRow(
        string TeamKey,
        string TeamName,
        int Gold,
        int Silver,
        int Bronze);
}
