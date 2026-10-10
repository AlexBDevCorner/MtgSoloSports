namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

/// <summary>
/// One persisted qualification group result (MSS-062, MSS-071).
/// <see cref="FinalPlaces"/> is the persisted per-group quota: fixed Final
/// places for v1, guaranteed places for v2 wildcard editions. <see cref="GuaranteedPlaces"/>
/// makes the v2 meaning explicit; <see cref="WildcardCandidate"/> is the
/// next-ranked team competing for a global wildcard (empty when the group has
/// no wildcards or standings are incomplete); <see cref="WildcardWinner"/> is
/// that candidate when it earned a wildcard place. <see cref="QualificationStatus"/>
/// per team distinguishes guaranteed, wildcard and eliminated honestly.
/// </summary>
public sealed record TypeCupTournamentQualificationGroup(
    int QualificationGroup,
    int GroupSize,
    int FinalPlaces,
    string Checksum,
    IReadOnlyList<TypeCupTournamentTeam> Teams,
    IReadOnlyList<TypeCupTournamentLeg> Legs,
    IReadOnlyList<string> QualifiedTeams,
    IReadOnlyList<string> EliminatedTeams,
    int GuaranteedPlaces = 0,
    string? WildcardCandidate = null,
    string? WildcardWinner = null);
