namespace MtgSoloSports.Features.Leagues.InauguralDraw;

/// <summary>
/// Outcome of the Season 1 inaugural draw. <see cref="Entries"/> holds one entry
/// per save athlete (2,048) with per-color draw indices; <see cref="Checksum"/>
/// fingerprints the eight league rosters in draw order so identical draws can be
/// compared without resimulation.
/// </summary>
public sealed record InauguralDrawResult(
    IReadOnlyList<InauguralDrawEntry> Entries,
    string Checksum);
