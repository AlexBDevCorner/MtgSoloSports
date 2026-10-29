namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record TypeCupWildcardEntry(
    string CreatureType,
    int QualificationGroup,
    int GroupRank,
    int TeamScoreThousandths,
    int TeamBaseThousandths,
    int GroupSize,
    long NormalizedNumerator,
    long NormalizedDenominator,
    bool TieDraw);
