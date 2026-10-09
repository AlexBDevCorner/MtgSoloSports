namespace MtgSoloSports.Features.Qualifiers.GetQualifierRounds;

/// <summary>
/// One persisted feeder qualifier round identity (no payload transfer).
/// The exact-round replay endpoint decompresses a single payload on demand.
/// </summary>
public sealed record QualifierRoundSummary(
    int RoundNumber,
    int RulesVersion,
    string PayloadChecksum);
