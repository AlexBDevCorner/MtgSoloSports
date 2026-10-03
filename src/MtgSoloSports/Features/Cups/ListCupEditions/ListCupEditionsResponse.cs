namespace MtgSoloSports.Features.Cups.ListCupEditions;

/// <summary>
/// Every Cup edition of a save, newest first, plus the all-time team table of
/// each Cup. Built only from persisted selections, standings and rounds.
/// </summary>
public sealed record ListCupEditionsResponse(
    Guid SaveId,
    IReadOnlyList<ListCupEditionsResponse.Edition> Editions,
    IReadOnlyList<ListCupEditionsResponse.TeamSummary> ColorTeams,
    IReadOnlyList<ListCupEditionsResponse.TeamSummary> TypeTeams)
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
}
