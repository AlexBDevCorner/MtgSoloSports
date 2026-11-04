namespace MtgSoloSports.Features.Cups.GetChaosCupRecords;

/// <summary>
/// Versioned initial thresholds for Chaos Tournament Stories (MSS-117).
/// Kept small and explicit so calibration stays a one-spot change:
/// bumping <see cref="Version"/> plus the cutoff below never reinterprets
/// persisted history, because stories are recomputed read-only from completed
/// editions and never persisted as sporting state.
/// Conservative by design: a group outranking (even above a past champion)
/// is never a head-to-head duel. Only Final-stage achievement by a Pool or
/// Feeder 3 entrant can qualify as an underdog, and story output never
/// affects points, bonus, selection or advancement.
/// </summary>
public static class ChaosCupRecordThresholds
{
    public const string Version = "MSS-117-v1";

    /// <summary>Pool invitee Final rank that qualifies as a notable underdog (1..8).</summary>
    public const int PoolUnderdogMaxFinalRank = 8;

    /// <summary>Feeder 3 entrant Final rank that qualifies as a notable underdog (medal only).</summary>
    public const int Feeder3UnderdogMaxFinalRank = 3;

    /// <summary>Minimum all-time Chaos titles to surface a record-setting story.</summary>
    public const int RecordTitlesMinimum = 2;

    /// <summary>Minimum all-time Chaos finals to surface a record-setting story.</summary>
    public const int RecordFinalsMinimum = 2;

    /// <summary>Minimum P16-cutoff survivals to surface a repeated-survival record.</summary>
    public const int RepeatedP16Minimum = 2;
}
