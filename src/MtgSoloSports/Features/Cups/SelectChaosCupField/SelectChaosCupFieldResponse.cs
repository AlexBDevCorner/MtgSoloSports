namespace MtgSoloSports.Features.Cups.SelectChaosCupField;

/// <summary>
/// Persisted Chaos Cup field for one completed source season: exactly 1024 entrants
/// (all source-season league athletes plus uniform-random Pool invitations, 128 per
/// color) and the balanced 32x32 first-stage draw with exactly 4 of each color per
/// group. Built only from persisted edition, entrant and draw rows so replay never
/// resimulates.
/// </summary>
public sealed record SelectChaosCupFieldResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int RulesVersion,
    int ChaosFormatVersion,
    int FieldSize,
    int ActiveEntrants,
    int PoolInvites,
    IReadOnlyList<SelectChaosCupFieldResponse.PerColorQuota> PerColor,
    string EditionChecksum,
    string DrawChecksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    IReadOnlyList<SelectChaosCupFieldResponse.DrawGroup> Groups)
{
    public sealed record PerColorQuota(
        int SportingColor,
        string ColorName,
        int Active,
        int Invited);

    public sealed record DrawGroup(
        int GroupNumber,
        IReadOnlyList<int> AthleteIds);
}
