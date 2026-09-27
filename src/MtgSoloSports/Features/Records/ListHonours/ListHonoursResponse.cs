namespace MtgSoloSports.Features.Records.ListHonours;

/// <summary>
/// Immutable honours response: official league/Superleague championships
/// ordered by season then league name.
/// </summary>
public sealed record ListHonoursResponse(
    Guid SaveId,
    IReadOnlyList<HonourEntry> Honours);
