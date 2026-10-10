namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

/// <summary>
/// One qualification team row. <see cref="Qualified"/> stays for backward
/// compatibility (true for guaranteed and successful wildcards). <see cref="QualificationStatus"/>
/// distinguishes honestly: "Guaranteed", "Wildcard", "WildcardCandidate" (pending
/// while qualifiers are incomplete), or "Eliminated".
/// </summary>
public sealed record TypeCupTournamentTeam(
    string CreatureType,
    int TeamRank,
    int TeamScoreThousandths,
    int TeamBaseThousandths,
    string Medal,
    bool Qualified,
    string QualificationStatus = "Eliminated");
