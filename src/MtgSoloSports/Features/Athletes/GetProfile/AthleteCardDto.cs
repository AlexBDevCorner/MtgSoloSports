namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Current creature/sporting metadata copied at universe creation.
/// <c>TypeCupNationality</c> is the permanent Type Cup nationality (Game Rules
/// §15): null while uncapped, otherwise the creature type the athlete actually
/// represented in a Type Cup. Set atomically with Type Cup team participation
/// and never changed afterwards.
/// </summary>
public sealed record AthleteCardDto(
    string Name,
    int SportingColor,
    string SportingColorName,
    IReadOnlyList<string> CreatureTypes,
    string FrontColors,
    string ManaCost,
    string TypeLine,
    string? ImageUrl,
    string? SetCode,
    bool IsArtifact,
    bool HasDevoid,
    bool HasHybridMana,
    string? TypeCupNationality = null);
