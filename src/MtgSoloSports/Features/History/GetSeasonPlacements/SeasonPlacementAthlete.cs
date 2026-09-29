namespace MtgSoloSports.Features.History.GetSeasonPlacements;

/// <summary>
/// One season roster row for the placements matrix. The roster is the
/// season's <c>SeasonMembership</c> set (32 athletes) so the grid aligns to
/// the selected season/league even before any stage completes.
/// <c>CurrentEffectiveBonusThousandths</c> is the time-dependent career
/// effective bonus at read time, never the bonus that was active in earlier
/// stages.
/// </summary>
public sealed record SeasonPlacementAthlete(
    int AthleteId,
    string Name,
    int SportingColor,
    string SportingColorName,
    string? ImageUrl,
    int CurrentEffectiveBonusThousandths);
