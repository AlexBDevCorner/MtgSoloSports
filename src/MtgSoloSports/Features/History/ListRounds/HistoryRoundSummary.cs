namespace MtgSoloSports.Features.History.ListRounds;

/// <summary>
/// Normalized round summary for history navigation. Contains only round
/// identity plus the payload checksum; placements stay inside the immutable
/// compressed payload until the exact round is requested.
/// </summary>
public sealed record HistoryRoundSummary(
    int RoundNumber,
    int RulesVersion,
    string PayloadChecksum);
