namespace MtgSoloSports.Features.History.GetEventTeamStandings;

public sealed record HistoryEventTeamRow(string TeamName, int? Rank, int ScoreThousandths);
