namespace MtgSoloSports.Features.History.ListEventRounds;

public sealed record HistoryEventRoundSummary(int? Group, int Round, int? TournamentPhase = null, int? QualificationGroup = null);
