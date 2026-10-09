namespace MtgSoloSports.Features.Qualifiers.GetQualifierRounds;

/// <summary>
/// One athlete in a feeder qualifier field, derived from the just-completed
/// source standings (never resimulated). Role is provenance only
/// (Incumbent defends from the upper tier, Challenger attacks from the lower);
/// the top-8 cutoff decides who occupies the higher tier next season.
/// Artwork fields are display-only.
/// </summary>
public sealed record QualifierFieldMember(
    int AthleteId,
    string Name,
    string SportingColor,
    string Role,
    int FromLeagueId,
    string FromLeagueName,
    int FromSeasonRank,
    string? ImageUrl,
    string? SetCode,
    string TypeLine);
