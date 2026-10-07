namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

/// <summary>One persisted qualification group result.</summary>
public sealed record TypeCupTournamentQualificationGroup(
    int QualificationGroup,
    int GroupSize,
    int FinalPlaces,
    string Checksum,
    IReadOnlyList<TypeCupTournamentTeam> Teams,
    IReadOnlyList<TypeCupTournamentLeg> Legs,
    IReadOnlyList<string> QualifiedTeams,
    IReadOnlyList<string> EliminatedTeams);
