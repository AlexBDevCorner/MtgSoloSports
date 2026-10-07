namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

public sealed record TypeCupTournamentLeg(
    int SaveAthleteId,
    string AthleteName,
    string CreatureType,
    int SelectionRank,
    int GroupNumber,
    int GroupRank,
    int GroupScoreThousandths,
    int BaseScoreThousandths,
    bool Qualified);
