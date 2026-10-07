namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

/// <summary>The fresh 32-team Final result (or direct Final for 1-32 fields).</summary>
public sealed record TypeCupTournamentFinalResult(
    int TeamCount,
    string Checksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    string ChampionCreatureType,
    IReadOnlyList<TypeCupTournamentTeam> Teams,
    IReadOnlyList<TypeCupTournamentLeg> Legs);
