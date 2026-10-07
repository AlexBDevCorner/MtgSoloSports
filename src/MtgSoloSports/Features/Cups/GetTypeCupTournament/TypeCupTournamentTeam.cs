namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

public sealed record TypeCupTournamentTeam(
    string CreatureType,
    int TeamRank,
    int TeamScoreThousandths,
    int TeamBaseThousandths,
    string Medal,
    bool Qualified);
