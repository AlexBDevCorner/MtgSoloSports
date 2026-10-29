namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record TypeCupQualificationTeamEntry(
    string CreatureType,
    int TeamRank,
    int TeamScoreThousandths,
    int TeamBaseThousandths,
    string Medal,
    bool Qualified,
    string QualificationStatus);
