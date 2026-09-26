namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Current creature/sporting metadata copied at universe creation.
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
    bool HasHybridMana);
